using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace VRMDebugDraw
{
    /// job to combine and deduplicate the polygon indices
    /// owns its output structure
    public struct MeshGenerationJob : IJobParallelForTransform, IDisposable
    {
        NativeArray<float3> vertices;
        public NativeArray<float3> Vertices
        {
            get => vertices;
        }

        NativeArray<float3> normals;
        public NativeArray<float3> Normals
        {
            get => normals;
        }

        NativeArray<float2> uvs;
        public NativeArray<float2> Uvs
        {
            get => uvs;
        }

        NativeArray<BoneWeight> boneWeights;
        public NativeArray<BoneWeight> BoneWeights
        {
            get => boneWeights;
        }

        // quads => int4
        NativeArray<int4> indices;
        public NativeArray<int4> Indices
        {
            get => indices;
        }

        NativeArray<float4x4> bindPoses;
        public NativeArray<float4x4> BindPoses
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

                    int currentOffset = baseOffset + QuadCount(lateral, radial, 1);

                    // note: xzy swizzle required, but float3(float2, float) makes this easier to read
                    // note: then translate by lateral factor, here z * axis
                    vertices[currentOffset + 0] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[nextRadial], lateralPositions[lateral]),
                        axis
                    );
                    vertices[currentOffset + 1] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[radial], lateralPositions[lateral]),
                        axis
                    );
                    vertices[currentOffset + 2] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[radial], lateralPositions[nextLateral]),
                        axis
                    );
                    vertices[currentOffset + 3] = OffsetSwizzleAndTransformVertexToRootSpace(
                        mLocalToRoot,
                        math.float3(radius * radialPositions[nextRadial], lateralPositions[nextLateral]),
                        axis
                    );

                    // note: xzy swizzle required as well
                    // note: normalization required as well
                    normals[currentOffset + 0] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[nextRadial], 0.0f)
                    );
                    normals[currentOffset + 1] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[radial], 0.0f)
                    );
                    normals[currentOffset + 2] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[radial], 0.0f)
                    );
                    normals[currentOffset + 3] = SwizzleAndTransformNormalToRootSpace(
                        mLocalToRoot3x3,
                        math.float3(radialPositions[nextRadial], 0.0f)
                    );

                    uvs[currentOffset + 0] = math.float2(
                        (float)(radial + 1) / (float)radialSubdivisions,
                        (float)lateral / (float)lateralSubdivisions
                    );
                    uvs[currentOffset + 1] = math.float2(
                        (float)(radial) / (float)radialSubdivisions,
                        (float)lateral / (float)lateralSubdivisions
                    );
                    uvs[currentOffset + 2] = math.float2(
                        (float)(radial) / (float)radialSubdivisions,
                        (float)(lateral + 1) / (float)lateralSubdivisions
                    );
                    uvs[currentOffset + 3] = math.float2(
                        (float)(radial + 1) / (float)radialSubdivisions,
                        (float)(lateral + 1) / (float)lateralSubdivisions
                    );

                    boneWeights[currentOffset + 0] = new BoneWeight()
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
                    boneWeights[currentOffset + 1] = new BoneWeight()
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
                    boneWeights[currentOffset + 2] = new BoneWeight()
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
                    boneWeights[currentOffset + 3] = new BoneWeight()
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

                    indices[currentOffset] = math.int4( //
                        currentOffset + 0,
                        currentOffset + 1,
                        currentOffset + 2,
                        currentOffset + 3
                    );
                }
            }
        }

        public void Dispose()
        {
            vertices.Dispose();
            normals.Dispose();
            uvs.Dispose();
            boneWeights.Dispose();
            indices.Dispose();
            bindPoses.Dispose();
            parentPositions.Dispose();

            lateralPositions.Dispose();
            radialPositions.Dispose();
        }
    }
}
