using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;

namespace VRMDebugDraw.MeshGeneration
{
    public class CylinderSegmentFactory : ISegmentFactory, IDisposable
    {
        readonly float Radius;
        readonly float Length;

        readonly int LateralSubdivisions;
        readonly int RadialSubdivisions;

        static float ComputeTauBySubdivisions(int subdivisions) => 2 * math.PI / (float)subdivisions;

        public CylinderSegmentFactory(float radius, float length, int lateralSubdivisions, int radialSubdivisions)
        {
            Radius = radius;
            Length = length;
            LateralSubdivisions = lateralSubdivisions;
            RadialSubdivisions = radialSubdivisions;
        }

        public virtual Segment GenerateSegment()
        {
            float tauBySubdiv = ComputeTauBySubdivisions(RadialSubdivisions);
            float lengthBySubdiv = Length / (float)LateralSubdivisions;

            List<float3> positions = new();
            List<float3> normals = new();
            List<float2> texcoords = new();
            List<float4> colors = new();
            List<int4> indices = new();

            // no need to go full circle, hence `<`
            for (int r = 0; r < RadialSubdivisions; r++)
            {
                float angleR = tauBySubdiv * (float)r;
                float2 radialNormal = math.float2(math.sin(angleR), math.cos(angleR));
                float2 radialPosition = radialNormal * Radius;

                // need to go to Length/(lengthBySubdiv*LateralSubdivisions) = 1, hence `<=`
                for (int l = 0; l <= LateralSubdivisions; l++)
                {
                    positions.Add(math.float3(radialPosition.x, lengthBySubdiv * l, radialPosition.y));
                    normals.Add(math.float3(radialNormal.x, 0, radialNormal.y));
                    texcoords.Add(math.float2((float)r % 2, (float)l % 2));
                    colors.Add(math.float4((float)r % 2, (float)l % 2, (float)(r + 1) % 2, (float)(l + 1) % 2));
                }
            }

            // generating points to go full circle, hence `<=` and `%` below
            for (int r = 0; r <= RadialSubdivisions; r++)
            {
                int r0 = r % RadialSubdivisions;
                int r1 = (r + 1) % RadialSubdivisions;

                // generating points to go to LateralSubdivisions+1, hence `<`
                for (int l = 0; l < LateralSubdivisions; l++)
                {
                    int l0 = l;
                    int l1 = l + 1;

                    //  quad formed by points ABCD
                    //
                    //  A        B
                    //  +--------+
                    //  |        |
                    //  |        |
                    //  |        |
                    //  +--------+
                    //  C        D
                    //

                    int A = r0 * LateralSubdivisions + l0;
                    int B = r1 * LateralSubdivisions + l0;
                    int C = r0 * LateralSubdivisions + l1;
                    int D = r1 * LateralSubdivisions + l1;

                    indices.Add(math.int4(B, A, C, D)); // CCW order
                }
            }

            return new Segment()
            {
                vertices = positions.ToNativeArray(Allocator.TempJob),
                normals = normals.ToNativeArray(Allocator.TempJob),
                texcoords = texcoords.ToNativeArray(Allocator.TempJob),
                colors = colors.ToNativeArray(Allocator.TempJob),
                indices = indices.ToNativeArray(Allocator.TempJob),
            };
        }

        public virtual void Dispose() { }
    }
}
