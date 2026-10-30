using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

/// <summary>Shows a frozen, locally captured face on the original head surface during High.</summary>
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
    [Header("Face region on original head (normalized bounds)")]
    [SerializeField] private Vector2 faceHorizontalRange = new Vector2(.12f, .88f);
    [SerializeField] private Vector2 faceVerticalRange = new Vector2(.25f, .92f);
    [Header("Photo alignment on head (normalized bounds)")]
    [SerializeField, Range(.25f, .55f)] private float photoEyeSpacing = .42f;
    [SerializeField, Range(.5f, .8f)] private float photoEyeHeight = .68f;
    [SerializeField, Range(.3f, .55f)] private float photoMouthHeight = .44f;
    [Header("Skin colour matching")]
    [SerializeField] private bool matchSkinColour = true;
    [SerializeField, Range(0, 1)] private float skinColourStrength = 1f;
    [SerializeField, Range(.5f, 1.5f)] private float skinBrightness = 1f;
    [SerializeField, Range(0, 1)] private float skinSmoothness = .2f;
    private Renderer skinRenderer;
    private Material[] originalSkinMaterials, skinMaterials;
    private Color[] originalSkinColours;
    private float[] originalSmoothness;
    private string[] skinColourProperties, skinSmoothnessProperties;
    private Color latestSkinColour, frozenSkinColour;
    private bool latestHasSkin, frozenHasSkin;
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    private Texture2D latestPortrait, frozenPortrait;
    private float portraitAt;
    private Material portraitMaterial;
    private Mesh portraitMesh;
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
        public float[] skin_rgb;
    }
    [Serializable] private class Request { public int id; public string image; public int rotation; public float[] portrait_targets; }

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
        if (shader == null) return false;
        maskObject = new GameObject("Face photo overlay");
        maskObject.layer = source.gameObject.layer;
        maskObject.transform.SetParent(head, false);
        var face = new GameObject("Frozen portrait"); face.layer = maskObject.layer;
        face.transform.SetParent(maskObject.transform, false);
        // Sample the actual mesh surface, keeping its nose, cheeks and jaw depth.
        // An isolated collider is used only for construction; original renderers stay untouched.
        var probe = new GameObject("Face surface sampler");
        probe.transform.SetParent(source.transform, false);
        var collider = probe.AddComponent<MeshCollider>(); collider.sharedMesh = source.sharedMesh;
        Physics.SyncTransforms();
        const int columns = 80, rows = 96;
        var vertices = new Vector3[(columns + 1) * (rows + 1)];
        var uv = new Vector2[vertices.Length];
        var valid = new bool[vertices.Length];
        var triangles = new System.Collections.Generic.List<int>();
        float offset = bounds.size.z * .0025f;
        try
        {
            for (int y = 0; y <= rows; y++) for (int x = 0; x <= columns; x++)
            {
                float u = (float)x / columns, v = (float)y / rows;
                int i = y * (columns + 1) + x;
                float px = Mathf.Lerp(bounds.min.x, bounds.max.x, Mathf.Lerp(faceHorizontalRange.x, faceHorizontalRange.y, u));
                float py = Mathf.Lerp(bounds.min.y, bounds.max.y, Mathf.Lerp(faceVerticalRange.x, faceVerticalRange.y, v));
                var origin = head.TransformPoint(new Vector3(px, py, bounds.max.z + bounds.size.z));
                var end = head.TransformPoint(new Vector3(px, py, bounds.min.z));
                var ray = new Ray(origin, end - origin);
                if (collider.Raycast(ray, out var hit, Vector3.Distance(origin, end)))
                {
                    var local = head.InverseTransformPoint(hit.point);
                    // Exclude rear surfaces visible through the original eye openings.
                    if (local.z > bounds.center.z)
                    { vertices[i] = local + Vector3.forward * offset; valid[i] = true; }
                }
                if (!valid[i]) vertices[i] = new Vector3(px, py, 0);
                uv[i] = new Vector2(1 - u, v);
            }
            // Fill only enclosed eye openings, using neighboring surface depth. Outside
            // the head silhouette remains invalid, so ears/neck never acquire a photo sheet.
            var originalValid = (bool[])valid.Clone();
            for (int y = 1; y < rows; y++) for (int x = 1; x < columns; x++)
            {
                int i = y * (columns + 1) + x;
                if (valid[i]) continue;
                int left = x-1, right = x+1, down = y-1, up = y+1;
                while (left >= 0 && !originalValid[y*(columns+1)+left]) left--;
                while (right <= columns && !originalValid[y*(columns+1)+right]) right++;
                while (down >= 0 && !originalValid[down*(columns+1)+x]) down--;
                while (up <= rows && !originalValid[up*(columns+1)+x]) up++;
                if (left < 0 || right > columns || down < 0 || up > rows || right-left > 24 || up-down > 20) continue;
                vertices[i].z = (vertices[y*(columns+1)+left].z + vertices[y*(columns+1)+right].z +
                    vertices[down*(columns+1)+x].z + vertices[up*(columns+1)+x].z) * .25f;
                valid[i] = true;
            }
            // The source has open eye sockets. Bridge their depth at the surrounding
            // face plane, including inner/back-facing socket geometry hit by the probe.
            for (int y = 1; y < rows; y++) for (int x = 1; x < columns; x++)
            {
                int i = y*(columns+1)+x;
                float nx = (vertices[i].x-bounds.min.x)/bounds.size.x;
                float ny = (vertices[i].y-bounds.min.y)/bounds.size.y;
                float eyeX = nx < .5f ? .28f : .72f;
                if (Mathf.Pow((nx-eyeX)/.105f,2) + Mathf.Pow((ny-.68f)/.045f,2) > 1) continue;
                int left = Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(faceHorizontalRange.x,faceHorizontalRange.y,eyeX-.115f)*columns),0,columns);
                int right = Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(faceHorizontalRange.x,faceHorizontalRange.y,eyeX+.115f)*columns),0,columns);
                int a = y*(columns+1)+left, b = y*(columns+1)+right;
                if (!valid[a] || !valid[b]) continue;
                float bridge = Mathf.Lerp(vertices[a].z,vertices[b].z,Mathf.InverseLerp(left,right,x)) + offset;
                vertices[i].z = valid[i] ? Mathf.Max(vertices[i].z,bridge) : bridge;
                valid[i] = true;
            }
            // Conservative depth envelope prevents the sampled grid from cutting through
            // sharp nose/nostril edges between samples, without altering the head mesh.
            var sampled = (Vector3[])vertices.Clone();
            for (int y = 0; y <= rows; y++) for (int x = 0; x <= columns; x++)
            {
                int i = y*(columns+1)+x;
                if (!valid[i]) continue;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x+dx, yy = y+dy;
                    if (xx < 0 || xx > columns || yy < 0 || yy > rows) continue;
                    int j = yy*(columns+1)+xx;
                    if (valid[j]) vertices[i].z = Mathf.Max(vertices[i].z,sampled[j].z);
                }
            }
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
            {
                int i = y*(columns+1)+x, j = i+columns+1;
                if (valid[i] && valid[i+1] && valid[j]) { triangles.Add(i); triangles.Add(i+1); triangles.Add(j); }
                if (valid[i+1] && valid[j+1] && valid[j]) { triangles.Add(i+1); triangles.Add(j+1); triangles.Add(j); }
            }
        }
        finally { probe.SetActive(false); Destroy(probe); }
        portraitMesh = new Mesh { name = "Original head fitted face photo" };
        portraitMesh.vertices = vertices; portraitMesh.uv = uv; portraitMesh.triangles = triangles.ToArray();
        portraitMesh.RecalculateNormals(); portraitMesh.RecalculateBounds();
        face.AddComponent<MeshFilter>().sharedMesh = portraitMesh;
        portraitMaterial = new Material(shader);
        face.AddComponent<MeshRenderer>().sharedMaterial = portraitMaterial;
        face.SetActive(false);
        PrepareSkinMaterials(source.GetComponent<Renderer>());
        return true;
    }

    private void PrepareSkinMaterials(Renderer renderer)
    {
        skinRenderer = renderer;
        originalSkinMaterials = renderer.sharedMaterials;
        skinMaterials = new Material[originalSkinMaterials.Length];
        originalSkinColours = new Color[skinMaterials.Length];
        originalSmoothness = new float[skinMaterials.Length];
        skinColourProperties = new string[skinMaterials.Length];
        skinSmoothnessProperties = new string[skinMaterials.Length];
        for (int i = 0; i < skinMaterials.Length; i++)
        {
            var original = originalSkinMaterials[i];
            if (original == null) continue;
            var material = skinMaterials[i] = new Material(original);
            material.name = original.name + " (runtime skin match)";
            string colour = material.HasProperty("_BaseColor") ? "_BaseColor" : material.HasProperty("_Color") ? "_Color" : null;
            skinColourProperties[i] = colour;
            if (colour != null) originalSkinColours[i] = material.GetColor(colour);
            string gloss = material.HasProperty("_Glossiness") ? "_Glossiness" : material.HasProperty("_Smoothness") ? "_Smoothness" : null;
            skinSmoothnessProperties[i] = gloss;
            if (gloss != null) originalSmoothness[i] = material.GetFloat(gloss);
        }
        renderer.sharedMaterials = skinMaterials;
    }

    private static bool TrySkinColour(float[] rgb, out Color colour)
    {
        colour = Color.white;
        if (rgb == null || rgb.Length != 3) return false;
        foreach (float value in rgb)
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value > 1) return false;
        colour = new Color(rgb[0], rgb[1], rgb[2], 1);
        return true;
    }

    private void BlendSkin(float amount)
    {
        if (skinMaterials == null) return;
        float blend = frozenHasSkin && matchSkinColour ? Mathf.Clamp01(amount) * skinColourStrength : 0;
        for (int i = 0; i < skinMaterials.Length; i++)
        {
            var material = skinMaterials[i];
            if (material == null) continue;
            if (skinColourProperties[i] != null)
            {
                var target = frozenSkinColour * skinBrightness;
                target.r = Mathf.Clamp01(target.r); target.g = Mathf.Clamp01(target.g); target.b = Mathf.Clamp01(target.b);
                target.a = originalSkinColours[i].a;
                material.SetColor(skinColourProperties[i], Color.Lerp(originalSkinColours[i], target, blend));
            }
            if (skinSmoothnessProperties[i] != null)
                material.SetFloat(skinSmoothnessProperties[i], Mathf.Lerp(originalSmoothness[i], Mathf.Min(originalSmoothness[i], skinSmoothness), blend));
        }
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
                            latestHasSkin = TrySkinColour(reply.skin_rgb, out latestSkinColour);
                        }
                        else Destroy(texture);
                    }
                    catch { Destroy(texture); }
                }
                else { if (latestPortrait != null) Destroy(latestPortrait); latestPortrait = null; latestHasSkin = false; }

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
            image = Convert.ToBase64String(frame.EncodeToJPG(85)), rotation = webcam.videoRotationAngle,
            portrait_targets = PortraitAlignmentTargets() });
        nextSample = Time.unscaledTime + sampleInterval; sentAt = Time.unscaledTime; inFlight = true;
        var process = worker; int currentSession = session;
        Task.Run(() => {
            try { process.StandardInput.WriteLine(payload); process.StandardInput.Flush(); }
            catch (Exception) { if (currentSession == session) messages.Enqueue("{\"type\":\"error\",\"message\":\"Worker input closed\"}"); }
        });
    }

    private float[] PortraitAlignmentTargets()
    {
        // Image coordinates have Y down; surface UVs have Y up and mirrored X.
        float width = Mathf.Max(.01f, faceHorizontalRange.y - faceHorizontalRange.x);
        float height = Mathf.Max(.01f, faceVerticalRange.y - faceVerticalRange.x);
        float left = (faceHorizontalRange.y - (.5f + photoEyeSpacing * .5f)) / width;
        float right = (faceHorizontalRange.y - (.5f - photoEyeSpacing * .5f)) / width;
        float eyes = (faceVerticalRange.y - photoEyeHeight) / height;
        float mouth = (faceVerticalRange.y - photoMouthHeight) / height;
        return new[] { left, eyes, right, eyes, (faceHorizontalRange.y - .5f) / width, mouth };
    }

    private void Capture()
    {
        Cancel();
        if (!ready || workerFailed || portraitMaterial == null || latestPortrait == null || Time.unscaledTime - portraitAt > maximumSnapshotAge)
        { Debug.Log("Face reveal: no fresh frontal face; keeping original head.", this); return; }
        frozenSkinColour = latestSkinColour; frozenHasSkin = latestHasSkin;
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
        BlendSkin(amount);
        if (maskObject == null || portraitMaterial == null) return;
        var face = maskObject.transform.Find("Frozen portrait");
        face.gameObject.SetActive(amount > 0);
        portraitMaterial.color = new Color(1, 1, 1, amount);
    }
    private void Cancel()
    {
        if (transition != null) StopCoroutine(transition);
        transition = null; armed = false; fadingIn = false; textFinished = false; revealed = false; SetBlend(0);
        frozenHasSkin = false;
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
        if (latestPortrait != null) Destroy(latestPortrait); latestPortrait = null;
        if (skinRenderer != null && originalSkinMaterials != null) skinRenderer.sharedMaterials = originalSkinMaterials;
        if (skinMaterials != null) foreach (var material in skinMaterials) if (material != null) Destroy(material);
        skinMaterials = null; originalSkinMaterials = null; skinRenderer = null; latestHasSkin = false;
        if (portraitMaterial != null) Destroy(portraitMaterial);
        if (portraitMesh != null) Destroy(portraitMesh);
        if (maskObject != null) { maskObject.SetActive(false); Destroy(maskObject); }
        maskObject = null;
        if (frame != null) { Destroy(frame); frame = null; }
        ready = false; inFlight = false;
    }
}
