using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using TMPEffects.Components;
using TMPEffects.Components.Animator;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Runs the authority-text effect. TMPEffects can animate the intact text first;
/// then this controller moves, spins, fades, and breaks up each visible TMP character.
/// </summary>
public class AuthorityVisualController : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] private Transform earAnchor;
    [SerializeField] private TMP_Text messageText;
    [Tooltip("Dynamic fallback for recognized Chinese/Japanese/Korean text.")]
    [SerializeField] private Font speechFallbackFont;
    [Tooltip("Optional. If empty, a basic world-space ParticleSystem is created at runtime.")]
    [SerializeField] private ParticleSystem dissolveParticles;

    [Header("Speech: Floating Characters")]
    [SerializeField, Min(1)] private int maximumSpeechCharacters = 24;
    [SerializeField, Min(2)] private int speechCharacterStride = 3;
    [Tooltip("World-space ellipsoid around Head, in Head-local directions.")]
    [SerializeField] private Vector3 speechCloudRadius = new Vector3(0.65f, 0.45f, 0.4f);
    [SerializeField, Min(0f)] private float speechFloatAmplitude = 0.035f;
    [SerializeField, Min(0f)] private float speechFloatSpeed = 0.65f;

    [Header("Score Trigger")]
    [SerializeField, Range(0f, 100f)] private float highAuthorityThreshold = 70f;
    [SerializeField, Range(0f, 100f)] private float keyboardHighScore = 85f;
    [SerializeField, Range(0f, 100f)] private float keyboardLowScore = 20f;
    [Tooltip("Prevents scores near the high threshold from rapidly switching between the two effects.")]
    [SerializeField, Range(0f, 25f)] private float scoreHysteresis = 5f;
    [SerializeField] private bool enableKeyboardTesting = true;
    [SerializeField] private KeyCode playKey = KeyCode.H;
    [SerializeField] private KeyCode lowKey = KeyCode.L;
    [SerializeField] private KeyCode resetKey = KeyCode.R;

    [Header("Sequence Timing")]
    [Tooltip("Let the current TMPEffects animation play this long before taking over the character mesh.")]
    [SerializeField, Min(0f)] private float scatterHoldDuration = 1.5f;
    [Tooltip("Pause after the visual finishes, then restore the text so the next recording can play.")]
    [SerializeField, Min(0f)] private float resetAfterSequenceDelay = 0.8f;
    [Tooltip("Travel time for each character. Characters begin one after another using Character Stagger.")]
    [SerializeField, Min(0.05f)] private float moveDuration = 1.8f;
    [SerializeField, Min(0f)] private float characterStagger = 0.075f;
    [FormerlySerializedAs("fadeDuration"), SerializeField, Min(0.05f)] private float characterFadeDuration = 0.45f;
    [SerializeField] private AnimationCurve fadeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

    [Header("Per-character Path")]
    [Tooltip("Offset from Ear Anchor, in the anchor's local coordinates.")]
    [SerializeField] private Vector3 anchorLocalOffset = new Vector3(0f, 0.15f, 0f);
    [Tooltip("Random arc height in the text object's up direction.")]
    [SerializeField, Min(0f)] private float pathArcHeight = 0.18f;
    [Tooltip("Random sideways and depth variation for each character's curved path.")]
    [SerializeField, Min(0f)] private float pathSpread = 0.12f;
    [Tooltip("Characters land in a small area around the anchor instead of stacking on exactly the same point.")]
    [SerializeField, Min(0f)] private float anchorScatterRadius = 0.025f;
    [SerializeField] private AnimationCurve moveCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [SerializeField, Min(0f)] private float minimumSpinTurns = 0.65f;
    [SerializeField, Min(0f)] private float maximumSpinTurns = 1.45f;
    [Tooltip("Maximum random tilt away from spinning around the text plane's normal.")]
    [SerializeField, Range(0f, 1f)] private float spinAxisTilt = 0.3f;
    [Tooltip("Adds a random final twist so letters do not all arrive in the same orientation.")]
    [SerializeField, Range(0f, 180f)] private float finalTwistRange = 85f;
    [SerializeField] private int randomSeed = 417;

    [Header("Particle Dissolve")]
    [SerializeField, Min(0)] private int particlesPerCharacter = 18;
    [SerializeField, Min(0f)] private float particleLifetime = 0.9f;
    [SerializeField, Min(0f)] private float particleSize = 0.025f;
    [SerializeField, Min(0f)] private float particleInitialSpeed = 0.22f;
    [SerializeField, Min(0f)] private float particleSpawnRadius = 0.018f;
    [SerializeField] private float particleUpwardBias = 0.08f;
    [SerializeField, ColorUsage(true, true)] private Color particleColor = Color.white;

    [Header("Low Authority: Orbit")]
    [Tooltip("Head pivot for the orbit. If empty, the Ear Anchor's parent is used.")]
    [SerializeField] private Transform headOrbitCenter;
    [Tooltip("Orbit axis in the Head object's local coordinates.")]
    [SerializeField] private Vector3 lowOrbitAxisLocal = Vector3.up;
    [SerializeField, Min(0f)] private float lowStartDelay = 0.25f;
    [SerializeField, Min(0.05f)] private float lowOrbitDuration = 2.4f;
    [SerializeField, Min(0f)] private float lowOrbitTurns = 0.8f;
    [SerializeField] private bool rotateTextWithOrbit = true;
    [Tooltip("Keep each glyph facing the viewing camera while its position orbits Head.")]
    [SerializeField] private bool faceCameraDuringLowOrbit = true;
    [SerializeField] private Camera lowOrbitCamera;
    private bool lowOrbitBillboarding;
    private Vector3[][] lowOrbitBaseVertices;
    [HideInInspector, SerializeField, Min(0.05f)] private float lowDriftDuration = 1.8f;
    [HideInInspector, SerializeField, Min(0f)] private float lowDriftDistance = 1.2f;
    #pragma warning disable 0414 // Legacy scene settings retained for compatibility.
    [HideInInspector, SerializeField] private float lowDriftUpwardBias = 0.25f;
    [HideInInspector, SerializeField] private float lowDriftTwist = 35f;
    [SerializeField] private AnimationCurve lowOrbitCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [HideInInspector, SerializeField] private AnimationCurve lowDriftCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [HideInInspector, SerializeField] private AnimationCurve lowFadeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

    #pragma warning restore 0414

    [Header("Low Authority: Fragment Dissolve")]
    [SerializeField, Min(0.1f)] private float lowDissolveDuration = 3.5f;
    [SerializeField, Range(3, 12)] private int lowDissolveGrid = 7;
    [SerializeField, Range(0f, 1f)] private float lowParticleDensity = 0.22f;
    [SerializeField, Min(0.1f)] private float lowDissolveParticleLifetime = 2f;
    [SerializeField, Min(0f)] private float lowDissolveParticleSpeed = 0.12f;

    private sealed class DissolveMesh
    {
        public GameObject gameObject;
        public Mesh mesh;
        public Vector3[] vertices;
        public Color32[] colors;
        public Color32[] baseColors;
        public float[] thresholds;
        public bool[] emitted;
        public bool[] particleAllowed;
    }
    private readonly List<DissolveMesh> lowDissolveMeshes = new List<DissolveMesh>();

    private struct CharacterMotion
    {
        public int materialIndex;
        public int vertexIndex;
        public Vector3 startWorld;
        public Vector3 controlWorld;
        public Vector3 targetWorld;
        public Vector3 spinAxisLocal;
        public float spinTurns;
        public float finalTwist;
        public float startDelay;
        public bool particlesEmitted;
    }

    private enum AuthorityVisualMode
    {
        None,
        Low,
        High
    }

    private Vector3[][] originalVertices;
    private Color32[][] originalColors;
    private readonly List<CharacterMotion> characterMotions = new List<CharacterMotion>();
    private AuthorityVisualMode activeMode;
    private Vector3 initialTextPosition;
    private Quaternion initialTextRotation = Quaternion.identity;
    private Color initialTextColor = Color.white;
    private float authorityScore = 50f;
    private Coroutine sequenceCoroutine;
    private bool meshSnapshotAvailable;
    private bool initialTextStateCaptured;
    private bool animatorWasEnabled;
    private bool animatorWasAnimating;
    private bool animatorWasPausedByDisabling;
    private TMPAnimator tmpAnimator;
    private Material generatedParticleMaterial;
    private TMP_FontAsset runtimeSpeechFont;
    private TMP_FontAsset runtimePrimaryFont;

    private bool speechCloudActive;
    private bool speechCloudFrozen;
    private Vector3[][] speechBaseVertices;
    private Vector3[][] frozenSpeechVertices;

    public float AuthorityScore => authorityScore;
    public bool IsSequencePlaying => sequenceCoroutine != null;

    /// <summary>Show a sparse floating selection for both preview and final recognition results.</summary>
    public void SetSpeechText(string text, bool final = false)
    {
        if (messageText == null || IsSequencePlaying)
            return;
        // Preview and final results use the same sparse selection; never reveal the full transcript.
        string selected = SelectSpeechCharacters(text);
        if (string.IsNullOrEmpty(text))
        {
            speechCloudActive = false;
            speechCloudFrozen = false;
            RestoreAnimatorState();
        }
        else if (!speechCloudActive)
        {
            RememberAnimatorState();
            StopAnimatorForMeshControl();
            speechCloudActive = true;
        }
        meshSnapshotAvailable = false;
        messageText.text = selected;
        messageText.ForceMeshUpdate(true);
        speechBaseVertices = CopySpeechVertices();
        speechCloudFrozen = false;
        if (speechCloudActive)
            UpdateSpeechCloud();
    }

    private string SelectSpeechCharacters(string transcript)
    {
        var result = new System.Text.StringBuilder();
        int eligible = 0;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(transcript ?? "");
        while (elements.MoveNext() && result.Length < Mathf.Max(1, maximumSpeechCharacters))
        {
            string element = elements.GetTextElement();
            if (char.IsWhiteSpace(element, 0) || char.IsPunctuation(element, 0) || char.IsControl(element, 0))
                continue;
            if (eligible++ % Mathf.Max(2, speechCharacterStride) == 0)
                result.Append(element.Replace("<", "＜").Replace(">", "＞"));
        }
        return result.ToString();
    }

    private Vector3[][] CopySpeechVertices()
    {
        var meshes = messageText.textInfo.meshInfo;
        var copy = new Vector3[meshes.Length][];
        for (int i = 0; i < meshes.Length; i++)
            copy[i] = (Vector3[])meshes[i].vertices.Clone();
        return copy;
    }

    private void LateUpdate()
    {
        if (lowOrbitBillboarding)
        {
            FaceLowOrbitCharactersToCamera();
            return;
        }
        if (!speechCloudActive || messageText == null) return;
        if (!speechCloudFrozen)
            UpdateSpeechCloud();
        else if (activeMode == AuthorityVisualMode.Low && frozenSpeechVertices != null)
        {
            // TMP rebuilds its mesh when the low-score sequence changes the text color.
            for (int i = 0; i < frozenSpeechVertices.Length; i++)
                Array.Copy(frozenSpeechVertices[i], messageText.textInfo.meshInfo[i].vertices,
                    frozenSpeechVertices[i].Length);
            messageText.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
        }
    }

    private void UpdateSpeechCloud()
    {
        Transform head = GetOrbitCenter();
        if (head == null || speechBaseVertices == null) return;
        var info = messageText.textInfo;
        Transform textTransform = messageText.transform;
        for (int i = 0; i < info.characterCount; i++)
        {
            var character = info.characterInfo[i];
            if (!character.isVisible) continue;
            int material = character.materialReferenceIndex;
            int vertex = character.vertexIndex;
            Vector3 center = (speechBaseVertices[material][vertex] + speechBaseVertices[material][vertex + 2]) * 0.5f;
            // Golden-angle placement avoids a flat row and keeps existing characters in stable slots.
            float angle = i * 2.399963f + Time.time * speechFloatSpeed * 0.12f;
            float height = Mathf.Sin(i * 1.73f);
            float radial = Mathf.Sqrt(1f - height * height * 0.7f);
            Vector3 offset = new Vector3(Mathf.Cos(angle) * speechCloudRadius.x * radial,
                height * speechCloudRadius.y + Mathf.Sin(Time.time * speechFloatSpeed + i) * speechFloatAmplitude,
                Mathf.Sin(angle) * speechCloudRadius.z * radial);
            Vector3 worldCenter = head.position + head.rotation * offset;
            Quaternion tilt = Quaternion.AngleAxis(Mathf.Sin(i * 2.7f + Time.time * 0.2f) * 24f, textTransform.forward);
            float scale = Mathf.Lerp(0.65f, 1.2f, (Mathf.Sin(i * 3.1f) + 1f) * 0.5f);
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 delta = textTransform.TransformVector(speechBaseVertices[material][vertex + corner] - center);
                info.meshInfo[material].vertices[vertex + corner] = textTransform.InverseTransformPoint(worldCenter + tilt * delta * scale);
            }
        }
        messageText.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
    }

    private void Awake()
    {
        CaptureInitialTextState();
        if (messageText != null && messageText.font != null && speechFallbackFont != null)
        {
            runtimeSpeechFont = TMP_FontAsset.CreateFontAsset(speechFallbackFont);
            runtimePrimaryFont = Instantiate(messageText.font);
            runtimePrimaryFont.fallbackFontAssetTable = new List<TMP_FontAsset>(messageText.font.fallbackFontAssetTable);
            runtimePrimaryFont.fallbackFontAssetTable.Add(runtimeSpeechFont);
            messageText.font = runtimePrimaryFont;
        }
    }

    private void Update()
    {
        if (!enableKeyboardTesting) return;

        if (Input.GetKeyDown(playKey))
            SetAuthorityScore(keyboardHighScore);

        if (Input.GetKeyDown(lowKey))
            SetAuthorityScore(keyboardLowScore);

        if (Input.GetKeyDown(resetKey))
            ResetSequence();
    }

    /// <summary>Set a score from an audio analyzer; high and low bands start their respective visual behaviors.</summary>
    public void SetAuthorityScore(float score)
    {
        authorityScore = Mathf.Clamp(score, 0f, 100f);

        if (authorityScore >= highAuthorityThreshold)
        {
            BeginMode(AuthorityVisualMode.High);
        }
        else if (authorityScore <= highAuthorityThreshold - scoreHysteresis || activeMode == AuthorityVisualMode.None)
        {
            BeginMode(AuthorityVisualMode.Low);
        }
    }

    /// <summary>Public trigger for UnityEvents or other scene components.</summary>
    public void PlaySequence() => BeginMode(AuthorityVisualMode.High);

    /// <summary>Public trigger for testing or UnityEvents that should play the low-score behavior.</summary>
    public void PlayLowSequence() => BeginMode(AuthorityVisualMode.Low);

    private void BeginMode(AuthorityVisualMode mode)
    {
        if (activeMode == mode) return;

        if (messageText == null)
        {
            Debug.LogError("AuthorityVisualController: assign Message Text in the Inspector.", this);
            return;
        }

        if (!initialTextStateCaptured)
            CaptureInitialTextState();

        if (earAnchor == null)
        {
            Debug.LogError("AuthorityVisualController: assign Ear Anchor in the Inspector.", this);
            return;
        }

        if (mode == AuthorityVisualMode.Low && GetOrbitCenter() == null)
        {
            Debug.LogError("AuthorityVisualController: assign a Head Orbit Center or parent Ear Anchor to the Head.", this);
            return;
        }

        if (sequenceCoroutine != null)
        {
            StopCoroutine(sequenceCoroutine);
            sequenceCoroutine = null;
        }

        if (activeMode != AuthorityVisualMode.None)
            RestoreVisualState();

        if (speechCloudActive)
        {
            UpdateSpeechCloud();
            speechCloudFrozen = true;
            frozenSpeechVertices = CopySpeechVertices();
        }
        else
            RememberAnimatorState();
        activeMode = mode;
        sequenceCoroutine = mode == AuthorityVisualMode.High
            ? StartCoroutine(PlayHighSequenceRoutine())
            : StartCoroutine(PlayLowSequenceRoutine());
    }

    public void ResetSequence()
    {
        if (sequenceCoroutine != null)
        {
            StopCoroutine(sequenceCoroutine);
            sequenceCoroutine = null;
        }

        if (!initialTextStateCaptured)
            CaptureInitialTextState();
        RestoreVisualState();
        activeMode = AuthorityVisualMode.None;
        authorityScore = 50f;
    }

    private IEnumerator PlayHighSequenceRoutine()
    {
        // Keep the TMPEffects-driven scatter visible before this controller takes over the mesh.
        if (scatterHoldDuration > 0f)
            yield return new WaitForSeconds(scatterHoldDuration);

        StopAnimatorForMeshControl();
        if (!speechCloudActive)
            messageText.ForceMeshUpdate(true);
        CaptureOriginalMesh();
        BuildCharacterMotions();
        PrepareParticleSystem();

        if (characterMotions.Count == 0)
        {
            Debug.LogWarning("AuthorityVisualController: the TMP text has no visible characters to animate.", this);
            CompleteSequenceAndRestore();
            yield break;
        }

        float totalDuration = (characterMotions.Count - 1) * characterStagger + moveDuration + characterFadeDuration;
        float elapsed = 0f;
        while (elapsed < totalDuration)
        {
            elapsed += Time.deltaTime;
            AnimateCharacters(elapsed);
            yield return null;
        }

        AnimateCharacters(totalDuration);
        if (resetAfterSequenceDelay > 0f)
            yield return new WaitForSeconds(resetAfterSequenceDelay);
        CompleteSequenceAndRestore();
    }

    private IEnumerator PlayLowSequenceRoutine()
    {
        if (lowStartDelay > 0f)
            yield return new WaitForSeconds(lowStartDelay);

        lowOrbitBaseVertices = speechCloudActive && frozenSpeechVertices != null
            ? frozenSpeechVertices : CopySpeechVertices();
        StopAnimatorForMeshControl();
        lowOrbitBillboarding = faceCameraDuringLowOrbit;

        Transform centerTransform = GetOrbitCenter();
        Vector3 center = centerTransform.position;
        Vector3 axis = centerTransform.TransformDirection(lowOrbitAxisLocal).normalized;
        if (axis.sqrMagnitude < 0.001f)
            axis = centerTransform.up;

        Vector3 initialOffset = messageText.transform.position - center;
        float axialOffset = Vector3.Dot(initialOffset, axis);
        Vector3 orbitRadiusOffset = Vector3.ProjectOnPlane(initialOffset, axis);
        if (orbitRadiusOffset.sqrMagnitude < 0.001f)
        {
            orbitRadiusOffset = Vector3.ProjectOnPlane(centerTransform.right, axis);
            if (orbitRadiusOffset.sqrMagnitude < 0.001f)
                orbitRadiusOffset = Vector3.ProjectOnPlane(centerTransform.forward, axis);
            orbitRadiusOffset = orbitRadiusOffset.normalized * 0.35f;
        }

        Quaternion startRotation = messageText.transform.rotation;
        float orbitDegrees = 360f * lowOrbitTurns;
        Vector3 finalRadialOffset = Quaternion.AngleAxis(orbitDegrees, axis) * orbitRadiusOffset;
        Vector3 orbitEndPosition = center + axis * axialOffset + finalRadialOffset;
        Quaternion orbitEndRotation = rotateTextWithOrbit
            ? Quaternion.AngleAxis(orbitDegrees, axis) * startRotation
            : startRotation;

        float totalDuration = lowOrbitDuration;
        float elapsed = 0f;
        while (elapsed < totalDuration)
        {
            elapsed += Time.deltaTime;

            float t = Mathf.Clamp01(elapsed / lowOrbitDuration);
            float eased = lowOrbitCurve != null ? Mathf.Clamp01(lowOrbitCurve.Evaluate(t)) : t;
            float angle = orbitDegrees * eased;
            Vector3 rotatedOffset = Quaternion.AngleAxis(angle, axis) * orbitRadiusOffset;
            messageText.transform.position = center + axis * axialOffset + rotatedOffset;
            messageText.transform.rotation = rotateTextWithOrbit
                ? Quaternion.AngleAxis(angle, axis) * startRotation
                : startRotation;

            yield return null;
        }

        messageText.transform.position = orbitEndPosition;
        messageText.transform.rotation = orbitEndRotation;
        if (lowOrbitBillboarding)
        {
            FaceLowOrbitCharactersToCamera();
            // Preserve the final facing and positions for the fragment handoff.
            if (speechCloudActive) frozenSpeechVertices = CopySpeechVertices();
        }
        lowOrbitBillboarding = false;
        lowOrbitBaseVertices = null;
        yield return PlayLowFragmentDissolve();
        if (resetAfterSequenceDelay > 0f)
            yield return new WaitForSeconds(resetAfterSequenceDelay);
        CompleteSequenceAndRestore();
    }

    private void FaceLowOrbitCharactersToCamera()
    {
        Camera viewingCamera = lowOrbitCamera != null ? lowOrbitCamera : Camera.main;
        if (viewingCamera == null || lowOrbitBaseVertices == null) return;
        Transform textTransform = messageText.transform;
        Vector3 scale = textTransform.lossyScale;
        TMP_TextInfo info = messageText.textInfo;
        for (int i = 0; i < info.characterCount; i++)
        {
            TMP_CharacterInfo character = info.characterInfo[i];
            if (!character.isVisible) continue;
            int material = character.materialReferenceIndex;
            int vertex = character.vertexIndex;
            Vector3[] source = lowOrbitBaseVertices[material];
            Vector3 centerLocal = (source[vertex] + source[vertex + 2]) * 0.5f;
            Vector3 centerWorld = textTransform.TransformPoint(centerLocal);
            Vector3 forward = viewingCamera.orthographic ? viewingCamera.transform.forward
                : centerWorld - viewingCamera.transform.position;
            if (forward.sqrMagnitude < 0.000001f) forward = viewingCamera.transform.forward;
            Quaternion facing = Quaternion.LookRotation(forward.normalized, viewingCamera.transform.up);
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 offset = source[vertex + corner] - centerLocal;
                Vector3 cameraOffset = new Vector3(offset.x * scale.x, offset.y * scale.y, 0f);
                info.meshInfo[material].vertices[vertex + corner] =
                    textTransform.InverseTransformPoint(centerWorld + facing * cameraOffset);
            }
        }
        messageText.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
    }

    private IEnumerator PlayLowFragmentDissolve()
    {
        // StopAnimating can synchronously restore TMP's original line layout. Capture the
        // visible orbit pose first, before handing control from TMP to the dissolve meshes.
        if (speechCloudActive && frozenSpeechVertices != null)
        {
            for (int i = 0; i < frozenSpeechVertices.Length; i++)
                Array.Copy(frozenSpeechVertices[i], messageText.textInfo.meshInfo[i].vertices,
                    frozenSpeechVertices[i].Length);
        }
        CaptureOriginalMesh();
        StopAnimatorForMeshControl();
        PrepareParticleSystem();
        BuildLowDissolveMeshes();
        // Hide the intact glyphs; the subdivided copies retain the same SDF font materials.
        foreach (TMP_MeshInfo meshInfo in messageText.textInfo.meshInfo)
            for (int i = 0; i < meshInfo.colors32.Length; i++)
                meshInfo.colors32[i].a = 0;
        messageText.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);

        float duration = Mathf.Max(0.1f, lowDissolveDuration);
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            UpdateLowDissolve(Mathf.Clamp01(elapsed / duration));
            yield return null;
        }
        UpdateLowDissolve(1f);
        yield return new WaitForSeconds(Mathf.Max(0.1f, lowDissolveParticleLifetime));
        ClearLowDissolveMeshes();
    }

    private void BuildLowDissolveMeshes()
    {
        ClearLowDissolveMeshes();
        TMP_TextInfo info = messageText.textInfo;
        int grid = Mathf.Clamp(lowDissolveGrid, 3, 12);
        var random = new System.Random(randomSeed);
        for (int material = 0; material < info.meshInfo.Length; material++)
        {
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var uv2 = new List<Vector2>();
            var colors = new List<Color32>();
            var triangles = new List<int>();
            var thresholds = new List<float>();
            var particleAllowed = new List<bool>();
            TMP_MeshInfo source = info.meshInfo[material];
            // Use the captured orbit pose, never the live TMP mesh which may have rebuilt.
            Vector3[] orbitVertices = originalVertices[material];
            Color32[] orbitColors = originalColors[material];
            Texture2D atlas = source.material != null ? source.material.mainTexture as Texture2D : null;
            for (int c = 0; c < info.characterCount; c++)
            {
                TMP_CharacterInfo character = info.characterInfo[c];
                if (!character.isVisible || character.materialReferenceIndex != material) continue;
                int v = character.vertexIndex;
                for (int y = 0; y < grid; y++)
                for (int x = 0; x < grid; x++)
                {
                    int first = vertices.Count;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        float u = (x + (corner >= 2 ? 1f : 0f)) / grid;
                        float t = (y + (corner == 1 || corner == 2 ? 1f : 0f)) / grid;
                        vertices.Add(Vector3.Lerp(Vector3.Lerp(orbitVertices[v], orbitVertices[v + 3], u),
                            Vector3.Lerp(orbitVertices[v + 1], orbitVertices[v + 2], u), t));
                        uv.Add(Vector2.Lerp(Vector2.Lerp(source.uvs0[v], source.uvs0[v + 3], u),
                            Vector2.Lerp(source.uvs0[v + 1], source.uvs0[v + 2], u), t));
                        // Preserve SDF scale and packed mapping, rather than interpolating packed UV2 values.
                        uv2.Add(source.uvs2[v + corner]);
                        colors.Add(orbitColors[v + corner]);
                    }
                    triangles.AddRange(new[] { first, first + 1, first + 2, first + 2, first + 3, first });
                    thresholds.Add(0.08f + (float)random.NextDouble() * 0.82f);
                    Vector2 sampleUV = (uv[first] + uv[first + 2]) * 0.5f;
                    // Readable SDF atlases let us avoid emitting dust from the empty glyph padding.
                    particleAllowed.Add(atlas == null || !atlas.isReadable || atlas.GetPixelBilinear(sampleUV.x, sampleUV.y).a >= 0.45f);
                }
            }
            if (vertices.Count == 0) continue;
            var obj = new GameObject("Low Text Dissolve Fragments");
            obj.layer = messageText.gameObject.layer;
            obj.transform.SetParent(messageText.transform, false);
            Mesh mesh = new Mesh { name = "Runtime Low Text Fragments" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, uv2);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = source.material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Renderer textRenderer = messageText.GetComponent<Renderer>();
            if (textRenderer != null)
            {
                renderer.sortingLayerID = textRenderer.sortingLayerID;
                renderer.sortingOrder = textRenderer.sortingOrder;
            }
            lowDissolveMeshes.Add(new DissolveMesh
            {
                gameObject = obj, mesh = mesh, vertices = vertices.ToArray(), colors = colors.ToArray(),
                baseColors = colors.ToArray(), thresholds = thresholds.ToArray(), emitted = new bool[thresholds.Count], particleAllowed = particleAllowed.ToArray()
            });
        }
    }

    private void UpdateLowDissolve(float progress)
    {
        for (int m = 0; m < lowDissolveMeshes.Count; m++)
        {
            DissolveMesh fragment = lowDissolveMeshes[m];
            for (int cell = 0; cell < fragment.thresholds.Length; cell++)
            {
                float erosion = Mathf.Clamp01((progress - fragment.thresholds[cell]) / 0.1f);
                int v = cell * 4;
                for (int corner = 0; corner < 4; corner++)
                {
                    Color32 color = fragment.baseColors[v + corner];
                    color.a = (byte)Mathf.RoundToInt(color.a * (1f - erosion));
                    fragment.colors[v + corner] = color;
                }
                if (erosion <= 0f || fragment.emitted[cell]) continue;
                fragment.emitted[cell] = true;
                var random = new System.Random(randomSeed ^ (m * 104729) ^ (cell * 7919));
                if (!fragment.particleAllowed[cell] || random.NextDouble() > lowParticleDensity) continue;
                Vector3 center = (fragment.vertices[v] + fragment.vertices[v + 2]) * 0.5f;
                dissolveParticles.Emit(new ParticleSystem.EmitParams
                {
                    position = fragment.gameObject.transform.TransformPoint(center),
                    velocity = (RandomInsideUnitSphere(random) + Vector3.up * 0.5f) * lowDissolveParticleSpeed,
                    startColor = fragment.baseColors[v],
                    startSize = particleSize * RandomRange(random, 0.4f, 0.85f),
                    startLifetime = Mathf.Max(0.1f, lowDissolveParticleLifetime)
                }, 1);
            }
            fragment.mesh.colors32 = fragment.colors;
        }
    }

    private void ClearLowDissolveMeshes()
    {
        foreach (DissolveMesh fragment in lowDissolveMeshes)
        {
            if (fragment.gameObject != null) Destroy(fragment.gameObject);
            if (fragment.mesh != null) Destroy(fragment.mesh);
        }
        lowDissolveMeshes.Clear();
    }

    private void CompleteSequenceAndRestore()
    {
        RestoreVisualState();
        activeMode = AuthorityVisualMode.None;
        authorityScore = 50f;
        sequenceCoroutine = null;
    }

    private Transform GetOrbitCenter()
    {
        if (headOrbitCenter != null)
            return headOrbitCenter;
        if (earAnchor != null && earAnchor.parent != null)
            return earAnchor.parent;
        return earAnchor;
    }

    private void CaptureInitialTextState()
    {
        if (messageText == null)
            return;

        initialTextPosition = messageText.transform.position;
        initialTextRotation = messageText.transform.rotation;
        initialTextColor = messageText.color;
        tmpAnimator = messageText.GetComponent<TMPAnimator>();
        initialTextStateCaptured = true;
    }

    private void RememberAnimatorState()
    {
        tmpAnimator = messageText.GetComponent<TMPAnimator>();
        animatorWasEnabled = tmpAnimator != null && tmpAnimator.enabled;
        animatorWasAnimating = tmpAnimator != null && tmpAnimator.IsAnimating;
        animatorWasPausedByDisabling = false;
    }

    private void StopAnimatorForMeshControl()
    {
        if (tmpAnimator == null || !tmpAnimator.isActiveAndEnabled)
            return;

        if (tmpAnimator.UpdateFrom == UpdateFrom.Script)
        {
            // Script-driven TMPEffects cannot be stopped with StopAnimating().
            tmpAnimator.enabled = false;
            animatorWasPausedByDisabling = true;
        }
        else
        {
            tmpAnimator.StopAnimating();
        }
    }

    private void RestoreAnimatorState()
    {
        if (tmpAnimator == null || !animatorWasEnabled)
            return;

        if (!tmpAnimator.enabled)
            tmpAnimator.enabled = true;

        if (!animatorWasPausedByDisabling && animatorWasAnimating && tmpAnimator.isActiveAndEnabled &&
            tmpAnimator.UpdateFrom != UpdateFrom.Script)
        {
            tmpAnimator.StartAnimating();
        }
    }

    private void CaptureOriginalMesh()
    {
        TMP_TextInfo textInfo = messageText.textInfo;
        originalVertices = new Vector3[textInfo.meshInfo.Length][];
        originalColors = new Color32[textInfo.meshInfo.Length][];

        for (int i = 0; i < textInfo.meshInfo.Length; i++)
        {
            originalVertices[i] = (Vector3[])textInfo.meshInfo[i].vertices.Clone();
            originalColors[i] = (Color32[])textInfo.meshInfo[i].colors32.Clone();
        }

        meshSnapshotAvailable = true;
    }

    private void BuildCharacterMotions()
    {
        characterMotions.Clear();
        TMP_TextInfo textInfo = messageText.textInfo;
        Transform textTransform = messageText.transform;
        int visibleIndex = 0;

        for (int i = 0; i < textInfo.characterCount; i++)
        {
            TMP_CharacterInfo character = textInfo.characterInfo[i];
            if (!character.isVisible)
                continue;

            int materialIndex = character.materialReferenceIndex;
            int vertexIndex = character.vertexIndex;
            if (materialIndex < 0 || materialIndex >= originalVertices.Length || vertexIndex + 3 >= originalVertices[materialIndex].Length)
                continue;

            Vector3[] vertices = originalVertices[materialIndex];
            Vector3 centerLocal = (vertices[vertexIndex] + vertices[vertexIndex + 1] +
                                   vertices[vertexIndex + 2] + vertices[vertexIndex + 3]) * 0.25f;
            Vector3 startWorld = textTransform.TransformPoint(centerLocal);

            System.Random random = new System.Random(randomSeed + i * 7919);
            Vector3 anchorBase = earAnchor.TransformPoint(anchorLocalOffset);
            Vector3 anchorJitter = RandomInsideUnitSphere(random) * anchorScatterRadius;
            Vector3 targetWorld = anchorBase + anchorJitter;

            Vector3 sideOffset = textTransform.right * RandomRange(random, -pathSpread, pathSpread) +
                                 textTransform.forward * RandomRange(random, -pathSpread, pathSpread);
            float arc = pathArcHeight * RandomRange(random, 0.35f, 1f);
            if (random.NextDouble() < 0.5)
                arc = -arc;
            Vector3 controlWorld = Vector3.Lerp(startWorld, targetWorld, 0.5f) +
                                   textTransform.up * arc + sideOffset;

            Vector3 randomTilt = new Vector3(
                RandomRange(random, -spinAxisTilt, spinAxisTilt),
                RandomRange(random, -spinAxisTilt, spinAxisTilt),
                RandomRange(random, -spinAxisTilt, spinAxisTilt));
            Vector3 spinAxisLocal = (Vector3.forward + randomTilt).normalized;
            if (spinAxisLocal.sqrMagnitude < 0.001f)
                spinAxisLocal = Vector3.forward;

            characterMotions.Add(new CharacterMotion
            {
                materialIndex = materialIndex,
                vertexIndex = vertexIndex,
                startWorld = startWorld,
                controlWorld = controlWorld,
                targetWorld = targetWorld,
                spinAxisLocal = spinAxisLocal,
                spinTurns = RandomRange(random, minimumSpinTurns, maximumSpinTurns),
                finalTwist = RandomRange(random, -finalTwistRange, finalTwistRange),
                startDelay = visibleIndex * characterStagger,
                particlesEmitted = false
            });

            visibleIndex++;
        }
    }

    private void AnimateCharacters(float elapsed)
    {
        TMP_TextInfo textInfo = messageText.textInfo;
        for (int i = 0; i < textInfo.meshInfo.Length; i++)
        {
            Array.Copy(originalVertices[i], textInfo.meshInfo[i].vertices, originalVertices[i].Length);
            Array.Copy(originalColors[i], textInfo.meshInfo[i].colors32, originalColors[i].Length);
        }

        Transform textTransform = messageText.transform;
        for (int motionIndex = 0; motionIndex < characterMotions.Count; motionIndex++)
        {
            CharacterMotion motion = characterMotions[motionIndex];
            float localTime = elapsed - motion.startDelay;
            float rawMove = Mathf.Clamp01(localTime / moveDuration);
            float easedMove = moveCurve != null ? Mathf.Clamp01(moveCurve.Evaluate(rawMove)) : rawMove;

            Vector3 a = Vector3.Lerp(motion.startWorld, motion.controlWorld, easedMove);
            Vector3 b = Vector3.Lerp(motion.controlWorld, motion.targetWorld, easedMove);
            Vector3 currentCenter = Vector3.Lerp(a, b, easedMove);

            float angle = (360f * motion.spinTurns + motion.finalTwist) * easedMove;
            Vector3 spinAxisWorld = textTransform.TransformDirection(motion.spinAxisLocal).normalized;
            Quaternion rotation = Quaternion.AngleAxis(angle, spinAxisWorld);
            int materialIndex = motion.materialIndex;
            int vertexIndex = motion.vertexIndex;

            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 originalWorld = textTransform.TransformPoint(originalVertices[materialIndex][vertexIndex + corner]);
                Vector3 animatedWorld = currentCenter + rotation * (originalWorld - motion.startWorld);
                textInfo.meshInfo[materialIndex].vertices[vertexIndex + corner] = textTransform.InverseTransformPoint(animatedWorld);
            }

            float fadeProgress = Mathf.Clamp01((localTime - moveDuration) / characterFadeDuration);
            float fadeAmount = fadeCurve != null ? Mathf.Clamp01(fadeCurve.Evaluate(fadeProgress)) : fadeProgress;
            byte alpha = (byte)Mathf.RoundToInt(255f * (1f - fadeAmount));
            Color32[] colors = textInfo.meshInfo[materialIndex].colors32;
            Color32[] baseColors = originalColors[materialIndex];
            for (int corner = 0; corner < 4; corner++)
            {
                Color32 color = baseColors[vertexIndex + corner];
                color.a = (byte)Mathf.RoundToInt(color.a * (alpha / 255f));
                colors[vertexIndex + corner] = color;
            }

            if (localTime >= moveDuration && !motion.particlesEmitted)
            {
                EmitParticleBurst(motion.targetWorld, motionIndex);
                motion.particlesEmitted = true;
                characterMotions[motionIndex] = motion;
            }
        }

        messageText.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
    }

    private void EmitParticleBurst(Vector3 center, int characterIndex)
    {
        if (dissolveParticles == null || particlesPerCharacter <= 0)
            return;

        System.Random random = new System.Random(randomSeed ^ (characterIndex * 104729));
        for (int i = 0; i < particlesPerCharacter; i++)
        {
            Vector3 direction = RandomInsideUnitSphere(random);
            if (direction.sqrMagnitude < 0.001f)
                direction = Vector3.up;

            ParticleSystem.EmitParams emit = new ParticleSystem.EmitParams
            {
                position = center + RandomInsideUnitSphere(random) * particleSpawnRadius,
                velocity = direction.normalized * particleInitialSpeed + Vector3.up * particleUpwardBias,
                startColor = particleColor,
                startSize = particleSize,
                startLifetime = particleLifetime
            };
            dissolveParticles.Emit(emit, 1);
        }
    }

    private void PrepareParticleSystem()
    {
        if (dissolveParticles == null)
        {
            GameObject particleObject = new GameObject("Text Dissolve Particles");
            particleObject.transform.SetParent(transform, false);
            dissolveParticles = particleObject.AddComponent<ParticleSystem>();
            ConfigureDefaultParticleSystem(dissolveParticles);
        }

        ParticleSystem.MainModule main = dissolveParticles.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.Max(main.maxParticles, characterMotions.Count * particlesPerCharacter);

        ParticleSystem.EmissionModule emission = dissolveParticles.emission;
        emission.enabled = false;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = dissolveParticles.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient alphaGradient = new Gradient();
        alphaGradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(alphaGradient);

        ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = dissolveParticles.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f,
            new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f)));

        ParticleSystemRenderer particleRenderer = dissolveParticles.GetComponent<ParticleSystemRenderer>();
        if (particleRenderer != null)
        {
            particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            if (particleRenderer.sharedMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null)
                    shader = Shader.Find("Particles/Standard Unlit");
                if (shader != null)
                {
                    generatedParticleMaterial = new Material(shader);
                    generatedParticleMaterial.name = "Runtime Text Dissolve Particle Material";
                    particleRenderer.sharedMaterial = generatedParticleMaterial;
                }
            }
        }

        if (!dissolveParticles.isPlaying)
            dissolveParticles.Play();
    }

    private void ConfigureDefaultParticleSystem(ParticleSystem particleSystem)
    {
        ParticleSystem.MainModule main = particleSystem.main;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = particleLifetime;
        main.startSpeed = 0f;
        main.startSize = particleSize;
        main.startColor = particleColor;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 10000;

        ParticleSystem.EmissionModule emission = particleSystem.emission;
        emission.enabled = false;

        ParticleSystem.ShapeModule shape = particleSystem.shape;
        shape.enabled = false;
    }

    private void RestoreOriginalMesh()
    {
        if (messageText == null || originalVertices == null || originalColors == null)
            return;

        messageText.ForceMeshUpdate(true);
        TMP_TextInfo textInfo = messageText.textInfo;
        int count = Mathf.Min(textInfo.meshInfo.Length, originalVertices.Length);
        for (int i = 0; i < count; i++)
        {
            int vertexCount = Mathf.Min(textInfo.meshInfo[i].vertices.Length, originalVertices[i].Length);
            int colorCount = Mathf.Min(textInfo.meshInfo[i].colors32.Length, originalColors[i].Length);
            Array.Copy(originalVertices[i], textInfo.meshInfo[i].vertices, vertexCount);
            Array.Copy(originalColors[i], textInfo.meshInfo[i].colors32, colorCount);
        }
        messageText.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
        meshSnapshotAvailable = false;
    }

    private void RestoreVisualState()
    {
        lowOrbitBillboarding = false;
        lowOrbitBaseVertices = null;
        ClearLowDissolveMeshes();
        bool clearSpeech = speechCloudActive;
        speechCloudActive = false;
        speechCloudFrozen = false;
        speechBaseVertices = null;
        frozenSpeechVertices = null;
        if (messageText != null)
        {
            if (initialTextStateCaptured)
            {
                messageText.transform.position = initialTextPosition;
                messageText.transform.rotation = initialTextRotation;
                messageText.color = initialTextColor;
            }

            if (meshSnapshotAvailable)
                RestoreOriginalMesh();
        }

        if (dissolveParticles != null)
            dissolveParticles.Clear();

        RestoreAnimatorState();
        if (clearSpeech && messageText != null)
        {
            messageText.text = "";
            messageText.ForceMeshUpdate(true);
        }
    }

    private static float RandomRange(System.Random random, float min, float max)
    {
        return min + (float)random.NextDouble() * (max - min);
    }

    private static Vector3 RandomInsideUnitSphere(System.Random random)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            Vector3 point = new Vector3(
                RandomRange(random, -1f, 1f),
                RandomRange(random, -1f, 1f),
                RandomRange(random, -1f, 1f));
            if (point.sqrMagnitude <= 1f)
                return point;
        }

        return Vector3.up;
    }

    private void OnValidate()
    {
        highAuthorityThreshold = Mathf.Clamp(highAuthorityThreshold, 0f, 100f);
        keyboardHighScore = Mathf.Clamp(keyboardHighScore, 0f, 100f);
        keyboardLowScore = Mathf.Clamp(keyboardLowScore, 0f, 100f);
        scoreHysteresis = Mathf.Clamp(scoreHysteresis, 0f, 25f);
        scatterHoldDuration = Mathf.Max(0f, scatterHoldDuration);
        resetAfterSequenceDelay = Mathf.Max(0f, resetAfterSequenceDelay);
        moveDuration = Mathf.Max(0.05f, moveDuration);
        characterStagger = Mathf.Max(0f, characterStagger);
        characterFadeDuration = Mathf.Max(0.05f, characterFadeDuration);
        maximumSpinTurns = Mathf.Max(minimumSpinTurns, maximumSpinTurns);
        particlesPerCharacter = Mathf.Max(0, particlesPerCharacter);
        particleLifetime = Mathf.Max(0f, particleLifetime);
        particleSize = Mathf.Max(0f, particleSize);
        particleInitialSpeed = Mathf.Max(0f, particleInitialSpeed);
        particleSpawnRadius = Mathf.Max(0f, particleSpawnRadius);
        lowStartDelay = Mathf.Max(0f, lowStartDelay);
        lowOrbitDuration = Mathf.Max(0.05f, lowOrbitDuration);
        lowOrbitTurns = Mathf.Max(0f, lowOrbitTurns);
        lowDriftDuration = Mathf.Max(0.05f, lowDriftDuration);
        lowDriftDistance = Mathf.Max(0f, lowDriftDistance);
    }

    private void OnDestroy()
    {
        ClearLowDissolveMeshes();
        if (runtimePrimaryFont != null) Destroy(runtimePrimaryFont);
        if (runtimeSpeechFont != null)
        {
            foreach (Texture2D atlas in runtimeSpeechFont.atlasTextures)
                if (atlas != null) Destroy(atlas);
            if (runtimeSpeechFont.material != null) Destroy(runtimeSpeechFont.material);
            Destroy(runtimeSpeechFont);
        }
        if (generatedParticleMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(generatedParticleMaterial);
            else
                DestroyImmediate(generatedParticleMaterial);
        }

    }
}
