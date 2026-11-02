using UnityEngine;

/// <summary>
/// Test 3's self-contained particle-face visual. It listens to the existing
/// authority controller: High forms a procedural 3D face, Low disturbs the
/// cloud, and Idle returns it to a breathing cloud. Camera/MediaPipe targets
/// can later replace the generated face through SetFaceTargets.
/// </summary>
[DisallowMultipleComponent]
public sealed class ParticleFacePrototype : MonoBehaviour
{
    public enum FaceState { Idle, Low, High }

    [Header("Scene placement")]
    [SerializeField] private Vector3 worldCenter = new Vector3(0f, 2.12f, 0f);
    [SerializeField, Min(100)] private int particleCount = 2400;
    [SerializeField, Min(0.001f)] private float particleSize = 0.022f;

    [Header("Motion")]
    [SerializeField, Min(0.01f)] private float cloudRadius = 0.48f;
    [Tooltip("How deep particles extend inward/outward from the loose cloud shell.")]
    [SerializeField, Range(0f, 0.8f)] private float cloudShellThickness = 0.32f;
    [Tooltip("Slow radial movement that keeps the Idle cloud surface alive.")]
    [SerializeField, Range(0f, 0.3f)] private float cloudSurfaceWaveAmplitude = 0.055f;
    [SerializeField, Range(0.05f, 4f)] private float cloudSurfaceWaveSpeed = 0.75f;
    [SerializeField, Min(0.01f)] private float transitionSpeed = 3.2f;
    [SerializeField, Range(0f, 0.5f)] private float lowJitter = 0.16f;
    [SerializeField, Min(0f)] private float lowRecoverySeconds = 1.4f;
    [SerializeField] private bool enableKeyboardPreview = true;
    [SerializeField] private KeyCode idleKey = KeyCode.I;

    [Header("Colour")]
    [Tooltip("Opacity shared by the rainbow colours in the Idle cloud.")]
    [SerializeField, Range(0f, 1f)] private float cloudColourAlpha = 0.9f;
    [Tooltip("Keeps the Idle cloud bright rather than producing muted rainbow colours.")]
    [SerializeField, Range(0f, 1f)] private float cloudColourSaturation = 0.82f;
    [SerializeField, Range(0f, 1f)] private float cloudColourValue = 1f;
    [SerializeField] private Color shadowColor = new Color(0.20f, 0.07f, 0.10f, 0.95f);
    [SerializeField] private Color skinColor = new Color(1f, 0.55f, 0.36f, 0.95f);
    [SerializeField] private Color highlightColor = new Color(1f, 0.88f, 0.67f, 0.95f);

    [Header("High scan-line face")]
    [Tooltip("Horizontal contours used instead of sparse particles in High mode.")]
    [SerializeField, Range(24, 120)] private int scanLineCount = 76;
    [SerializeField, Range(16, 160)] private int scanLinePoints = 88;
    [SerializeField, Min(0.001f)] private float scanLineWidth = 0.009f;
    [SerializeField, Range(0f, 0.04f)] private float scanLineFlowAmount = 0.006f;
    [SerializeField, Min(0f)] private float scanLineFlowSpeed = 0.65f;
    [SerializeField] private Color scanLineColor = new Color(0.78f, 0.9f, 1f, 0.92f);

    private ParticleSystem particleSystem;
    private ParticleSystem.Particle[] particles;
    private Vector3[] cloudTargets;
    private Color[] cloudColors;
    private Vector3[] faceTargets;
    private Color[] faceColors;
    private LineRenderer[] scanLines;
    private Vector3[] scanLinePositions;
    private GameObject scanLineHost;
    private FaceState state = FaceState.Idle;
    private float lowStartedAt;
    private float scanLineVisibility;
    private Material runtimeMaterial;
    private Material runtimeLineMaterial;

    public int ParticleCount => particleCount;

    private void Awake()
    {
        CreateParticleSystem();
        BuildTargets();
        CreateScanLines();
        SubscribeToAuthorityEvents();
    }

    private void OnDestroy()
    {
        var authority = GetComponent<AuthorityVisualController>();
        if (authority != null)
        {
            authority.HighStarted -= OnHighStarted;
            authority.LowStarted -= OnLowStarted;
            authority.SequenceCancelled -= OnSequenceCancelled;
        }
        if (runtimeMaterial != null) Destroy(runtimeMaterial);
        if (runtimeLineMaterial != null) Destroy(runtimeLineMaterial);
        if (scanLineHost != null) Destroy(scanLineHost);
    }

    private void Update()
    {
        if (!enableKeyboardPreview) return;
        if (Input.GetKeyDown(idleKey)) SetState(FaceState.Idle);
    }

    private void LateUpdate()
    {
        if (particles == null) return;

        float blend = 1f - Mathf.Exp(-transitionSpeed * Time.deltaTime);
        float breathing = 1f + Mathf.Sin(Time.time * 0.8f) * 0.075f;
        float lowEnvelope = state == FaceState.Low
            ? Mathf.Exp(-(Time.time - lowStartedAt) / Mathf.Max(0.01f, lowRecoverySeconds))
            : 0f;

        for (int i = 0; i < particles.Length; i++)
        {
            Vector3 target;
            Color targetColor;
            if (state == FaceState.High)
            {
                target = faceTargets[i];
                targetColor = faceColors[i];
                targetColor.a = 0f;
            }
            else
            {
                Vector3 direction = cloudTargets[i].sqrMagnitude > 0.0001f
                    ? cloudTargets[i].normalized : Vector3.up;
                float phase = i * 0.6180339f;
                float surfaceWave = (Mathf.Sin(Time.time * cloudSurfaceWaveSpeed + phase) +
                                     Mathf.Sin(Time.time * cloudSurfaceWaveSpeed * 0.53f + phase * 1.7f) * 0.5f)
                                    * cloudSurfaceWaveAmplitude;
                target = cloudTargets[i] * breathing + direction * surfaceWave;
                targetColor = cloudColors[i];
                if (lowEnvelope > 0f)
                {
                    float lowPhase = Time.time * 9f + i * 0.6180339f;
                    target += new Vector3(Mathf.Sin(lowPhase), Mathf.Cos(lowPhase * 1.31f), Mathf.Sin(lowPhase * 0.73f))
                        * lowJitter * lowEnvelope;
                }
            }

            particles[i].position = Vector3.Lerp(particles[i].position, target, blend);
            particles[i].startColor = Color.Lerp((Color)particles[i].startColor, targetColor, blend);
        }
        particleSystem.SetParticles(particles, particles.Length);
        UpdateScanLines(blend);
    }

    public void SetState(FaceState nextState)
    {
        if (nextState == FaceState.Low && state != FaceState.Low) lowStartedAt = Time.time;
        state = nextState;
    }

    /// <summary>Reserved for the MediaPipe bridge. Arrays must match particleCount.</summary>
    public bool SetFaceTargets(Vector3[] positions, Color[] colours)
    {
        if (positions == null || positions.Length != particleCount ||
            (colours != null && colours.Length != particleCount)) return false;

        for (int i = 0; i < particleCount; i++)
        {
            faceTargets[i] = positions[i];
            faceColors[i] = colours == null ? skinColor : colours[i];
        }
        return true;
    }

    private void OnHighStarted() => SetState(FaceState.High);
    private void OnLowStarted() => SetState(FaceState.Low);
    private void OnSequenceCancelled() => SetState(FaceState.Idle);

    private void SubscribeToAuthorityEvents()
    {
        var authority = GetComponent<AuthorityVisualController>();
        if (authority == null)
        {
            Debug.LogWarning("ParticleFacePrototype needs AuthorityVisualController for voice-driven state changes.", this);
            return;
        }
        authority.HighStarted += OnHighStarted;
        authority.LowStarted += OnLowStarted;
        authority.SequenceCancelled += OnSequenceCancelled;
    }

    private void CreateParticleSystem()
    {
        var host = new GameObject("ParticleFaceSystem (runtime)");
        host.transform.position = worldCenter;
        particleSystem = host.AddComponent<ParticleSystem>();
        var main = particleSystem.main;
        main.loop = false;
        main.playOnAwake = false;
        main.maxParticles = particleCount;
        main.startLifetime = 10000f;
        main.startSpeed = 0f;
        main.startSize = particleSize;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Local;
        var emission = particleSystem.emission;
        emission.enabled = false;
        var renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        Shader shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            runtimeMaterial = new Material(shader) { color = Color.white };
            renderer.material = runtimeMaterial;
        }
        particleSystem.Emit(particleCount);
        particles = new ParticleSystem.Particle[particleCount];
        particleSystem.GetParticles(particles);
    }

    private void BuildTargets()
    {
        cloudTargets = new Vector3[particleCount];
        cloudColors = new Color[particleCount];
        faceTargets = new Vector3[particleCount];
        faceColors = new Color[particleCount];
        Random.InitState(1103);
        for (int i = 0; i < particleCount; i++)
        {
            // Keep the centre open: a loose shell reads as particles, rather than a solid blue disc.
            float radius = cloudRadius * (1f + Random.Range(-cloudShellThickness, cloudShellThickness));
            cloudTargets[i] = Random.onUnitSphere * radius;
            cloudColors[i] = MakeCloudColour(i);
            faceTargets[i] = MakeProceduralFacePoint();
            faceColors[i] = MakeProceduralFaceColour(faceTargets[i]);
            particles[i].position = cloudTargets[i];
            particles[i].startColor = cloudColors[i];
            particles[i].remainingLifetime = 9999f;
        }
        particleSystem.SetParticles(particles, particleCount);
    }

    private void CreateScanLines()
    {
        scanLineHost = new GameObject("High Scan-Line Face (runtime)");
        scanLineHost.transform.position = worldCenter;
        scanLinePositions = new Vector3[scanLinePoints];

        Shader shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
        if (shader == null) return;

        runtimeLineMaterial = new Material(shader) { color = Color.white };
        scanLines = new LineRenderer[scanLineCount];
        for (int row = 0; row < scanLineCount; row++)
        {
            var lineObject = new GameObject($"Face contour {row + 1:00}");
            lineObject.transform.SetParent(scanLineHost.transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.sharedMaterial = runtimeLineMaterial;
            line.positionCount = scanLinePoints;
            line.widthMultiplier = scanLineWidth;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.textureMode = LineTextureMode.Stretch;
            scanLines[row] = line;
        }
        scanLineHost.SetActive(false);
    }

    private void UpdateScanLines(float blend)
    {
        if (scanLineHost == null || scanLines == null) return;

        float targetVisibility = state == FaceState.High ? 1f : 0f;
        scanLineVisibility = Mathf.Lerp(scanLineVisibility, targetVisibility, blend);
        scanLineHost.SetActive(scanLineVisibility > 0.002f);
        if (scanLineVisibility <= 0.002f) return;

        Color colour = scanLineColor;
        colour.a *= scanLineVisibility;
        for (int row = 0; row < scanLines.Length; row++)
        {
            float normalizedRow = scanLines.Length == 1 ? 0f : (float)row / (scanLines.Length - 1);
            float y = Mathf.Lerp(-0.92f, 0.92f, normalizedRow);
            float halfWidth = 0.68f * Mathf.Sqrt(Mathf.Max(0f, 1f - y * y / (0.92f * 0.92f)));
            for (int point = 0; point < scanLinePositions.Length; point++)
            {
                float normalizedPoint = scanLinePositions.Length == 1 ? 0.5f : (float)point / (scanLinePositions.Length - 1);
                float x = Mathf.Lerp(-halfWidth, halfWidth, normalizedPoint);
                float flow = (Mathf.Sin(Time.time * scanLineFlowSpeed + row * 0.43f + point * 0.18f) +
                              Mathf.Sin(Time.time * scanLineFlowSpeed * 0.61f + row * 0.19f - point * 0.11f) * 0.5f)
                             * scanLineFlowAmount;
                scanLinePositions[point] = new Vector3(x, y, GetProceduralFaceDepth(x, y) + flow);
            }
            scanLines[row].startColor = colour;
            scanLines[row].endColor = colour;
            scanLines[row].SetPositions(scanLinePositions);
        }
    }

    private Vector3 MakeProceduralFacePoint()
    {
        float latitude = Mathf.Lerp(-1.2f, 1.2f, Random.value);
        float longitude = Mathf.Lerp(-1.25f, 1.25f, Random.value);
        float y = Mathf.Sin(latitude) * 0.92f;
        float side = Mathf.Cos(latitude);
        float x = Mathf.Sin(longitude) * side * 0.68f;
        float z = Mathf.Cos(longitude) * side * 0.34f - 0.05f;

        // Facial relief: nose bridge/tip protrude, eye sockets recede, mouth is subtle.
        float nose = Mathf.Exp(-(x * x * 22f + (y - 0.02f) * (y - 0.02f) * 9f));
        float leftEye = Mathf.Exp(-((x + 0.25f) * (x + 0.25f) * 80f + (y - 0.24f) * (y - 0.24f) * 95f));
        float rightEye = Mathf.Exp(-((x - 0.25f) * (x - 0.25f) * 80f + (y - 0.24f) * (y - 0.24f) * 95f));
        float mouth = Mathf.Exp(-(x * x * 30f + (y + 0.35f) * (y + 0.35f) * 150f));
        z += nose * 0.26f - (leftEye + rightEye) * 0.065f + mouth * 0.025f;
        return new Vector3(x, y, z);
    }

    private static float GetProceduralFaceDepth(float x, float y)
    {
        float faceCurve = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x / (0.68f * 0.68f) - y * y / (0.92f * 0.92f)));
        float z = faceCurve * 0.34f - 0.05f;
        float nose = Mathf.Exp(-(x * x * 22f + (y - 0.02f) * (y - 0.02f) * 9f));
        float leftEye = Mathf.Exp(-((x + 0.25f) * (x + 0.25f) * 80f + (y - 0.24f) * (y - 0.24f) * 95f));
        float rightEye = Mathf.Exp(-((x - 0.25f) * (x - 0.25f) * 80f + (y - 0.24f) * (y - 0.24f) * 95f));
        float mouth = Mathf.Exp(-(x * x * 30f + (y + 0.35f) * (y + 0.35f) * 150f));
        return z + nose * 0.26f - (leftEye + rightEye) * 0.065f + mouth * 0.025f;
    }

    private Color MakeProceduralFaceColour(Vector3 point)
    {
        float lighting = Mathf.Clamp01(0.55f + point.x * -0.35f + point.y * 0.18f + point.z * 0.55f);
        Color baseColour = Color.Lerp(shadowColor, skinColor, lighting);
        float highlight = Mathf.Clamp01((point.z - 0.1f) * 2.4f);
        return Color.Lerp(baseColour, highlightColor, highlight * 0.45f);
    }

    private Color MakeCloudColour(int particleIndex)
    {
        // The golden-ratio offset spreads neighbouring particles across the whole spectrum
        // without creating a visible repeating sequence or clustering one colour together.
        float hue = Mathf.Repeat(particleIndex * 0.6180339f + Random.Range(-0.045f, 0.045f), 1f);
        Color colour = Color.HSVToRGB(hue, cloudColourSaturation, cloudColourValue);
        colour.a = cloudColourAlpha;
        return colour;
    }
}
