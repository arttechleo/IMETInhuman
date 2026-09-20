using UnityEngine;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// An open glass cylinder seen from inside: the rain booth the viewer stands in.
    ///
    /// Built in metres with its base at the local origin. u runs once around
    /// (seam at the back, -z), v from floor to top, so the rain shader treats
    /// it exactly like a pane that is 2 pi r wide and <c>height</c> tall. Normals
    /// and front faces point inward; tangents run along +u with the bitangent up,
    /// as the shader's tangent frame expects.
    /// </summary>
    public static class BoothMesh
    {
        public static Mesh Build(float radius, float height, int segments = 96)
        {
            segments = Mathf.Max(segments, 8);
            var columns = segments + 1;   // the seam column is duplicated for its uv
            var vertices = new Vector3[columns * 2];
            var normals = new Vector3[columns * 2];
            var tangents = new Vector4[columns * 2];
            var uvs = new Vector2[columns * 2];

            for (var i = 0; i < columns; i++)
            {
                var u = (float)i / segments;
                // Seam behind the viewer's starting heading.
                var angle = (u - 0.5f) * Mathf.PI * 2f;
                var outward = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                var along = new Vector3(Mathf.Cos(angle), 0f, -Mathf.Sin(angle));

                for (var j = 0; j < 2; j++)
                {
                    var k = i * 2 + j;
                    vertices[k] = outward * radius + Vector3.up * (j * height);
                    normals[k] = -outward;
                    // w = -1: cross(inward normal, +u) points down, and the
                    // bitangent has to point up the glass.
                    tangents[k] = new Vector4(along.x, along.y, along.z, -1f);
                    uvs[k] = new Vector2(u, j);
                }
            }

            // Clockwise as seen from inside, which is Unity's front face.
            var triangles = new int[segments * 6];
            for (var i = 0; i < segments; i++)
            {
                var b0 = i * 2;
                var t0 = b0 + 1;
                var b1 = b0 + 2;
                var t1 = b0 + 3;
                var n = i * 6;
                triangles[n] = b0; triangles[n + 1] = t0; triangles[n + 2] = b1;
                triangles[n + 3] = t0; triangles[n + 4] = t1; triangles[n + 5] = b1;
            }

            var mesh = new Mesh { name = "Rain Booth" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            // Bounds that enclose the viewer, so the booth is never culled
            // whichever way they face.
            mesh.bounds = new Bounds(Vector3.up * height * 0.5f, new Vector3(1f, 0f, 1f) * (radius * 2f) + Vector3.up * height);
            return mesh;
        }

        /// <summary>
        /// A glass disc closing the booth: the floor at y = 0 facing up, or the
        /// ceiling at <paramref name="y"/> facing down -- towards the viewer
        /// either way. uv is planar, 0..1 across the diameter, so the shader
        /// treats it as a square pane 2r on a side (its corners never exist).
        /// </summary>
        public static Mesh BuildCap(float radius, float y, bool ceiling, int segments = 96)
        {
            segments = Mathf.Max(segments, 8);
            var vertices = new Vector3[segments + 1];
            var uvs = new Vector2[segments + 1];
            var normals = new Vector3[segments + 1];
            var tangents = new Vector4[segments + 1];

            var normal = ceiling ? Vector3.down : Vector3.up;
            // Tangent +x, bitangent +z: cross(normal, +x) is -z facing up and +z
            // facing down, so the sign flips with the side.
            var tangent = new Vector4(1f, 0f, 0f, ceiling ? 1f : -1f);

            vertices[0] = new Vector3(0f, y, 0f);
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (var i = 0; i < segments; i++)
            {
                var angle = (float)i / segments * Mathf.PI * 2f;
                var p = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                vertices[i + 1] = p * radius + Vector3.up * y;
                uvs[i + 1] = new Vector2(p.x * 0.5f + 0.5f, p.z * 0.5f + 0.5f);
            }
            for (var i = 0; i <= segments; i++)
            {
                normals[i] = normal;
                tangents[i] = tangent;
            }

            // Fan; wound so the front face looks along the normal.
            var triangles = new int[segments * 3];
            for (var i = 0; i < segments; i++)
            {
                var a = i + 1;
                var b = (i + 1) % segments + 1;
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = ceiling ? b : a;
                triangles[i * 3 + 2] = ceiling ? a : b;
            }

            var mesh = new Mesh { name = ceiling ? "Rain Booth Ceiling" : "Rain Booth Floor" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
