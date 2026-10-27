using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

/// <summary>Freezes stable face proportions on High and morphs the actual Head mesh using BlendShapes.</summary>
public class HeadFaceFusion : MonoBehaviour
{
    [SerializeField] private AuthorityVisualController visualController;
    [SerializeField] private Transform head;
    [SerializeField] private Mesh morphMesh;
    [SerializeField] private string pythonExecutable = "Tools/FaceFusion/.venv/bin/python";
    [SerializeField] private string cameraDevice = "";
    [SerializeField, Min(.2f)] private float sampleInterval = .5f;
    [SerializeField, Min(.1f)] private float maximumSnapshotAge = 2f;
    [SerializeField, Min(.1f)] private float fadeInSeconds = 1.5f;
    [SerializeField, Min(0)] private float holdSeconds = 1f;
    [SerializeField, Min(.1f)] private float fadeOutSeconds = 1f;
    [Header("Identity proportions (eye-center distance / face width; mouth width / face width)")]
    [Tooltip("Artistic reference for this Head. Tune using a frontal, relaxed face; these are not identity recognition scores.")]
    [SerializeField, Range(.2f, .8f)] private float referenceEyeSpacing = .52f;
    [SerializeField, Range(.2f, .8f)] private float referenceMouthWidth = .42f;
    [SerializeField, Range(0, 100)] private float maximumShapeWeight = 75f;
    [SerializeField, Range(2, 6)] private int stableSampleCount = 3;
    [Tooltip("Reject a sampling window if the normalized proportions fluctuate more than this amount.")]
    [SerializeField, Range(.005f, .1f)] private float maximumRatioSpread = .035f;

    private static readonly string[] ShapeNames = { "EyeSpacingWide", "EyeSpacingNarrow", "MouthWide", "MouthNarrow" };
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    private readonly List<Measurement> samples = new List<Measurement>();
    private readonly int[] shapeIndices = new int[4];
    private readonly float[] initialWeights = new float[4];
    private Process worker;
    private WebCamTexture webcam;
    private Texture2D frame;
    private Renderer sourceRenderer;
    private bool sourceWasEnabled;
    private SkinnedMeshRenderer morphRenderer;
    private GameObject morphObject;
    private Vector4 targetWeights;
    private float sentAt, nextSample, workerStartedAt;
    private bool ready, inFlight, armed, textFinished, fadingIn, revealed, workerFailed;
    private Coroutine transition;
    private int requestId, session;

    [Serializable] private class Reply
    {
        public string type, message;
        public int id;
        public bool valid;
        public float eye_spacing_ratio, mouth_width_ratio;
    }
    [Serializable] private class Request { public int id; public string image; public int rotation; }
    private struct Measurement { public float eyes, mouth, at; }

    private void OnEnable() => StartCoroutine(Initialize());

    private IEnumerator Initialize()
    {
        if (visualController == null) visualController = GetComponent<AuthorityVisualController>();
        if (head == null || visualController == null || morphMesh == null || !BuildMorphRenderer())
        { Debug.LogWarning("Head morph: assign Head, Visual Controller and the authored morph mesh.", this); yield break; }
        visualController.HighStarted += Capture;
        visualController.HighMotionStarted += Reveal;
        visualController.HighTextFinished += Finish;
        visualController.SequenceCancelled += Cancel;
        ready = false; inFlight = false; workerFailed = false; samples.Clear();
        while (messages.TryDequeue(out _)) { }
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!isActiveAndEnabled || !Application.HasUserAuthorization(UserAuthorization.WebCam))
        { Debug.LogWarning("Head morph: camera permission unavailable; text effects remain available.", this); yield break; }
        if (WebCamTexture.devices.Length == 0)
        { Debug.LogWarning("Head morph: no webcam found.", this); yield break; }
        webcam = new WebCamTexture(string.IsNullOrEmpty(cameraDevice) ? WebCamTexture.devices[0].name : cameraDevice, 640, 480, 15);
        webcam.Play(); StartWorker();
    }

    private bool BuildMorphRenderer()
    {
        MeshFilter source = null;
        foreach (var filter in head.GetComponentsInChildren<MeshFilter>())
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.enabled || filter.sharedMesh == null) continue;
            if (source == null || renderer.bounds.size.y > source.GetComponent<Renderer>().bounds.size.y) source = filter;
        }
        if (source == null || source.sharedMesh.vertexCount != morphMesh.vertexCount ||
            Vector3.Distance(source.sharedMesh.bounds.size, morphMesh.bounds.size) > .001f)
        { Debug.LogWarning("Head morph: authored mesh does not match the source Head topology/bounds.", this); return false; }
        for (int i = 0; i < ShapeNames.Length; i++)
        {
            shapeIndices[i] = morphMesh.GetBlendShapeIndex(ShapeNames[i]);
            if (shapeIndices[i] < 0) { Debug.LogWarning("Head morph: missing BlendShape " + ShapeNames[i], this); return false; }
        }
        sourceRenderer = source.GetComponent<Renderer>(); sourceWasEnabled = sourceRenderer.enabled;
        morphObject = new GameObject("Head BlendShape renderer"); morphObject.layer = source.gameObject.layer;
        morphObject.transform.SetParent(source.transform, false);
        morphRenderer = morphObject.AddComponent<SkinnedMeshRenderer>();
        morphRenderer.sharedMesh = morphMesh;
        morphRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
        morphRenderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
        morphRenderer.receiveShadows = sourceRenderer.receiveShadows;
        var bounds = morphMesh.bounds; bounds.Expand(bounds.size * .15f); morphRenderer.localBounds = bounds;
        for (int i = 0; i < initialWeights.Length; i++) initialWeights[i] = morphRenderer.GetBlendShapeWeight(shapeIndices[i]);
        sourceRenderer.enabled = false;
        return true;
    }

    private void StartWorker()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string python = Path.IsPathRooted(pythonExecutable) ? pythonExecutable : Path.Combine(root, pythonExecutable);
        string script = Path.Combine(root, "Tools/FaceFusion/face_worker.py");
        int currentSession = ++session;
        try
        {
            worker = new Process { StartInfo = new ProcessStartInfo {
                FileName = python, Arguments = "-u \"" + script + "\"", WorkingDirectory = root,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            worker.OutputDataReceived += (_, e) => {
                if (currentSession == session && !string.IsNullOrEmpty(e.Data)) messages.Enqueue(e.Data);
            };
            worker.ErrorDataReceived += (_, e) => { }; // Drain native diagnostics; structured failures arrive on stdout.
            worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine(); workerStartedAt = Time.unscaledTime;
        }
        catch (Exception e) { workerFailed = true; Debug.LogWarning("Head morph unavailable: " + e.Message + ". See Tools/FaceFusion/README.md", this); }
    }

    private void Update()
    {
        while (messages.TryDequeue(out string json))
        {
            Reply reply;
            try { reply = JsonUtility.FromJson<Reply>(json); }
            catch { continue; }
            if (reply == null) continue;
            if (reply.type == "ready") { ready = true; Debug.Log("Head morph: local detector ready. Face camera with relaxed mouth for three stable samples.", this); }
            else if (reply.type == "error")
            { inFlight = false; samples.Clear(); Debug.LogWarning("Head morph: " + reply.message, this); }
            else if (reply.type == "face" && reply.id == requestId)
            {
                inFlight = false;
                if (reply.valid && IsRatio(reply.eye_spacing_ratio) && IsRatio(reply.mouth_width_ratio))
                {
                    samples.Add(new Measurement { eyes = reply.eye_spacing_ratio, mouth = reply.mouth_width_ratio, at = sentAt });
                    while (samples.Count > stableSampleCount) samples.RemoveAt(0);
                }
                else samples.Clear();
            }
        }
        if (worker != null && !workerFailed && (worker.HasExited ||
            (!ready && Time.unscaledTime - workerStartedAt > 60f) ||
            (inFlight && Time.unscaledTime - sentAt > 10f)))
        {
            workerFailed = true; ready = false; samples.Clear();
            Debug.LogWarning("Head morph: detector stopped or timed out; restart Play after checking the local setup.", this);
        }
        if (!ready || workerFailed || inFlight || webcam == null || !webcam.didUpdateThisFrame || webcam.width < 32 || Time.unscaledTime < nextSample) return;
        if (frame == null || frame.width != webcam.width || frame.height != webcam.height)
        { if (frame != null) Destroy(frame); frame = new Texture2D(webcam.width, webcam.height, TextureFormat.RGB24, false); }
        var pixels = webcam.GetPixels32();
        if (webcam.videoVerticallyMirrored)
            for (int y = 0; y < webcam.height / 2; y++) for (int x = 0; x < webcam.width; x++)
            {
                int a = y * webcam.width + x, b = (webcam.height - y - 1) * webcam.width + x;
                var temporary = pixels[a]; pixels[a] = pixels[b]; pixels[b] = temporary;
            }
        frame.SetPixels32(pixels); frame.Apply();
        string payload = JsonUtility.ToJson(new Request { id = ++requestId,
            image = Convert.ToBase64String(frame.EncodeToJPG(85)), rotation = webcam.videoRotationAngle });
        nextSample = Time.unscaledTime + sampleInterval; sentAt = Time.unscaledTime; inFlight = true;
        var process = worker; int currentSession = session;
        Task.Run(() => {
            try { process.StandardInput.WriteLine(payload); process.StandardInput.Flush(); }
            catch (Exception) { if (currentSession == session) messages.Enqueue("{\"type\":\"error\",\"message\":\"Worker input closed\"}"); }
        });
    }

    private static bool IsRatio(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > .1f && value < .9f;

    private void Capture()
    {
        Cancel();
        if (morphRenderer == null || samples.Count < stableSampleCount || Time.unscaledTime - samples[0].at > maximumSnapshotAge)
        { Debug.Log("Head morph: no fresh stable frontal measurements; playing text only.", this); return; }
        float eyeMin = 1, eyeMax = 0, mouthMin = 1, mouthMax = 0;
        var eyes = new List<float>(); var mouths = new List<float>();
        foreach (var sample in samples)
        {
            eyeMin = Mathf.Min(eyeMin, sample.eyes); eyeMax = Mathf.Max(eyeMax, sample.eyes);
            mouthMin = Mathf.Min(mouthMin, sample.mouth); mouthMax = Mathf.Max(mouthMax, sample.mouth);
            eyes.Add(sample.eyes); mouths.Add(sample.mouth);
        }
        if (eyeMax - eyeMin > maximumRatioSpread || mouthMax - mouthMin > maximumRatioSpread)
        { Debug.Log("Head morph: unstable measurements; playing text only.", this); return; }
        eyes.Sort(); mouths.Sort();
        targetWeights = HeadMorphWeights.Calculate(eyes[eyes.Count / 2], mouths[mouths.Count / 2],
            referenceEyeSpacing, referenceMouthWidth, maximumShapeWeight);
        armed = true;
    }

    private void Reveal() { if (armed) { revealed = true; transition = StartCoroutine(FadeIn()); } }
    private IEnumerator FadeIn()
    {
        fadingIn = true;
        for (float t = 0; t < fadeInSeconds; t += Time.deltaTime)
        { SetBlend(Mathf.SmoothStep(0, 1, t / fadeInSeconds)); yield return null; }
        SetBlend(1); fadingIn = false; transition = null;
        if (textFinished) transition = StartCoroutine(FadeOut());
    }
    private void Finish()
    {
        if (!revealed) { Cancel(); return; }
        textFinished = true;
        if (armed && !fadingIn) transition = StartCoroutine(FadeOut());
    }
    private IEnumerator FadeOut()
    {
        yield return new WaitForSeconds(holdSeconds);
        for (float t = 0; t < fadeOutSeconds; t += Time.deltaTime)
        { SetBlend(1 - Mathf.SmoothStep(0, 1, t / fadeOutSeconds)); yield return null; }
        SetBlend(0); armed = false; transition = null;
    }
    private void SetBlend(float amount)
    {
        if (morphRenderer == null) return;
        for (int i = 0; i < initialWeights.Length; i++)
            morphRenderer.SetBlendShapeWeight(shapeIndices[i], Mathf.Lerp(initialWeights[i], targetWeights[i], amount));
    }
    private void Cancel()
    {
        if (transition != null) StopCoroutine(transition);
        transition = null; armed = false; fadingIn = false; textFinished = false; revealed = false; SetBlend(0);
    }
    private void OnDisable()
    {
        StopAllCoroutines(); Cancel(); ++session;
        if (visualController != null)
        {
            visualController.HighStarted -= Capture; visualController.HighMotionStarted -= Reveal;
            visualController.HighTextFinished -= Finish; visualController.SequenceCancelled -= Cancel;
        }
        if (webcam != null) { webcam.Stop(); Destroy(webcam); webcam = null; }
        if (worker != null)
        {
            try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { }
            worker.Dispose(); worker = null;
        }
        if (sourceRenderer != null) sourceRenderer.enabled = sourceWasEnabled;
        if (morphObject != null) { morphObject.SetActive(false); Destroy(morphObject); }
        morphRenderer = null; morphObject = null; sourceRenderer = null;
        if (frame != null) { Destroy(frame); frame = null; }
        samples.Clear(); ready = false; inFlight = false;
    }
}
