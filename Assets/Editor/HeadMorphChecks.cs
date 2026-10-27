using System;
using UnityEditor;
using UnityEngine;

/// <summary>Asset-level regression checks; creates no scene objects that persist.</summary>
public static class HeadMorphChecks
{
    [MenuItem("Tools/Leaf/Validate Head BlendShapes")]
    public static void Run()
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(HeadBlendShapeBuilder.MeshPath);
        Require(mesh != null && mesh.blendShapeCount == 4, "Expected an authored mesh with four shapes.");
        var neutral = HeadMorphWeights.Calculate(.52f, .42f, .52f, .42f, 75);
        Require(neutral.sqrMagnitude < .00001f, "Reference proportions must preserve the base shape.");
        var wide = HeadMorphWeights.Calculate(.8f, .8f, .52f, .42f, 75);
        var narrow = HeadMorphWeights.Calculate(.2f, .2f, .52f, .42f, 75);
        Require(wide == new Vector4(75, 0, 75, 0) && narrow == new Vector4(0, 75, 0, 75),
            "Paired directions or weight clamping failed.");
        var go = new GameObject("Temporary BlendShape validation") { hideFlags = HideFlags.HideAndDontSave };
        var baseline = new Mesh(); var deformed = new Mesh();
        try
        {
            var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
            renderer.BakeMesh(baseline); var original = baseline.vertices;
            var baseVertices = mesh.vertices;
            for (int k = 0; k < 4; k++)
            {
                var delta = new Vector3[mesh.vertexCount]; var opposite = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(k, 0, delta, null, null);
                mesh.GetBlendShapeFrameVertices(k ^ 1, 0, opposite, null, null);
                int changed = 0;
                for (int i = 0; i < delta.Length; i++)
                {
                    if (delta[i].sqrMagnitude > 1e-16f) changed++;
                    Require((delta[i] + opposite[i]).sqrMagnitude < 1e-15f, "Wide/narrow shapes are not complementary.");
                    // This FBX stores height on local Z; its renderer rotates that axis to Unity world Y.
                    if (baseVertices[i].z < mesh.bounds.min.z + mesh.bounds.size.z * .25f)
                        Require(delta[i].sqrMagnitude < 1e-16f, "Neck/base vertices must stay fixed.");
                }
                Require(changed > 50 && changed < mesh.vertexCount * .6f, "Invalid facial influence region.");
                renderer.SetBlendShapeWeight(k, 100); renderer.BakeMesh(deformed);
                var moved = deformed.vertices; float displacement = 0;
                for (int i = 0; i < moved.Length; i++) displacement += Vector3.Distance(moved[i], original[i]);
                Require(displacement > 1e-5f, "BlendShape failed to deform the rendered mesh.");
                renderer.SetBlendShapeWeight(k, 0); renderer.BakeMesh(deformed); moved = deformed.vertices;
                for (int i = 0; i < moved.Length; i++)
                    Require(Vector3.Distance(moved[i], original[i]) < 1e-7f, "Reset failed to restore the original geometry.");
                Debug.Log("Validated " + mesh.GetBlendShapeName(k) + ": " + changed + " affected vertices; bake/reset and neck checks passed.");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(baseline);
            UnityEngine.Object.DestroyImmediate(deformed);
        }
        Debug.Log("Head BlendShape validation passed.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
