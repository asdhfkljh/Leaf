using UnityEngine;

/// <summary>Maps normalized identity proportions to paired shape controls; never maps expression scores.</summary>
public static class HeadMorphWeights
{
    public static Vector4 Calculate(float eyeRatio, float mouthRatio, float referenceEye, float referenceMouth,
        float maximumWeight)
    {
        float eyes = Mathf.Clamp((eyeRatio / Mathf.Max(.01f, referenceEye) - 1) / .20f, -1, 1);
        float mouth = Mathf.Clamp((mouthRatio / Mathf.Max(.01f, referenceMouth) - 1) / .25f, -1, 1);
        float limit = Mathf.Clamp(maximumWeight, 0, 100);
        return new Vector4(Mathf.Max(eyes, 0), Mathf.Max(-eyes, 0), Mathf.Max(mouth, 0), Mathf.Max(-mouth, 0)) * limit;
    }
}
