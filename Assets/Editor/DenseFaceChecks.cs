using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Cross-language geometry regression using independently exported Python reference vertices.</summary>
public static class DenseFaceChecks
{
    [MenuItem("Tools/Leaf/Validate 3DDFA Face Mapping")]
    public static void Run()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string directory = Path.Combine(root, "Tools/FaceFusion/3ddfa-data");
        string path = Path.Combine(root, DenseFaceModel.RelativePath);
        if (!File.Exists(path)) throw new InvalidOperationException("Run Tools/FaceFusion/prepare_3ddfa.py before validation.");
        int count; float front = float.NegativeInfinity;
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            reader.BaseStream.Position = 8; count = reader.ReadInt32(); reader.BaseStream.Position = 52;
            for (int i = 0; i < count; i++) { reader.ReadSingle(); reader.ReadSingle(); front = Mathf.Max(front, reader.ReadSingle()); }
        }
        // Choose reference bounds so the loader's placement is the identity transform.
        float height = 1 / .76f;
        var bounds = new Bounds(new Vector3(0, -.10f * height, front - .5f), new Vector3(1, height, 1));
        var holder = new GameObject("Temporary 3DDFA geometry validation") { hideFlags = HideFlags.HideAndDontSave };
        Mesh mesh = null, baked = new Mesh();
        try
        {
            string id; mesh = DenseFaceModel.Load(path, holder.transform, bounds, out id);
            Require(mesh.blendShapeCount == 80, "Expected 80 paired identity BlendShapes.");
            float[] shape = File.ReadAllLines(Path.Combine(directory, "validation_shape.txt"))
                .Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            float[] weights = DenseFaceModel.Weights(shape, 1);
            var renderer = holder.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
            for (int i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
            renderer.BakeMesh(baked); var vertices = baked.vertices; float maxError = 0;
            using (var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "validation_face.bytes"))))
            {
                Require(reader.BaseStream.Length == vertices.Length * 12L, "Unexpected reference geometry length.");
                for (int i = 0; i < vertices.Length; i++)
                {
                    var expected = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    maxError = Mathf.Max(maxError, Vector3.Distance(vertices[i], expected));
                }
            }
            Require(maxError < .00001f, "Python/Unity face geometry mismatch: " + maxError);
            for (int i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, 0);
            renderer.BakeMesh(baked); vertices = baked.vertices; var original = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                Require(Vector3.Distance(vertices[i], original[i]) < .000001f, "Neutral reset failed.");
            bool rejected = false;
            try { DenseFaceModel.Weights(new float[39], 1); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Invalid coefficient count was accepted.");
            Debug.Log("3DDFA mapping passed: " + count + " vertices, 80 BlendShapes, max error=" + maxError + "; model=" + id);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(holder); UnityEngine.Object.DestroyImmediate(baked);
            if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
