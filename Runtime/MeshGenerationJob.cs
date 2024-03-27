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
        MeshGeneration.Segment segment;

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

        public MeshGenerationJob(float4x4 mWorldToRootSpace, MeshGeneration.Segment segment, Transform[] transforms)
        {
            this.mWorldToRootSpace = mWorldToRootSpace;
            this.segment = segment;

            Debug.Log($"{transforms.Length} transforms");

            Assert.AreEqual(segment.vertices.Length, segment.normals.Length);
            Assert.AreEqual(segment.vertices.Length, segment.texcoords.Length);
            Assert.AreEqual(segment.vertices.Length, segment.colors.Length);

            vertices = new NativeArray<float3>(segment.vertices.Length * transforms.Length, Allocator.TempJob);
            normals = new NativeArray<float3>(segment.normals.Length * transforms.Length, Allocator.TempJob);
            texcoords = new NativeArray<float2>(segment.texcoords.Length * transforms.Length, Allocator.TempJob);
            colors = new NativeArray<float4>(segment.colors.Length * transforms.Length, Allocator.TempJob);
            boneWeights = new NativeArray<BoneWeight>(segment.vertices.Length * transforms.Length, Allocator.TempJob);
            indices = new NativeArray<int4>(segment.indices.Length * transforms.Length, Allocator.TempJob);
            bindPoses = new NativeArray<float4x4>(transforms.Length, Allocator.TempJob);

            parentPositions = new NativeArray<float3>(transforms.Length, Allocator.TempJob);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                parentPositions[i] = transform.parent != null ? transform.parent.position : math.float3(0, math.EPSILON, 0);
            }

            slices = new NativeArray<SOASlice>(transforms.Length, Allocator.TempJob);
            for (int i = 0; i < transforms.Length; i++)
            {
                var _vertices = new NativeSlice<float3>(this.vertices, segment.vertices.Length * i, segment.vertices.Length);
                var _normals = new NativeSlice<float3>(this.normals, segment.normals.Length * i, segment.normals.Length);
                var _texcoords = new NativeSlice<float2>(this.texcoords, segment.texcoords.Length * i, segment.texcoords.Length);
                var _colors = new NativeSlice<float4>(this.colors, segment.colors.Length * i, segment.colors.Length);
                var _boneWeights = new NativeSlice<BoneWeight>(
                    this.boneWeights,
                    segment.vertices.Length * i,
                    segment.vertices.Length
                );
                var _indices = new NativeSlice<int4>(this.indices, segment.indices.Length * i, segment.indices.Length);

                slices[i] = new SOASlice()
                {
                    vertices = _vertices,
                    normals = _normals,
                    texcoords = _texcoords,
                    colors = _colors,
                    boneWeights = _boneWeights,
                    indices = _indices,
                };
            }
        }

        [BurstCompile]
        public void Execute(int transformIndex, TransformAccess transform)
        {
            float4x4 mWorldToLocal = transform.worldToLocalMatrix;
            float4x4 mLocalToRoot = math.mul(mWorldToRootSpace, transform.localToWorldMatrix);
            float3x3 mLocalToRoot3x3 = math.float3x3(mLocalToRoot);

            float3 localPositionInLocalSpace = math.float3(0, 0, 0);
            float3 parentPositionInLocalSpace = math.mul(mWorldToLocal, math.float4(parentPositions[transformIndex], 1.0f)).xyz;
            float3 axis = parentPositionInLocalSpace - localPositionInLocalSpace; //or basically parentPositionInLocalSpace

            BoneWeight bw =
                new()
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

            // assign transformed vertices/normals
            // copy texcoords, colors
            // assign boneweights
            for (int i = 0; i < segment.vertices.Length; i++)
            {
                vertices[i] = OffsetSwizzleAndTransformVertexToRootSpace(mLocalToRoot, segment.vertices[i], axis);
                normals[i] = SwizzleAndTransformNormalToRootSpace(mLocalToRoot3x3, segment.normals[i]);
                texcoords[i] = segment.texcoords[i];
                colors[i] = segment.colors[i];
                boneWeights[i] = bw;
            }

            bindPoses[transformIndex] = (float4x4)transform.worldToLocalMatrix * mWorldToRootSpace;

            // assign offset indices
            for (int i = 0; i < segment.indices.Length; i++)
            {
                indices[i] = segment.indices.Length * transformIndex + i;
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
        }
    }
}
