using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Reads the locally prepared, licensed BFM template and builds matched identity BlendShapes.</summary>
public static class DenseFaceModel
{
    public const int ParameterCount = 40;
    public const float CoefficientLimit = 3f;
    public const string RelativePath = "Tools/FaceFusion/3ddfa-data/unity_face.bytes";

    public static Mesh Load(string path, Transform head, Bounds reference, out string modelId)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "L3DFACE1") throw new InvalidDataException("Invalid 3DDFA template header.");
            int count = reader.ReadInt32(), indexCount = reader.ReadInt32(), dimensions = reader.ReadInt32();
            if (count < 100 || count > 100000 || indexCount < 300 || indexCount > 1000000 || indexCount % 3 != 0 || dimensions != ParameterCount)
                throw new InvalidDataException("Invalid 3DDFA template dimensions.");
            byte[] signature = reader.ReadBytes(32);
            if (signature.Length != 32) throw new EndOfStreamException();
            modelId = BitConverter.ToString(signature).Replace("-", "").ToLowerInvariant();
            long expected = 8 + 12 + 32 + count * 12L * (1 + dimensions) + indexCount * 4L;
            if (reader.BaseStream.Length != expected) throw new InvalidDataException("Incomplete 3DDFA template.");
            var normalized = new Vector3[count]; float front = float.NegativeInfinity;
            for (int i = 0; i < count; i++) { normalized[i] = ReadVector(reader); front = Mathf.Max(front, normalized[i].z); }
            var triangles = new int[indexCount];
            for (int i = 0; i < triangles.Length; i++)
            { triangles[i] = reader.ReadInt32(); if (triangles[i] < 0 || triangles[i] >= count) throw new InvalidDataException("Invalid template triangle."); }
            float scale = reference.size.y * .76f;
            Vector3 center = new Vector3(reference.center.x, reference.min.y + reference.size.y * .60f, reference.max.z - front * scale);
            var vertices = new Vector3[count];
            for (int i = 0; i < count; i++) vertices[i] = head.InverseTransformPoint(center + normalized[i] * scale);
            var mesh = new Mesh { name = "3DDFA matched BFM identity face", indexFormat = IndexFormat.UInt32 };
            Mesh scratch = null;
            try
            {
                mesh.vertices = vertices; mesh.triangles = triangles; mesh.RecalculateNormals();
                var baseNormals = mesh.normals;
                scratch = UnityEngine.Object.Instantiate(mesh);
                for (int k = 0; k < dimensions; k++)
                {
                    var positive = new Vector3[count];
                    for (int i = 0; i < count; i++) positive[i] = head.InverseTransformVector(ReadVector(reader) * scale);
                    for (int sign = 1; sign >= -1; sign -= 2)
                    {
                        var delta = new Vector3[count]; var changed = new Vector3[count];
                        for (int i = 0; i < count; i++) { delta[i] = positive[i] * sign; changed[i] = vertices[i] + delta[i]; }
                        scratch.vertices = changed; scratch.RecalculateNormals(); var normals = scratch.normals;
                        for (int i = 0; i < count; i++) normals[i] -= baseNormals[i];
                        mesh.AddBlendShapeFrame("Identity_" + k.ToString("D2") + (sign > 0 ? "_Positive" : "_Negative"), 100, delta, normals, null);
                    }
                }
                mesh.RecalculateBounds(); return mesh;
            }
            catch { DestroyOwned(mesh); throw; }
            finally { if (scratch != null) DestroyOwned(scratch); }
        }
    }

    private static void DestroyOwned(UnityEngine.Object value)
    {
        if (Application.isPlaying) UnityEngine.Object.Destroy(value);
        else UnityEngine.Object.DestroyImmediate(value);
    }

    public static float[] Weights(float[] coefficients, float strength)
    {
        if (coefficients == null || coefficients.Length != ParameterCount) throw new ArgumentException("Expected 40 3DDFA shape coefficients.");
        var weights = new float[ParameterCount * 2];
        for (int i = 0; i < coefficients.Length; i++)
        {
            float value = coefficients[i];
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Invalid 3DDFA shape coefficient.");
            value = Mathf.Clamp(value / CoefficientLimit, -1, 1) * 100 * Mathf.Clamp01(strength);
            weights[i * 2] = Mathf.Max(value, 0); weights[i * 2 + 1] = Mathf.Max(-value, 0);
        }
        return weights;
    }
    private static Vector3 ReadVector(BinaryReader reader)
    {
        var value = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (float.IsNaN(value.sqrMagnitude) || float.IsInfinity(value.sqrMagnitude)) throw new InvalidDataException("Nonfinite template data.");
        return value;
    }
}
