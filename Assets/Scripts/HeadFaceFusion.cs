using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

/// <summary>Shows a frozen, locally captured face on a blank mask during High.</summary>
public class HeadFaceFusion : MonoBehaviour
{
    [SerializeField] private AuthorityVisualController visualController;
    [SerializeField] private Transform head;
    [SerializeField] private string pythonExecutable = "Tools/FaceFusion/.venv/bin/python";
    [SerializeField] private string cameraDevice = "";
    [SerializeField, Min(.2f)] private float sampleInterval = .5f;
    [SerializeField, Min(.1f)] private float maximumSnapshotAge = 2f;
    [SerializeField, Min(.1f)] private float fadeInSeconds = 1.5f;
    [SerializeField, Min(0)] private float holdSeconds = 1f;
    [SerializeField, Min(.1f)] private float fadeOutSeconds = 1f;
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    private Texture2D latestPortrait, frozenPortrait;
    private float portraitAt;
    private Material portraitMaterial, blankMaterial;
    private Mesh portraitMesh;
    private readonly List<Renderer> hiddenRenderers = new List<Renderer>();
    private Process worker;
    private WebCamTexture webcam;
    private Texture2D frame;
    private GameObject maskObject;
    private float sentAt, nextSample, workerStartedAt;
    private bool ready, inFlight, armed, textFinished, fadingIn, revealed, workerFailed;
    private Coroutine transition;
    private int requestId, session;

    [Serializable] private class Reply
    {
        public string type, message, portrait;
        public int id;
    }
    [Serializable] private class Request { public int id; public string image; public int rotation; }

    private void OnEnable() => StartCoroutine(Initialize());

    private IEnumerator Initialize()
    {
        if (visualController == null) visualController = GetComponent<AuthorityVisualController>();
        if (head == null || visualController == null || !BuildMaskRenderer())
        { Debug.LogWarning("Face reveal: assign Head and Visual Controller; check face shader.", this); yield break; }
        visualController.HighStarted += Capture;

        visualController.HighTextFinished += Finish;
        visualController.SequenceCancelled += Cancel;
        ready = false; inFlight = false; workerFailed = false;
        while (messages.TryDequeue(out _)) { }
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!isActiveAndEnabled || !Application.HasUserAuthorization(UserAuthorization.WebCam))
        { Debug.LogWarning("Face reveal: camera permission unavailable; text effects remain available.", this); yield break; }
        if (WebCamTexture.devices.Length == 0)
        { Debug.LogWarning("Face reveal: no webcam found.", this); yield break; }
        webcam = new WebCamTexture(string.IsNullOrEmpty(cameraDevice) ? WebCamTexture.devices[0].name : cameraDevice, 640, 480, 15);
        webcam.Play(); StartWorker();
    }

    private bool BuildMaskRenderer()
    {
        MeshFilter source = null;
        foreach (var filter in head.GetComponentsInChildren<MeshFilter>())
            if (filter.sharedMesh != null && filter.GetComponent<Renderer>() != null &&
                (source == null || filter.GetComponent<Renderer>().bounds.size.y > source.GetComponent<Renderer>().bounds.size.y)) source = filter;
        if (source == null) return false;
        var bounds = new Bounds(); bool first = true;
        foreach (var v in source.sharedMesh.vertices)
        {
            var p = head.InverseTransformPoint(source.transform.TransformPoint(v));
            if (first) { bounds = new Bounds(p, Vector3.zero); first = false; } else bounds.Encapsulate(p);
        }
        var shader = Shader.Find("Leaf/FaceReveal");
        var blankShader = Shader.Find("Standard");
        if (shader == null || blankShader == null) return false;
        foreach (var renderer in head.GetComponentsInChildren<Renderer>())
            if (renderer.enabled) { hiddenRenderers.Add(renderer); renderer.enabled = false; }
        maskObject = new GameObject("Blank mask and captured face");
        maskObject.layer = source.gameObject.layer;
        maskObject.transform.SetParent(head, false);
        var blank = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        blank.name = "Blank head"; blank.layer = maskObject.layer;
        blank.transform.SetParent(maskObject.transform, false);
        blank.transform.localPosition = bounds.center;
        blank.transform.localScale = bounds.size;
        Destroy(blank.GetComponent<Collider>());
        blankMaterial = new Material(blankShader); blankMaterial.color = Color.white;
        blank.GetComponent<Renderer>().sharedMaterial = blankMaterial;
        var face = new GameObject("Frozen portrait"); face.layer = maskObject.layer;
        face.transform.SetParent(maskObject.transform, false);
        // Front is +Z on this project's head. A curved patch sits just outside the blank ellipsoid.
        const int columns = 32, rows = 40;
        var vertices = new Vector3[(columns + 1) * (rows + 1)];
        var uv = new Vector2[vertices.Length]; var triangles = new int[columns * rows * 6];
        for (int y = 0; y <= rows; y++) for (int x = 0; x <= columns; x++)
        {
            float u = (float)x / columns, v = (float)y / rows;
            float px = (u - .5f) * 1.65f, py = (v - .5f) * 1.70f;
            float z = Mathf.Sqrt(Mathf.Max(.015f, 1 - px * px - py * py));
            int i = y * (columns + 1) + x;
            vertices[i] = bounds.center + Vector3.Scale(new Vector3(px, py, z + .018f), bounds.extents);
            uv[i] = new Vector2(1 - u, v); // +Z front has screen-right along local -X.
            if (x < columns && y < rows)
            {
                int t = (y * columns + x) * 6;
                triangles[t] = i; triangles[t+1] = i+1; triangles[t+2] = i+columns+1;
                triangles[t+3] = i+1; triangles[t+4] = i+columns+2; triangles[t+5] = i+columns+1;
            }
        }
        portraitMesh = new Mesh { name = "Curved face photo" };
        portraitMesh.vertices = vertices; portraitMesh.uv = uv; portraitMesh.triangles = triangles;
        portraitMesh.RecalculateNormals(); portraitMesh.RecalculateBounds();
        face.AddComponent<MeshFilter>().sharedMesh = portraitMesh;
        portraitMaterial = new Material(shader);
        face.AddComponent<MeshRenderer>().sharedMaterial = portraitMaterial;
        face.SetActive(false);
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
        catch (Exception e) { workerFailed = true; Debug.LogWarning("Face reveal unavailable: " + e.Message + ". See Tools/FaceFusion/README.md", this); }
    }

    private void Update()
    {
        while (messages.TryDequeue(out string json))
        {
            Reply reply;
            try { reply = JsonUtility.FromJson<Reply>(json); }
            catch { continue; }
            if (reply == null) continue;
            if (reply.type == "ready") { ready = true; Debug.Log("Face reveal: local detector ready. Face the camera; High freezes the latest valid portrait.", this); }
            else if (reply.type == "error")
            { inFlight = false; Debug.LogWarning("Face reveal: " + reply.message, this); }
            else if (reply.type == "face" && reply.id == requestId)
            {
                inFlight = false;
                if (!string.IsNullOrEmpty(reply.portrait))
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    try
                    {
                        if (texture.LoadImage(Convert.FromBase64String(reply.portrait)))
                        {
                            if (latestPortrait != null) Destroy(latestPortrait);
                            latestPortrait = texture; portraitAt = sentAt;
                        }
                        else Destroy(texture);
                    }
                    catch { Destroy(texture); }
                }
                else { if (latestPortrait != null) Destroy(latestPortrait); latestPortrait = null; }

            }
        }
        if (worker != null && !workerFailed && (worker.HasExited ||
            (!ready && Time.unscaledTime - workerStartedAt > 60f) ||
            (inFlight && Time.unscaledTime - sentAt > 10f)))
        {
            workerFailed = true; ready = false;
            Debug.LogWarning("Face reveal: detector stopped or timed out; restart Play after checking the local setup.", this);
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

    private void Capture()
    {
        Cancel();
        if (!ready || workerFailed || portraitMaterial == null || latestPortrait == null || Time.unscaledTime - portraitAt > maximumSnapshotAge)
        { Debug.Log("Face reveal: no fresh frontal face; keeping blank mask.", this); return; }
        frozenPortrait = Instantiate(latestPortrait);
        portraitMaterial.mainTexture = frozenPortrait;
        armed = true;
        Reveal();
    }

    private void Reveal() { if (armed && !revealed) { revealed = true; transition = StartCoroutine(FadeIn()); } }
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
        if (maskObject == null || portraitMaterial == null) return;
        var face = maskObject.transform.Find("Frozen portrait");
        face.gameObject.SetActive(amount > 0);
        portraitMaterial.color = new Color(1, 1, 1, amount);
    }
    private void Cancel()
    {
        if (transition != null) StopCoroutine(transition);
        transition = null; armed = false; fadingIn = false; textFinished = false; revealed = false; SetBlend(0);
        if (frozenPortrait != null) Destroy(frozenPortrait); frozenPortrait = null;
    }
    private void OnDisable()
    {
        StopAllCoroutines(); Cancel(); ++session;
        if (visualController != null)
        {
            visualController.HighStarted -= Capture;
            visualController.HighTextFinished -= Finish; visualController.SequenceCancelled -= Cancel;
        }
        if (webcam != null) { webcam.Stop(); Destroy(webcam); webcam = null; }
        if (worker != null)
        {
            try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { }
            worker.Dispose(); worker = null;
        }
        foreach (var renderer in hiddenRenderers) if (renderer != null) renderer.enabled = true;
        hiddenRenderers.Clear();
        if (latestPortrait != null) Destroy(latestPortrait); latestPortrait = null;
        if (portraitMaterial != null) Destroy(portraitMaterial);
        if (blankMaterial != null) Destroy(blankMaterial);
        if (portraitMesh != null) Destroy(portraitMesh);
        if (maskObject != null) { maskObject.SetActive(false); Destroy(maskObject); }
        maskObject = null;
        if (frame != null) { Destroy(frame); frame = null; }
        ready = false; inFlight = false;
    }
}
