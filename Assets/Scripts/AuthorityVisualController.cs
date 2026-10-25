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
    [Tooltip("Optional. If empty, a basic world-space ParticleSystem is created at runtime.")]
    [SerializeField] private ParticleSystem dissolveParticles;

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

    [Header("Low Authority: Orbit and Drift")]
    [Tooltip("Head pivot for the orbit. If empty, the Ear Anchor's parent is used.")]
    [SerializeField] private Transform headOrbitCenter;
    [Tooltip("Orbit axis in the Head object's local coordinates.")]
    [SerializeField] private Vector3 lowOrbitAxisLocal = Vector3.up;
    [SerializeField, Min(0f)] private float lowStartDelay = 0.25f;
    [SerializeField, Min(0.05f)] private float lowOrbitDuration = 2.4f;
    [SerializeField, Min(0f)] private float lowOrbitTurns = 0.8f;
    [SerializeField] private bool rotateTextWithOrbit = true;
    [SerializeField, Min(0.05f)] private float lowDriftDuration = 1.8f;
    [SerializeField, Min(0f)] private float lowDriftDistance = 1.2f;
    [SerializeField] private float lowDriftUpwardBias = 0.25f;
    [SerializeField] private float lowDriftTwist = 35f;
    [SerializeField] private AnimationCurve lowOrbitCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [SerializeField] private AnimationCurve lowDriftCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [SerializeField] private AnimationCurve lowFadeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

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

    public float AuthorityScore => authorityScore;
    public bool IsSequencePlaying => sequenceCoroutine != null;

    private void Awake()
    {
        CaptureInitialTextState();
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
        Vector3 radialDirection = finalRadialOffset.normalized;
        Vector3 tangentDirection = Vector3.Cross(axis, radialDirection).normalized;
        Vector3 driftDirection = (radialDirection * 0.65f + tangentDirection * 0.55f + axis * lowDriftUpwardBias).normalized;
        if (driftDirection.sqrMagnitude < 0.001f)
            driftDirection = radialDirection;

        Vector3 driftEndPosition = orbitEndPosition + driftDirection * lowDriftDistance;
        Quaternion orbitEndRotation = rotateTextWithOrbit
            ? Quaternion.AngleAxis(orbitDegrees, axis) * startRotation
            : startRotation;

        float totalDuration = lowOrbitDuration + lowDriftDuration;
        float elapsed = 0f;
        while (elapsed < totalDuration)
        {
            elapsed += Time.deltaTime;

            if (elapsed < lowOrbitDuration)
            {
                float t = Mathf.Clamp01(elapsed / lowOrbitDuration);
                float eased = lowOrbitCurve != null ? Mathf.Clamp01(lowOrbitCurve.Evaluate(t)) : t;
                float angle = orbitDegrees * eased;
                Vector3 rotatedOffset = Quaternion.AngleAxis(angle, axis) * orbitRadiusOffset;
                messageText.transform.position = center + axis * axialOffset + rotatedOffset;
                messageText.transform.rotation = rotateTextWithOrbit
                    ? Quaternion.AngleAxis(angle, axis) * startRotation
                    : startRotation;
            }
            else
            {
                float t = Mathf.Clamp01((elapsed - lowOrbitDuration) / lowDriftDuration);
                float eased = lowDriftCurve != null ? Mathf.Clamp01(lowDriftCurve.Evaluate(t)) : t;
                messageText.transform.position = Vector3.Lerp(orbitEndPosition, driftEndPosition, eased);
                messageText.transform.rotation = orbitEndRotation * Quaternion.AngleAxis(lowDriftTwist * eased, Vector3.forward);

                float fade = lowFadeCurve != null ? Mathf.Clamp01(lowFadeCurve.Evaluate(t)) : t;
                Color color = initialTextColor;
                color.a = initialTextColor.a * (1f - fade);
                messageText.color = color;
            }

            yield return null;
        }

        messageText.transform.position = driftEndPosition;
        messageText.transform.rotation = orbitEndRotation * Quaternion.AngleAxis(lowDriftTwist, Vector3.forward);
        Color invisible = initialTextColor;
        invisible.a = 0f;
        messageText.color = invisible;
        if (resetAfterSequenceDelay > 0f)
            yield return new WaitForSeconds(resetAfterSequenceDelay);
        CompleteSequenceAndRestore();
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
        if (generatedParticleMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(generatedParticleMaterial);
            else
                DestroyImmediate(generatedParticleMaterial);
        }

    }
}
