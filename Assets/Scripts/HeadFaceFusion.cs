using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

/// <summary>Desktop prototype: one camera owner, local landmark worker, frozen feature atlas per High.</summary>
public class HeadFaceFusion : MonoBehaviour
{
    [SerializeField] private AuthorityVisualController visualController;
    [SerializeField] private Transform head;
    [SerializeField] private Shader fusionShader;
    [SerializeField] private string pythonExecutable = "Tools/FaceFusion/.venv/bin/python";
    [SerializeField] private string cameraDevice = "";
    [SerializeField, Min(.2f)] private float sampleInterval = .5f;
    [SerializeField, Min(.1f)] private float maximumSnapshotAge = 2f;
    [SerializeField, Min(.1f)] private float fadeInSeconds = 1.5f;
    [SerializeField, Min(0)] private float holdSeconds = 1f;
    [SerializeField, Min(.1f)] private float fadeOutSeconds = 1f;
    [Header("Feature positions in front-view Head bounds (x, y, width, height)")]
    [SerializeField] private Vector4 leftEye = new Vector4(.28f, .68f, .28f, .16f);
    [SerializeField] private Vector4 rightEye = new Vector4(.72f, .68f, .28f, .16f);
    [SerializeField] private Vector4 mouth = new Vector4(.5f, .44f, .38f, .14f);
    [SerializeField, Range(0, 1)] private float saturation = .35f;
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    private readonly List<Mesh> generatedMeshes = new List<Mesh>();
    private readonly List<GameObject> overlays = new List<GameObject>();
    private Process worker;
    private WebCamTexture webcam;
    private Texture2D frame, snapshot;
    private Material material;
    private byte[] latestAtlas;
    private float latestAt = -100f, sentAt, nextSample;
    private bool ready, inFlight, armed, textFinished, fadingIn, revealed;
    private Coroutine transition;
    private Matrix4x4 projection;
    private int requestId;
    private Vector3 frontLocal;
    private int cameraRotation;
    private bool cameraMirrored;
    [Serializable] private class Reply { public string type; public string message; public string atlas; public int id; }
    [Serializable] private class Request { public int id; public string image; public int rotation; }

    private void OnEnable() => StartCoroutine(Initialize());

    private IEnumerator Initialize()
    {
        if (visualController == null) visualController = GetComponent<AuthorityVisualController>();
        if (head == null || visualController == null || fusionShader == null)
        { Debug.LogWarning("Face fusion: assign Head, Visual Controller and Fusion Shader.", this); yield break; }
        visualController.HighStarted += Capture;
        visualController.HighMotionStarted += Reveal;
        visualController.HighTextFinished += Finish;
        visualController.SequenceCancelled += Cancel;
        ready = false; inFlight = false; latestAtlas = null; latestAt = -100f;
        while (messages.TryDequeue(out _)) { }
        BuildOverlays();
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!isActiveAndEnabled || !Application.HasUserAuthorization(UserAuthorization.WebCam))
        { Debug.LogWarning("Face fusion: camera permission unavailable; text effects remain available.", this); yield break; }
        if (WebCamTexture.devices.Length == 0) { Debug.LogWarning("Face fusion: no webcam found.", this); yield break; }
        webcam = new WebCamTexture(string.IsNullOrEmpty(cameraDevice) ? WebCamTexture.devices[0].name : cameraDevice, 640, 480, 15);
        webcam.Play();
        StartWorker();
    }

    private void StartWorker()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string python = Path.IsPathRooted(pythonExecutable) ? pythonExecutable : Path.Combine(root, pythonExecutable);
        string script = Path.Combine(root, "Tools/FaceFusion/face_worker.py");
        try
        {
            worker = new Process { StartInfo = new ProcessStartInfo {
                FileName = python, Arguments = "-u \"" + script + "\"", WorkingDirectory = root,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            worker.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) messages.Enqueue(e.Data); };
            // Drain native detector diagnostics without touching Unity APIs on a worker thread.
            worker.ErrorDataReceived += (_, e) => { };
            worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
        }
        catch (Exception e) { Debug.LogWarning("Face fusion unavailable: " + e.Message + ". See Tools/FaceFusion/README.md", this); }
    }

    private void Update()
    {
        while (messages.TryDequeue(out string json))
        {
            Reply reply;
            try { reply = JsonUtility.FromJson<Reply>(json); }
            catch { continue; }
            if (reply.type == "ready") { ready = true; Debug.Log("Face fusion: local detector ready.", this); }
            else if (reply.type == "error") { inFlight = false; Debug.LogWarning("Face fusion: " + reply.message, this); }
            else if (reply.type == "face" && reply.id == requestId)
            {
                inFlight = false;
                if (!string.IsNullOrEmpty(reply.atlas))
                {
                    try { latestAtlas = Convert.FromBase64String(reply.atlas); latestAt = sentAt; }
                    catch (FormatException) { latestAtlas = null; }
                }
            }
        }
        if (worker != null && !inFlight && ready && worker.HasExited)
        { ready = false; Debug.LogWarning("Face fusion: detector exited; restart Play after checking the local setup.", this); }
        if (ready && inFlight && Time.unscaledTime - sentAt > 10f)
        { ready = false; Debug.LogWarning("Face fusion: detector timed out; restart Play to reconnect.", this); }
        if (!ready || inFlight || webcam == null || !webcam.didUpdateThisFrame || webcam.width < 32 || Time.unscaledTime < nextSample) return;
        if (frame == null || frame.width != webcam.width || frame.height != webcam.height)
        { if (frame != null) Destroy(frame); frame = new Texture2D(webcam.width, webcam.height, TextureFormat.RGB24, false); }
        // Normalize camera orientation before detection; cached PNGs always use upright image coordinates.
        var pixels = webcam.GetPixels32();
        cameraRotation = webcam.videoRotationAngle;
        cameraMirrored = webcam.videoVerticallyMirrored;
        if (cameraMirrored) for (int y = 0; y < webcam.height / 2; y++) for (int x = 0; x < webcam.width; x++)
        { int a = y * webcam.width + x, b = (webcam.height - y - 1) * webcam.width + x; var t = pixels[a]; pixels[a] = pixels[b]; pixels[b] = t; }
        frame.SetPixels32(pixels); frame.Apply();
        // Rotation is handled by the worker, before landmark inference.
        string payload = JsonUtility.ToJson(new Request { id = ++requestId, image = Convert.ToBase64String(frame.EncodeToJPG(85)), rotation = cameraRotation });
        nextSample = Time.unscaledTime + sampleInterval; sentAt = Time.unscaledTime; inFlight = true;
        var process = worker;
        Task.Run(() => { try { process.StandardInput.WriteLine(payload); process.StandardInput.Flush(); }
            catch (Exception) { messages.Enqueue("{\"type\":\"error\",\"message\":\"Worker input closed\"}"); } });
    }


    private void Capture()
    {
        Cancel();
        if (latestAtlas == null || Time.unscaledTime - latestAt > maximumSnapshotAge || material == null)
        { Debug.Log("Face fusion: no recent valid frontal face; playing text only.", this); return; }
        snapshot = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (!snapshot.LoadImage(latestAtlas)) { Destroy(snapshot); snapshot = null; return; }
        snapshot.wrapMode = TextureWrapMode.Clamp;
        material.SetTexture("_MainTex", snapshot);
        armed = true; textFinished = false;
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
    private void Finish() { if (!revealed) { Cancel(); return; } textFinished = true; if (armed && !fadingIn) transition = StartCoroutine(FadeOut()); }
    private IEnumerator FadeOut()
    {
        yield return new WaitForSeconds(holdSeconds);
        for (float t = 0; t < fadeOutSeconds; t += Time.deltaTime)
        { SetBlend(1 - Mathf.SmoothStep(0, 1, t / fadeOutSeconds)); yield return null; }
        SetBlend(0); armed = false; transition = null;
        if (snapshot != null) Destroy(snapshot); snapshot = null;
    }
    private void Cancel()
    {
        if (transition != null) StopCoroutine(transition);
        transition = null; armed = false; fadingIn = false; textFinished = false; revealed = false; SetBlend(0);
        if (snapshot != null) Destroy(snapshot); snapshot = null;
    }
    private void SetBlend(float value) { if (material != null) material.SetFloat("_Blend", value); }

    private void BuildOverlays()
    {
        var candidates = head.GetComponentsInChildren<Renderer>();
        var supported = new List<Renderer>();
        foreach (var r in candidates)
            if (r.enabled && (r is SkinnedMeshRenderer || r.GetComponent<MeshFilter>() != null)) supported.Add(r);
        var renderers = supported.ToArray();
        if (renderers.Length == 0 || Camera.main == null) return;
        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        Vector3 right = Camera.main.transform.right, up = Camera.main.transform.up, forward = -Camera.main.transform.forward;
        Vector3 min = Vector3.one * float.PositiveInfinity, max = Vector3.one * float.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            Vector3 q = new Vector3(Vector3.Dot(right, p), Vector3.Dot(up, p), Vector3.Dot(forward, p));
            min = Vector3.Min(min, q); max = Vector3.Max(max, q);
        }
        projection = Matrix4x4.identity;
        projection.SetRow(0, new Vector4(right.x, right.y, right.z, -min.x) / Mathf.Max(.001f, max.x - min.x));
        projection.SetRow(1, new Vector4(up.x, up.y, up.z, -min.y) / Mathf.Max(.001f, max.y - min.y));
        projection = projection * head.localToWorldMatrix;
        material = new Material(fusionShader);
        frontLocal = head.InverseTransformDirection(forward);
        // Sample the actual surface. Feature grids bridge this asset's empty eye sockets,
        // which a material-only overlay cannot cover.
        var colliders = new List<MeshCollider>();
        var colliderObjects = new List<GameObject>();
        foreach (var source in renderers)
        {
            Mesh mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
            if (source is SkinnedMeshRenderer skin)
            { mesh = new Mesh(); skin.BakeMesh(mesh); generatedMeshes.Add(mesh); }
            if (mesh == null) continue;
            var obj = new GameObject("Temporary face surface sampler");
            obj.transform.SetParent(source.transform, false);
            var collider = obj.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
            colliders.Add(collider); colliderObjects.Add(obj);
        }
        Physics.SyncTransforms();
        foreach (var region in new[] { leftEye, rightEye, mouth })
            BuildFeatureSurface(region, min, max, right, up, forward, colliders);
        foreach (var obj in colliderObjects) { obj.SetActive(false); Destroy(obj); }
    }

    private void BuildFeatureSurface(Vector4 region, Vector3 min, Vector3 max,
        Vector3 right, Vector3 up, Vector3 forward, List<MeshCollider> colliders)
    {
        const int columns = 24, rows = 12;
        var vertices = new Vector3[(columns + 1) * (rows + 1)];
        var depths = new float[vertices.Length];
        var samples = new List<float>();
        float distance = max.z - min.z + 2f;
        for (int y = 0; y <= rows; y++) for (int x = 0; x <= columns; x++)
        {
            int i = y * (columns + 1) + x;
            float px = min.x + (region.x + (x / (float)columns - .5f) * region.z) * (max.x - min.x);
            float py = min.y + (region.y + (y / (float)rows - .5f) * region.w) * (max.y - min.y);
            Vector3 origin = right * px + up * py + forward * (max.z + 1f);
            float nearest = float.PositiveInfinity;
            foreach (var c in colliders)
                if (c.Raycast(new Ray(origin, -forward), out RaycastHit hit, distance)) nearest = Mathf.Min(nearest, hit.distance);
            depths[i] = float.IsInfinity(nearest) ? float.NaN : max.z + 1f - nearest;
            if (!float.IsNaN(depths[i])) samples.Add(depths[i]);
            vertices[i] = right * px + up * py;
        }
        if (samples.Count == 0) return;
        samples.Sort(); float median = samples[samples.Count / 2];
        // Exclude far-side geometry seen through eye openings; bridge it near the eyelids.
        float depthLimit = (max.z - min.z) * .035f;
        for (int i = 0; i < vertices.Length; i++)
        {
            float depth = float.IsNaN(depths[i]) ? median : Mathf.Max(depths[i], median - depthLimit);
            vertices[i] = head.InverseTransformPoint(vertices[i] + forward * (depth + .003f));
        }
        var triangles = new int[columns * rows * 6]; int t = 0;
        for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
        { int i = y * (columns + 1) + x; triangles[t++] = i; triangles[t++] = i + columns + 1; triangles[t++] = i + 1;
          triangles[t++] = i + 1; triangles[t++] = i + columns + 1; triangles[t++] = i + columns + 2; }
        var mesh = new Mesh { name = "Conformed facial feature", vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); generatedMeshes.Add(mesh);
        var go = new GameObject("Face fusion feature"); go.layer = head.gameObject.layer;
        go.transform.SetParent(head, false); overlays.Add(go);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    private void LateUpdate()
    {
        if (material == null || head == null) return;
        material.SetMatrix("_WorldToFace", projection * head.worldToLocalMatrix);
        material.SetVector("_Front", head.TransformDirection(frontLocal).normalized);
        material.SetVector("_LeftEye", leftEye); material.SetVector("_RightEye", rightEye); material.SetVector("_Mouth", mouth);
        material.SetFloat("_Saturation", saturation);
    }
    private void OnDisable()
    {
        StopAllCoroutines(); Cancel();
        if (visualController != null)
        { visualController.HighStarted -= Capture; visualController.HighMotionStarted -= Reveal; visualController.HighTextFinished -= Finish; visualController.SequenceCancelled -= Cancel; }
        if (webcam != null) { webcam.Stop(); Destroy(webcam); }
        if (worker != null)
        { try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { } worker.Dispose(); worker = null; }
        foreach (var go in overlays) if (go != null) Destroy(go);
        overlays.Clear();
        foreach (var mesh in generatedMeshes) if (mesh != null) Destroy(mesh); generatedMeshes.Clear();
        if (material != null) Destroy(material); if (frame != null) Destroy(frame);
    }
}
