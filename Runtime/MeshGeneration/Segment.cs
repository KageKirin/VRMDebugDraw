using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Jobs;

namespace VRMDebugDraw.MeshGeneration
{
    /// <summary>
    /// contains data for 1 mesh segment
    /// </summary>
    public struct Segment : IDisposable
    {
        public NativeArray<float3> vertices;
        public NativeArray<float3> normals;
        public NativeArray<float2> texcoords;
        public NativeArray<float4> colors;
        public NativeArray<int4> indices;

        public void Dispose()
        {
            vertices.Dispose();
            normals.Dispose();
            texcoords.Dispose();
            colors.Dispose();
            indices.Dispose();
        }
    }
}
