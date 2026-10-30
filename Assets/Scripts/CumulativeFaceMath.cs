using UnityEngine;

public enum FaceRegion { Eyes, Nose, Mouth, Contour, WholeFace }

/// <summary>Compact masks calibrated for the project's head-local +Y up, +Z front.</summary>
public static class CumulativeFaceMath
{
    private static float Oval(float x, float y, float rx, float ry)
    {
        float r = Mathf.Sqrt(x * x / (rx * rx) + y * y / (ry * ry));
        return 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.35f, 1, r));
    }

    public static float Mask(Vector3 p, Bounds b, FaceRegion region)
    {
        float x = (p.x - b.center.x) / b.size.x;
        float y = (p.y - b.min.y) / b.size.y;
        float front = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(b.center.z, b.max.z - b.size.z * .22f, p.z));
        float neck = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.26f, .36f, y));
        float eyes = Mathf.Max(Oval(x - .22f, y - .68f, .19f, .12f), Oval(x + .22f, y - .68f, .19f, .12f));
        float nose = Oval(x, y - .57f, .17f, .17f);
        float mouth = Oval(x, y - .44f, .33f, .10f);
        float contour = Oval(x, y - .54f, .49f, .29f) * (1 - Mathf.Max(eyes, Mathf.Max(nose, mouth)));
        float weight = region == FaceRegion.Eyes ? eyes : region == FaceRegion.Nose ? nose :
            region == FaceRegion.Mouth ? mouth : region == FaceRegion.Contour ? contour :
            Mathf.Max(contour, Mathf.Max(eyes, Mathf.Max(nose, mouth)));
        return front * neck * weight;
    }

    public static Vector3 Blend(Vector3 current, Vector3 target, float authority, float mask)
        => Vector3.Lerp(current, target, Mathf.Clamp01(authority) * Mathf.Clamp01(mask));

    // Both identities derive from the neutral face, never from an already deformed face.
    public static Vector3 DemoTarget(Vector3 p, Bounds b, int identity)
    {
        float sign = identity == 0 ? 1 : -1;
        float x = p.x - b.center.x;
        float eyes = Mask(p, b, FaceRegion.Eyes), nose = Mask(p, b, FaceRegion.Nose);
        float mouth = Mask(p, b, FaceRegion.Mouth), contour = Mask(p, b, FaceRegion.Contour);
        p.x += sign * (Mathf.Sign(x) * b.size.x * .055f * eyes + x * .28f * mouth + x * .15f * contour);
        p.z += sign * b.size.z * .10f * nose;
        p.y += sign * b.size.y * .025f * contour;
        return p;
    }
}
