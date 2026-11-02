using UnityEngine;

/// <summary>
/// Built-in-render-pipeline post effect: adds low-light analogue grain to the
/// scene while retaining pixels rendered on FaceMaskLayer. ParticleFacePrototype
/// assigns its runtime particle system to that layer automatically.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
public sealed class FaceSafeAnalogGrainEffect : MonoBehaviour
{
    public const int FaceMaskLayer = 30;

    [Header("Reference look")]
    [SerializeField, Range(0f, 1f)] private float intensity = 0.52f;
    [SerializeField, Range(0.25f, 4f)] private float grainSize = 1.2f;
    [SerializeField, Range(0f, 1f)] private float colouredGrain = 0.36f;
    [SerializeField, Range(0f, 3f)] private float rgbSplit = 0.55f;
    [SerializeField, Range(0f, 2f)] private float shadowGrainBoost = 0.8f;
    [SerializeField, Range(0f, 1f)] private float vignette = 0.18f;
    [SerializeField, Range(0.03f, 1f)] private float faceEdgeFeather = 0.2f;

    private Camera sourceCamera;
    private Camera maskCamera;
    private Material grainMaterial;
    private Shader maskShader;
    private RenderTexture maskTexture;

    public static FaceSafeAnalogGrainEffect EnsureOn(Camera camera)
    {
        if (camera == null) return null;
        var effect = camera.GetComponent<FaceSafeAnalogGrainEffect>();
        return effect != null ? effect : camera.gameObject.AddComponent<FaceSafeAnalogGrainEffect>();
    }

    private void OnEnable()
    {
        sourceCamera = GetComponent<Camera>();
        CreateResources();
    }

    private void OnDisable() => ReleaseResources();

    private void OnDestroy() => ReleaseResources();

    private void CreateResources()
    {
        if (grainMaterial == null)
        {
            Shader shader = Shader.Find("Leaf/Face Safe Analog Grain");
            if (shader != null) grainMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
        if (maskShader == null) maskShader = Shader.Find("Leaf/Face Grain Mask");
        if (maskCamera == null)
        {
            var host = new GameObject("Face grain mask camera") { hideFlags = HideFlags.HideAndDontSave };
            maskCamera = host.AddComponent<Camera>();
            maskCamera.enabled = false;
        }
    }

    private void ReleaseResources()
    {
        if (maskTexture != null) { DestroyImmediate(maskTexture); maskTexture = null; }
        if (grainMaterial != null) { DestroyImmediate(grainMaterial); grainMaterial = null; }
        if (maskCamera != null) { DestroyImmediate(maskCamera.gameObject); maskCamera = null; }
    }

    private void EnsureMaskTexture(RenderTexture source)
    {
        if (maskTexture != null && maskTexture.width == source.width && maskTexture.height == source.height) return;
        if (maskTexture != null) DestroyImmediate(maskTexture);
        maskTexture = new RenderTexture(source.width, source.height, 16, RenderTextureFormat.ARGB32)
        { name = "Face grain mask", hideFlags = HideFlags.HideAndDontSave };
    }

    private void RenderFaceMask(RenderTexture source)
    {
        EnsureMaskTexture(source);
        maskCamera.CopyFrom(sourceCamera);
        maskCamera.enabled = false;
        maskCamera.targetTexture = maskTexture;
        maskCamera.clearFlags = CameraClearFlags.SolidColor;
        maskCamera.backgroundColor = Color.black;
        maskCamera.cullingMask = 1 << FaceMaskLayer;
        maskCamera.allowHDR = false;
        maskCamera.allowMSAA = false;
        if (maskShader != null) maskCamera.SetReplacementShader(maskShader, "RenderType");
        maskCamera.Render();
        maskCamera.ResetReplacementShader();
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        CreateResources();
        if (grainMaterial == null || maskShader == null || sourceCamera == null)
        {
            Graphics.Blit(source, destination);
            return;
        }
        RenderFaceMask(source);
        grainMaterial.SetTexture("_FaceMask", maskTexture);
        grainMaterial.SetFloat("_Intensity", intensity);
        grainMaterial.SetFloat("_GrainSize", grainSize);
        grainMaterial.SetFloat("_ChromaGrain", colouredGrain);
        grainMaterial.SetFloat("_ChromaticAberration", rgbSplit);
        grainMaterial.SetFloat("_ShadowLift", shadowGrainBoost);
        grainMaterial.SetFloat("_Vignette", vignette);
        grainMaterial.SetFloat("_FaceFeather", faceEdgeFeather);
        Graphics.Blit(source, destination, grainMaterial);
    }
}
