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
    private int[] meshTriangles;
    private int[] sampleA, sampleB, sampleC;
    private Vector3[] sampleWeights;

    [Serializable] private class Request { public int id; public string image; public int rotation; }
    [Serializable] private class Reply
    {
        public string type;
        public string message;
        public int id;
        public bool valid;
        public float[] landmarks_flat;
        public float[] landmark_rgb_flat;
        public int[] mesh_triangles_flat;
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
                if (reply.valid && BuildParticleTargets(reply.landmarks_flat, reply.landmark_rgb_flat, reply.mesh_triangles_flat))
                    particleFace.SetFaceTargets(positions, colours);
            }
        }
    }

    private bool BuildParticleTargets(float[] landmarks, float[] rgb, int[] triangles)
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
        UpdateMeshSamplingPlan(landmarks, triangles, count);
        // Put the detected face's own ear-line at the local origin. The particle
        // host is then placed at the authored ear-anchor midpoint on High, so a
        // participant standing off-centre in the webcam cannot shift the face
        // away from the flying text.
        const int leftFaceSide = 234, rightFaceSide = 454;
        int leftOffset = leftFaceSide * 3, rightOffset = rightFaceSide * 3;
        float centerX = (landmarks[leftOffset] + landmarks[rightOffset]) * 0.5f;
        float centerY = (landmarks[leftOffset + 1] + landmarks[rightOffset + 1]) * 0.5f;
        float centerZ = (landmarks[leftOffset + 2] + landmarks[rightOffset + 2]) * 0.5f;
        for (int i = 0; i < count; i++)
        {
            int a = sampleA == null ? i % landmarkCount : sampleA[i];
            int b = sampleB == null ? a : sampleB[i];
            int c = sampleC == null ? a : sampleC[i];
            Vector3 weights = sampleWeights == null ? Vector3.right : sampleWeights[i];
            int offsetA = a * 3, offsetB = b * 3, offsetC = c * 3;
            // Face Landmarker: x/y are image normalized; z is relative depth and is negative toward the camera.
            float x = ((landmarks[offsetA] * weights.x + landmarks[offsetB] * weights.y + landmarks[offsetC] * weights.z) - centerX) * faceWidth;
            float y = (centerY - (landmarks[offsetA + 1] * weights.x + landmarks[offsetB + 1] * weights.y + landmarks[offsetC + 1] * weights.z)) * faceHeight;
            float z = -((landmarks[offsetA + 2] * weights.x + landmarks[offsetB + 2] * weights.y + landmarks[offsetC + 2] * weights.z) - centerZ) * faceDepth;
            Vector3 spread = DeterministicSpread(i) * particleSpread;
            positions[i] = new Vector3(x, y, z) * faceScale + spread;
            colours[i] = new Color(
                rgb[offsetA] * weights.x + rgb[offsetB] * weights.y + rgb[offsetC] * weights.z,
                rgb[offsetA + 1] * weights.x + rgb[offsetB + 1] * weights.y + rgb[offsetC + 1] * weights.z,
                rgb[offsetA + 2] * weights.x + rgb[offsetB + 2] * weights.y + rgb[offsetC + 2] * weights.z, .96f);
        }
        return true;
    }

    private void UpdateMeshSamplingPlan(float[] landmarks, int[] triangles, int count)
    {
        if (triangles == null || triangles.Length < 3 || triangles.Length % 3 != 0 || sampleA != null) return;

        meshTriangles = (int[])triangles.Clone();
        int triangleCount = meshTriangles.Length / 3;
        float[] cumulativeAreas = new float[triangleCount];
        float totalArea = 0f;
        for (int i = 0; i < triangleCount; i++)
        {
            int a = meshTriangles[i * 3] * 3, b = meshTriangles[i * 3 + 1] * 3, c = meshTriangles[i * 3 + 2] * 3;
            float twiceArea = Mathf.Abs((landmarks[b] - landmarks[a]) * (landmarks[c + 1] - landmarks[a + 1]) -
                                        (landmarks[c] - landmarks[a]) * (landmarks[b + 1] - landmarks[a + 1]));
            totalArea += twiceArea;
            cumulativeAreas[i] = totalArea;
        }
        if (totalArea < 0.000001f) return;

        sampleA = new int[count]; sampleB = new int[count]; sampleC = new int[count]; sampleWeights = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float pick = Hash01(i * 3 + 1) * totalArea;
            int triangle = System.Array.BinarySearch(cumulativeAreas, pick);
            if (triangle < 0) triangle = ~triangle;
            triangle = Mathf.Clamp(triangle, 0, triangleCount - 1);
            sampleA[i] = meshTriangles[triangle * 3]; sampleB[i] = meshTriangles[triangle * 3 + 1]; sampleC[i] = meshTriangles[triangle * 3 + 2];
            float root = Mathf.Sqrt(Hash01(i * 3 + 2));
            float second = Hash01(i * 3 + 3);
            sampleWeights[i] = new Vector3(1f - root, root * (1f - second), root * second);
        }
    }

    private static float Hash01(int value)
    {
        uint hash = (uint)value * 747796405u + 2891336453u;
        hash = (hash >> ((int)(hash >> 28) + 4)) ^ hash;
        hash *= 277803737u;
        return ((hash >> 22) ^ hash) * (1f / 4294967295f);
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
