using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class CumulativeFaceChecks
{
    [MenuItem("Tools/Leaf/Validate Cumulative Face Fusion")]
    public static void Run()
    {
        var b = new Bounds(new Vector3(0, .5f, 0), new Vector3(1, 1, 1));
        var current = new Vector3(0, .57f, .4f);
        var a = current + Vector3.forward * .1f;
        var first = CumulativeFaceMath.Blend(current, a, .8f, 1);
        var second = CumulativeFaceMath.Blend(first, current, .2f, 1);
        Require(Mathf.Abs((second - current).z - .064f) < 1e-6f, "Later weak viewer must retain 80% of previous trace.");
        Require(CumulativeFaceMath.Blend(first, a, 1, 0) == first, "Unselected region must stay unchanged.");
        Require(CumulativeFaceMath.Blend(first, a, 0, 1) == first, "Zero influence must preserve geometry.");
        Require(CumulativeFaceMath.Blend(first, a, 1, 1) == a, "Full influence must reach target.");
        Require(CumulativeFaceMath.Mask(new Vector3(0, .1f, .4f), b, FaceRegion.WholeFace) == 0, "Neck must not deform.");
        Require(CumulativeFaceMath.Mask(new Vector3(0, .57f, -.4f), b, FaceRegion.Nose) == 0, "Back of head must not deform.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/head.fbx");
        var instance = UnityEngine.Object.Instantiate(prefab);
        try
        {
            MeshFilter source = null;
            foreach (var f in instance.GetComponentsInChildren<MeshFilter>())
                if (f.sharedMesh != null && f.GetComponent<Renderer>() != null &&
                    (source == null || f.GetComponent<Renderer>().bounds.size.y > source.GetComponent<Renderer>().bounds.size.y)) source = f;
            Require(source != null && source.sharedMesh.isReadable, "Readable head source required.");
            var vertices = source.sharedMesh.vertices;
            var local = new Vector3[vertices.Length]; var bounds = new Bounds();
            for (int i = 0; i < vertices.Length; i++)
            {
                local[i] = instance.transform.InverseTransformPoint(source.transform.TransformPoint(vertices[i]));
                if (i == 0) bounds = new Bounds(local[i], Vector3.zero); else bounds.Encapsulate(local[i]);
            }
            for (int r = 0; r < 5; r++)
            {
                int affected = 0;
                for (int i = 0; i < local.Length; i++)
                {
                    float mask = CumulativeFaceMath.Mask(local[i], bounds, (FaceRegion)r);
                    Require(mask >= 0 && mask <= 1, "Mask outside unit interval.");
                    if (mask > .01f) affected++;
                    var result = local[i];
                    for (int n = 0; n < 100; n++) result = CumulativeFaceMath.Blend(result,
                        CumulativeFaceMath.DemoTarget(local[i], bounds, n % 2), .85f, mask);
                    Require((result - local[i]).magnitude <= bounds.size.magnitude * .15f, "Repeated evaluations exceeded safety envelope.");
                    if (local[i].y < bounds.min.y + bounds.size.y * .26f)
                        Require(result == local[i], "Repeated evaluation changed the neck.");
                }
                Require(affected > 20 && affected < local.Length, "Region must affect a compact part of the real head mesh.");
                Debug.Log("Cumulative face check: " + (FaceRegion)r + " / " + affected + " influenced vertices.");
            }
            var clone = UnityEngine.Object.Instantiate(source.sharedMesh);
            try
            {
                string signature = CumulativeFaceFusion.Fingerprint(source.sharedMesh);
                Require(CumulativeFaceFusion.Fingerprint(clone) == signature, "Cloned template fingerprint differs.");
                var changed = clone.vertices; changed[0].x += .001f; clone.vertices = changed;
                Require(CumulativeFaceFusion.Fingerprint(clone) != signature, "Changed vertex template must be rejected.");
            }
            finally { UnityEngine.Object.DestroyImmediate(clone); }
        }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
        Directory.CreateDirectory("Temp");
        File.WriteAllText("Temp/CumulativeFaceChecks.txt", "PASS: accumulation, overwrite, isolation, neck/back masks, all five real-mesh regions, 100 repeated evaluations and template fingerprint.\n");
        Debug.Log("Cumulative face validation PASSED.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
