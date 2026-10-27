using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>Authors four reusable shape targets on the original topology, without texture overlays.</summary>
public static class HeadBlendShapeBuilder
{
    public const string MeshPath = "Assets/Models/HeadMorph.asset";
    [MenuItem("Tools/Leaf/Rebuild Head BlendShapes")]
    public static void Build()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/head.fbx");
        if (prefab == null) throw new InvalidOperationException("Missing Assets/head.fbx");
        var instance = UnityEngine.Object.Instantiate(prefab);
        try
        {
            MeshFilter source = null;
            foreach (var filter in instance.GetComponentsInChildren<MeshFilter>())
                if (source == null || filter.GetComponent<Renderer>().bounds.size.y > source.GetComponent<Renderer>().bounds.size.y)
                    source = filter;
            if (source == null) throw new InvalidOperationException("Head has no MeshFilter");
            var bounds = source.GetComponent<Renderer>().bounds;
            var original = source.sharedMesh;
            var mesh = UnityEngine.Object.Instantiate(original);
            mesh.name = "Head Morph (Eye Spacing and Mouth Width)";
            mesh.ClearBlendShapes();
            var vertices = original.vertices;
            var baseNormals = original.normals;
            var normalMesh = UnityEngine.Object.Instantiate(original);
            var world = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) world[i] = source.transform.TransformPoint(vertices[i]);
            float width = bounds.size.x, height = bounds.size.y, depth = bounds.size.z;
            // Front-view anatomical regions for this Head. Masks taper to zero at the back and neck.
            float eyeY = bounds.min.y + height * .68f;
            float eyeX = width * .22f;
            float mouthY = bounds.min.y + height * .44f;
            foreach (string name in new[] { "EyeSpacingWide", "EyeSpacingNarrow", "MouthWide", "MouthNarrow" })
            {
                bool eye = name.StartsWith("EyeSpacing", StringComparison.Ordinal);
                float direction = name.EndsWith("Wide", StringComparison.Ordinal) ? 1 : -1;
                var delta = new Vector3[vertices.Length];
                for (int i = 0; i < world.Length; i++)
                {
                    Vector3 p = world[i]; float x = p.x - bounds.center.x;
                    float front = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(bounds.center.z, bounds.max.z - depth * .22f, p.z));
                    float movement;
                    if (eye)
                    {
                        float left = Region(x + eyeX, p.y - eyeY, width * .19f, height * .115f);
                        float right = Region(x - eyeX, p.y - eyeY, width * .19f, height * .115f);
                        movement = (right - left) * eyeX * .20f;
                    }
                    else
                        movement = x * .25f * Region(x, p.y - mouthY, width * .36f, height * .095f);
                    delta[i] = source.transform.InverseTransformVector(Vector3.right * (direction * front * movement));
                }
                var deformed = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++) deformed[i] = vertices[i] + delta[i];
                normalMesh.vertices = deformed; normalMesh.RecalculateNormals();
                var normals = normalMesh.normals;
                var deltaNormals = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++) deltaNormals[i] = delta[i].sqrMagnitude > 1e-18f ? normals[i] - baseNormals[i] : Vector3.zero;
                mesh.AddBlendShapeFrame(name, 100f, delta, deltaNormals, null);
            }
            UnityEngine.Object.DestroyImmediate(normalMesh);
            mesh.RecalculateBounds();
            if (!AssetDatabase.IsValidFolder("Assets/Models")) AssetDatabase.CreateFolder("Assets", "Models");
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (existing == null) AssetDatabase.CreateAsset(mesh, MeshPath);
            else { EditorUtility.CopySerialized(mesh, existing); UnityEngine.Object.DestroyImmediate(mesh); EditorUtility.SetDirty(existing); }
            AssetDatabase.SaveAssetIfDirty(existing == null ? mesh : existing);
            Debug.Log("Head BlendShapes built: " + vertices.Length + " vertices, four targets; source topology and materials preserved.");
        }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
    }

    private static float Region(float x, float y, float radiusX, float radiusY)
    {
        // Compact, smooth support keeps the neck, cranium and opposite feature untouched.
        float radius = Mathf.Sqrt(x * x / (radiusX * radiusX) + y * y / (radiusY * radiusY));
        return 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.5f, 1, radius));
    }
}
