using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Jobs;

namespace VRMDebugDraw
{
    /// job to combine and deduplicate the polygon indices
    /// owns its output structure
    public struct MeshGenerationJob : IJobParallelForTransform, IDisposable
    {
        struct SOASlice
        {
            public NativeSlice<float3> vertices;
            public NativeSlice<float3> normals;
            public NativeSlice<float2> texcoords;
            public NativeSlice<float4> colors;
            public NativeSlice<BoneWeight> boneWeights;
            public NativeSlice<int4> indices;
        }

        NativeArray<SOASlice> slices;

        NativeArray<float3> vertices;
        public readonly NativeArray<float3> Vertices
        {
            get => vertices;
        }

        NativeArray<float3> normals;
        public readonly NativeArray<float3> Normals
        {
            get => normals;
        }

        NativeArray<float2> texcoords;
        public readonly NativeArray<float2> TexCoords
        {
            get => texcoords;
        }

        NativeArray<float4> colors;
        public readonly NativeArray<float4> Colors
        {
            get => colors;
        }

        NativeArray<BoneWeight> boneWeights;
        public readonly NativeArray<BoneWeight> BoneWeights
        {
            get => boneWeights;
        }

        // quads => int4
        NativeArray<int4> indices;
        public readonly NativeArray<int4> Indices
        {
            get => indices;
        }

        NativeArray<float4x4> bindPoses;
        public readonly NativeArray<float4x4> BindPoses
        {
            get => bindPoses;
        }

        [ReadOnly]
        NativeArray<float3> parentPositions;

        [ReadOnly]
        float4x4 mWorldToRootSpace;

        [ReadOnly]
        float radius;

        [ReadOnly]
        int lateralSubdivisions;

        [ReadOnly]
        int radialSubdivisions;

        [ReadOnly]
        NativeArray<float> lateralPositions;

        [ReadOnly]
        NativeArray<float2> radialPositions;

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int QuadCount(int lateralSubdivisions, int radialSubdivisions, int offset) =>
            lateralSubdivisions * radialSubdivisions * 4 * offset;

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ComputeTauBySubdivisions(int subdivisions) => 2 * math.PI / (float)subdivisions;

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float2[] ComputeRadialPositions(int subdivisions)
        {
            float tauBySubdiv = ComputeTauBySubdivisions(subdivisions);
            float2[] positions = new float2[subdivisions];
            for (int i = 0; i < subdivisions; i++)
            {
                float angleI = tauBySubdiv * (float)i;
                positions[i] = math.float2(math.sin(angleI), math.cos(angleI));
            }
            return positions;
        }

        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float[] ComputeLateralPositions(int subdivisions)
        {
            float[] positions = new float[subdivisions + 1];
            for (int i = 0; i < subdivisions + 1; i++)
            {
                positions[i] = (float)i / (float)subdivisions;
            }
            return positions;
        }

        // offset, swizzle and transform quadVertices to rootSpace
        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float3 OffsetSwizzleAndTransformVertexToRootSpace(float4x4 mLocalToRoot, float3 vertex, in float3 axis)
        {
            float4 vertex4 = math.float4(vertex.z * axis + math.float3(vertex.x, 0, vertex.y), 1.0f);
            return math.mul(mLocalToRoot, vertex4).xyz;
        }

        // swizzle and transform quadNormals to rootSpace
        [BurstCompile, MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float3 SwizzleAndTransformNormalToRootSpace(float3x3 mLocalToRoot3x3, float3 normal)
        {
            return math.mul(mLocalToRoot3x3, math.normalize(normal.xzy));
        }

        public MeshGenerationJob(
            float4x4 mWorldToRootSpace,
            float radius,
            int lateralSubdivisions,
            int radialSubdivisions,
            Transform[] transforms
        )
        {
            this.mWorldToRootSpace = mWorldToRootSpace;
            this.radius = radius;
            this.lateralSubdivisions = lateralSubdivisions;
            this.radialSubdivisions = radialSubdivisions;
            lateralPositions = new NativeArray<float>(ComputeLateralPositions(lateralSubdivisions), Allocator.TempJob);
            radialPositions = new NativeArray<float2>(ComputeRadialPositions(radialSubdivisions), Allocator.TempJob);

            int quadCount = QuadCount(lateralSubdivisions, radialSubdivisions, transforms.Length);
            vertices = new NativeArray<float3>(quadCount, Allocator.TempJob);
            normals = new NativeArray<float3>(quadCount, Allocator.TempJob);
            uvs = new NativeArray<float2>(quadCount, Allocator.TempJob);
            boneWeights = new NativeArray<BoneWeight>(quadCount, Allocator.TempJob);
            indices = new NativeArray<int4>(quadCount / 4, Allocator.TempJob);
            bindPoses = new NativeArray<float4x4>(transforms.Length, Allocator.TempJob);

            parentPositions = new NativeArray<float3>(transforms.Length, Allocator.TempJob);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                parentPositions[i] = transform.parent != null ? transform.parent.position : math.float3(0, math.EPSILON, 0);
            }

            slices = new NativeArray<SOASlice>(transforms.Length, Allocator.TempJob);
            int sliceLength = QuadCount(lateralSubdivisions, radialSubdivisions, 1);
            Debug.Log(
                $"allocating {transforms.Length} slices with {sliceLength} elements each ({transforms.Length * sliceLength} elements total) "
            );
            for (int i = 0; i < transforms.Length; i++)
            {
                int baseOffset = QuadCount(lateralSubdivisions, radialSubdivisions, i);
                Assert.IsTrue(baseOffset < quadCount);
                Assert.IsTrue(baseOffset + sliceLength <= quadCount);
                slices[i] = new SOASlice()
                {
                    vertices = new NativeSlice<float3>(this.vertices, baseOffset, sliceLength),
                    normals = new NativeSlice<float3>(this.normals, baseOffset, sliceLength),
                    uvs = new NativeSlice<float2>(this.uvs, baseOffset, sliceLength),
                    boneWeights = new NativeSlice<BoneWeight>(this.boneWeights, baseOffset, sliceLength),
                    indices = new NativeSlice<int4>(this.indices, baseOffset, sliceLength),
                };
            }
        }

        [BurstCompile]
        public void Execute(int transformIndex, TransformAccess transform)
        {
            int baseOffset = QuadCount(lateralSubdivisions, radialSubdivisions, transformIndex);
            float4x4 mWorldToLocal = transform.worldToLocalMatrix;
            float4x4 mLocalToRoot = math.mul(mWorldToRootSpace, transform.localToWorldMatrix);
            float3x3 mLocalToRoot3x3 = math.float3x3(mLocalToRoot);

            float3 localPositionInLocalSpace = math.float3(0, 0, 0);
            float3 parentPositionInLocalSpace = math.mul(mWorldToLocal, math.float4(parentPositions[transformIndex], 1.0f)).xyz;
            float3 axis = parentPositionInLocalSpace - localPositionInLocalSpace; //or basically parentPositionInLocalSpace

            bindPoses[transformIndex] = (float4x4)transform.worldToLocalMatrix * mWorldToRootSpace;

            for (int lateral = 0; lateral < lateralSubdivisions; lateral++)
            {
                var nextLateral = lateral + 1; //< safe since we have lateralSubdivisions+1 values

                for (int radial = 0; radial < radialSubdivisions; radial++)
                {
                    var nextRadial = (radial + 1) % radialSubdivisions;

                    int currentOffset = QuadCount(lateral, radial, 1);

                    // note: xzy swizzle required, but float3(float2, float) makes this easier to read
                    // note: then translate by lateral factor, here z * axis
                    var currentSlice = slices[transformIndex];
                    currentSlice.vertices[currentOffset + 0] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[nextRadial], lateralPositions[lateral]),
                        axis
                    );
                    currentSlice.vertices[currentOffset + 1] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[radial], lateralPositions[lateral]),
                        axis
                    );
                    currentSlice.vertices[currentOffset + 2] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[radial], lateralPositions[nextLateral]),
                        axis
                    );
                    currentSlice.vertices[currentOffset + 3] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[nextRadial], lateralPositions[nextLateral]),
                        axis
                    );

                    // note: xzy swizzle required as well
                    // note: normalization required as well
                    currentSlice.normals[currentOffset + 0] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[nextRadial], 0.0f)
                    );
                    currentSlice.normals[currentOffset + 1] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[radial], 0.0f)
                    );
                    currentSlice.normals[currentOffset + 2] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[radial], 0.0f)
                    );
                    currentSlice.normals[currentOffset + 3] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[nextRadial], 0.0f)
                    );

                    currentSlice.uvs[currentOffset + 0] = math.float2(
                        (float)(radial + 1) / (float)radialSubdivisions,
                        (float)lateral / (float)lateralSubdivisions
                    );
                    currentSlice.uvs[currentOffset + 1] = math.float2(
                        (float)(radial) / (float)radialSubdivisions,
                        (float)lateral / (float)lateralSubdivisions
                    );
                    currentSlice.uvs[currentOffset + 2] = math.float2(
                        (float)(radial) / (float)radialSubdivisions,
                        (float)(lateral + 1) / (float)lateralSubdivisions
                    );
                    currentSlice.uvs[currentOffset + 3] = math.float2(
                        (float)(radial + 1) / (float)radialSubdivisions,
                        (float)(lateral + 1) / (float)lateralSubdivisions
                    );

                    currentSlice.boneWeights[currentOffset + 0] = new BoneWeight()
                    {
                        boneIndex0 = transformIndex,
                        boneIndex1 = 0,
                        boneIndex2 = 0,
                        boneIndex3 = 0,
                        weight0 = 1,
                        weight1 = 0,
                        weight2 = 0,
                        weight3 = 0,
                    };
                    currentSlice.boneWeights[currentOffset + 1] = new BoneWeight()
                    {
                        boneIndex0 = transformIndex,
                        boneIndex1 = 0,
                        boneIndex2 = 0,
                        boneIndex3 = 0,
                        weight0 = 1,
                        weight1 = 0,
                        weight2 = 0,
                        weight3 = 0,
                    };
                    currentSlice.boneWeights[currentOffset + 2] = new BoneWeight()
                    {
                        boneIndex0 = transformIndex,
                        boneIndex1 = 0,
                        boneIndex2 = 0,
                        boneIndex3 = 0,
                        weight0 = 1,
                        weight1 = 0,
                        weight2 = 0,
                        weight3 = 0,
                    };
                    currentSlice.boneWeights[currentOffset + 3] = new BoneWeight()
                    {
                        boneIndex0 = transformIndex,
                        boneIndex1 = 0,
                        boneIndex2 = 0,
                        boneIndex3 = 0,
                        weight0 = 1,
                        weight1 = 0,
                        weight2 = 0,
                        weight3 = 0,
                    };

                    currentSlice.indices[currentOffset] = math.int4(
                        baseOffset + currentOffset + 0,
                        baseOffset + currentOffset + 1,
                        baseOffset + currentOffset + 2,
                        baseOffset + currentOffset + 3
                    );
                }
            }
        }

        public void Dispose()
        {
            vertices.Dispose();
            normals.Dispose();
            texcoords.Dispose();
            colors.Dispose();
            boneWeights.Dispose();
            indices.Dispose();
            bindPoses.Dispose();
            parentPositions.Dispose();

            lateralPositions.Dispose();
            radialPositions.Dispose();
        }
    }
}
