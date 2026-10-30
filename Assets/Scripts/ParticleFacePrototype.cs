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
    [Tooltip("Visual diameter of each soft particle. Increase to close gaps without adding particles.")]
    [SerializeField, Min(0.001f)] private float particleSize = 0.055f;

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
    [SerializeField] private Color cloudColor = new Color(0.08f, 0.72f, 1f, 0.9f);
    [SerializeField] private Color shadowColor = new Color(0.20f, 0.07f, 0.10f, 0.95f);
    [SerializeField] private Color skinColor = new Color(1f, 0.55f, 0.36f, 0.95f);
    [SerializeField] private Color highlightColor = new Color(1f, 0.88f, 0.67f, 0.95f);

    private ParticleSystem particleSystem;
    private ParticleSystem.Particle[] particles;
    private Vector3[] cloudTargets;
    private Vector3[] faceTargets;
    private Color[] faceColors;
    private FaceState state = FaceState.Idle;
    private float lowStartedAt;
    private Material runtimeMaterial;
    private Texture2D runtimeParticleTexture;

    public int ParticleCount => particleCount;

    private void Awake()
    {
        CreateParticleSystem();
        BuildTargets();
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
        if (runtimeParticleTexture != null) Destroy(runtimeParticleTexture);
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
                targetColor = cloudColor;
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
            runtimeParticleTexture = CreateSoftParticleTexture();
            runtimeMaterial.mainTexture = runtimeParticleTexture;
            renderer.material = runtimeMaterial;
        }
        particleSystem.Emit(particleCount);
        particles = new ParticleSystem.Particle[particleCount];
        particleSystem.GetParticles(particles);
    }

    private void BuildTargets()
    {
        cloudTargets = new Vector3[particleCount];
        faceTargets = new Vector3[particleCount];
        faceColors = new Color[particleCount];
        Random.InitState(1103);
        for (int i = 0; i < particleCount; i++)
        {
            // Keep the centre open: a loose shell reads as particles, rather than a solid blue disc.
            float radius = cloudRadius * (1f + Random.Range(-cloudShellThickness, cloudShellThickness));
            cloudTargets[i] = Random.onUnitSphere * radius;
            faceTargets[i] = MakeProceduralFacePoint();
            faceColors[i] = MakeProceduralFaceColour(faceTargets[i]);
            particles[i].position = cloudTargets[i];
            particles[i].startColor = cloudColor;
            particles[i].remainingLifetime = 9999f;
        }
        particleSystem.SetParticles(particles, particleCount);
    }

    private static Texture2D CreateSoftParticleTexture()
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "Runtime soft particle"
        };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + .5f) / size * 2f - 1f;
                float v = (y + .5f) / size * 2f - 1f;
                float distance = Mathf.Sqrt(u * u + v * v);
                float alpha = 1f - Mathf.SmoothStep(.62f, 1f, distance);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
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

    private Color MakeProceduralFaceColour(Vector3 point)
    {
        float lighting = Mathf.Clamp01(0.55f + point.x * -0.35f + point.y * 0.18f + point.z * 0.55f);
        Color baseColour = Color.Lerp(shadowColor, skinColor, lighting);
        float highlight = Mathf.Clamp01((point.z - 0.1f) * 2.4f);
        return Color.Lerp(baseColour, highlightColor, highlight * 0.45f);
    }
}
