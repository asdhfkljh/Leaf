using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

/// <summary>
/// Local desktop bridge: Unity owns the webcam, and the repository's existing
/// Python MediaPipe Face Landmarker returns 478 3D landmarks and RGB samples.
/// No webcam image is written to disk or sent over the network.
/// </summary>
[DisallowMultipleComponent]
public sealed class MediaPipeParticleFaceBridge : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ParticleFacePrototype particleFace;
    [SerializeField] private AuthorityVisualController authority;

    [Header("Local MediaPipe worker")]
    [SerializeField] private string pythonExecutable = "Tools/FaceFusion/.venv/bin/python";
    [SerializeField] private string cameraDevice = "";
    [SerializeField, Range(2f, 15f)] private float samplesPerSecond = 8f;
    [SerializeField, Range(0.1f, 3f)] private float faceWidth = 1.28f;
    [SerializeField, Range(0.1f, 3f)] private float faceHeight = 1.70f;
    [SerializeField, Range(0.05f, 2f)] private float faceDepth = 1.15f;
    [Tooltip("Uniform scale applied to the detected face only; the Idle cloud is unchanged.")]
    [SerializeField, Range(0.25f, 8f)] private float faceScale = 4f;
    [SerializeField, Range(0f, 0.05f)] private float particleSpread = 0.010f;

    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    private Process worker;
    private WebCamTexture webcam;
    private Texture2D frame;
    private bool ready, inFlight, failed;
    private int requestId, session;
    private float nextSampleAt;
    private Vector3[] positions;
    private Color[] colours;

    [Serializable] private class Request { public int id; public string image; public int rotation; }
    [Serializable] private class Reply
    {
        public string type;
        public string message;
        public int id;
        public bool valid;
        public float[] landmarks_flat;
        public float[] landmark_rgb_flat;
    }

    private void OnEnable()
    {
        if (particleFace == null) particleFace = GetComponent<ParticleFacePrototype>();
        if (authority == null) authority = GetComponent<AuthorityVisualController>();
        if (particleFace == null)
        {
            Debug.LogError("MediaPipeParticleFaceBridge requires ParticleFacePrototype.", this);
            enabled = false;
            return;
        }
        StartCoroutine(InitializeCamera());
    }

    private System.Collections.IEnumerator InitializeCamera()
    {
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (!isActiveAndEnabled || !Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogWarning("Particle face: webcam permission was not granted.", this);
            yield break;
        }
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogWarning("Particle face: no webcam found.", this);
            yield break;
        }
        string device = string.IsNullOrEmpty(cameraDevice) ? WebCamTexture.devices[0].name : cameraDevice;
        webcam = new WebCamTexture(device, 640, 480, 15);
        webcam.Play();
        StartWorker();
    }

    private void Update()
    {
        DrainReplies();
        if (!ready || failed || inFlight || webcam == null || !webcam.didUpdateThisFrame || Time.unscaledTime < nextSampleAt)
            return;
        if (webcam.width < 32 || webcam.height < 32) return;
        if (frame == null || frame.width != webcam.width || frame.height != webcam.height)
        {
            if (frame != null) Destroy(frame);
            frame = new Texture2D(webcam.width, webcam.height, TextureFormat.RGB24, false);
        }
        Color32[] pixels = webcam.GetPixels32();
        if (webcam.videoVerticallyMirrored) FlipRows(pixels, webcam.width, webcam.height);
        frame.SetPixels32(pixels);
        frame.Apply(false, false);
        int id = ++requestId;
        string payload = JsonUtility.ToJson(new Request
        {
            id = id,
            image = Convert.ToBase64String(frame.EncodeToJPG(85)),
            rotation = webcam.videoRotationAngle
        });
        inFlight = true;
        nextSampleAt = Time.unscaledTime + 1f / samplesPerSecond;
        Process current = worker;
        int currentSession = session;
        Task.Run(() =>
        {
            try { current.StandardInput.WriteLine(payload); current.StandardInput.Flush(); }
            catch { if (currentSession == session) messages.Enqueue("{\"type\":\"error\",\"message\":\"MediaPipe worker input closed\"}"); }
        });
    }

    private void DrainReplies()
    {
        while (messages.TryDequeue(out string json))
        {
            Reply reply;
            try { reply = JsonUtility.FromJson<Reply>(json); }
            catch { continue; }
            if (reply == null) continue;
            if (reply.type == "ready")
            {
                ready = true;
                Debug.Log("Particle face: local MediaPipe Face Landmarker ready.", this);
            }
            else if (reply.type == "error")
            {
                inFlight = false;
                Debug.LogWarning("Particle face MediaPipe: " + reply.message, this);
            }
            else if (reply.type == "face" && reply.id == requestId)
            {
                inFlight = false;
                if (reply.valid && BuildParticleTargets(reply.landmarks_flat, reply.landmark_rgb_flat))
                    particleFace.SetFaceTargets(positions, colours);
            }
        }
    }

    private bool BuildParticleTargets(float[] landmarks, float[] rgb)
    {
        const int landmarkCount = 478;
        if (landmarks == null || rgb == null || landmarks.Length != landmarkCount * 3 || rgb.Length != landmarkCount * 3)
            return false;
        int count = particleFace.ParticleCount;
        if (positions == null || positions.Length != count)
        {
            positions = new Vector3[count];
            colours = new Color[count];
        }
        for (int i = 0; i < count; i++)
        {
            int index = i % landmarkCount;
            int offset = index * 3;
            // Face Landmarker: x/y are image normalized; z is relative depth and is negative toward the camera.
            float x = (landmarks[offset] - .5f) * faceWidth;
            float y = (.5f - landmarks[offset + 1]) * faceHeight;
            float z = -landmarks[offset + 2] * faceDepth;
            Vector3 spread = DeterministicSpread(i) * particleSpread;
            positions[i] = new Vector3(x, y, z) * faceScale + spread;
            colours[i] = new Color(rgb[offset], rgb[offset + 1], rgb[offset + 2], .96f);
        }
        return true;
    }

    private static Vector3 DeterministicSpread(int index)
    {
        float a = index * 2.3999632f;
        float r = Mathf.Sqrt((index % 11) / 10f);
        return new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a * 1.31f) * r, Mathf.Sin(a) * .45f * r);
    }

    private static void FlipRows(Color32[] pixels, int width, int height)
    {
        for (int y = 0; y < height / 2; y++)
            for (int x = 0; x < width; x++)
            {
                int a = y * width + x, b = (height - y - 1) * width + x;
                Color32 temporary = pixels[a]; pixels[a] = pixels[b]; pixels[b] = temporary;
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
            worker = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "-u \"" + script + "\"",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            worker.OutputDataReceived += (_, eventArgs) =>
            {
                if (currentSession == session && !string.IsNullOrEmpty(eventArgs.Data)) messages.Enqueue(eventArgs.Data);
            };
            worker.ErrorDataReceived += (_, __) => { };
            worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
        }
        catch (Exception exception)
        {
            failed = true;
            Debug.LogWarning("Particle face MediaPipe unavailable: " + exception.Message, this);
        }
    }

    private void OnDisable()
    {
        StopAllCoroutines(); ++session; ready = false; inFlight = false;
        if (webcam != null) { webcam.Stop(); Destroy(webcam); webcam = null; }
        if (worker != null)
        {
            try { if (!worker.HasExited) worker.Kill(); } catch (InvalidOperationException) { }
            worker.Dispose(); worker = null;
        }
        if (frame != null) { Destroy(frame); frame = null; }
    }
}
