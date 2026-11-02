using UnityEngine;

/// <summary>
/// Five-step pitch control for the player's recorded-voice playback.
/// Attach this to the object that owns the playback AudioSource (normally the
/// same object as RealtimeOpenSmileInput). Disable or remove it to remove both
/// the E/D controls and its developer overlay.
/// </summary>
[DisallowMultipleComponent]
public class VoiceSpeedLevelController : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private bool enableKeyboardInput = true;
    [SerializeField] private KeyCode fasterKey = KeyCode.E;
    [SerializeField] private KeyCode slowerKey = KeyCode.D;

    [Header("Five pitch levels")]
    [Tooltip("The selected level when the scene starts. E raises it and D lowers it.")]
    [SerializeField, Range(1, 5)] private int initialLevel = 4;
    [Tooltip("AudioSource pitch for each level, from lowest to highest. Level 3 is natural pitch.")]
    [SerializeField] private float[] levelPitches = { 0.8f, 0.9f, 1f, 1.1f, 1.2f };

    [Header("Developer display")]
    [Tooltip("Disable this to keep the controls active but hide the on-screen speed display.")]
    [SerializeField] private bool showDeveloperOverlay = true;
    [SerializeField] private Vector2 overlayPosition = new Vector2(16f, 44f);
    [SerializeField] private Color overlayColor = Color.white;

    [Header("Audio target")]
    [Tooltip("If empty, the AudioSource on this GameObject is used.")]
    [SerializeField] private AudioSource targetSource;

    private const int LevelCount = 5;
    private int currentLevel;
    private GUIStyle overlayStyle;

    /// <summary>Current pitch step, in the inclusive range 1 through 5.</summary>
    public int CurrentLevel => currentLevel;
    public float CurrentPitch => PitchForLevel(currentLevel);

    private void Awake()
    {
        if (targetSource == null)
            targetSource = GetComponent<AudioSource>();

        currentLevel = Mathf.Clamp(initialLevel, 1, LevelCount);
        ApplyPitch();
    }

    private void Update()
    {
        if (!enableKeyboardInput)
            return;

        if (Input.GetKeyDown(fasterKey))
            SetLevel(currentLevel + 1);
        else if (Input.GetKeyDown(slowerKey))
            SetLevel(currentLevel - 1);
    }

    // RealtimeOpenSmileInput initializes its AudioSource during Awake and starts
    // playback after High or Low text effects. Applying every frame keeps the
    // selected speed for either result, regardless of script execution order.
    private void LateUpdate()
    {
        ApplyPitch();
    }

    /// <summary>Sets the pitch step. Useful for UI Buttons or other input systems.</summary>
    public void SetLevel(int level)
    {
        int clampedLevel = Mathf.Clamp(level, 1, LevelCount);
        if (clampedLevel == currentLevel)
            return;

        currentLevel = clampedLevel;
        ApplyPitch();
    }

    public void RaisePitch() => SetLevel(currentLevel + 1);
    public void LowerPitch() => SetLevel(currentLevel - 1);

    private float PitchForLevel(int level)
    {
        int index = Mathf.Clamp(level, 1, LevelCount) - 1;
        if (levelPitches == null || levelPitches.Length != LevelCount)
            return Mathf.Lerp(0.8f, 1.2f, index / (float)(LevelCount - 1));

        return Mathf.Clamp(levelPitches[index], -3f, 3f);
    }

    private void ApplyPitch()
    {
        // RealtimeOpenSmileInput may create its AudioSource in its own Awake.
        // Retry here so Unity script execution order cannot leave this unbound.
        if (targetSource == null)
            targetSource = GetComponent<AudioSource>();

        if (targetSource != null)
            targetSource.pitch = CurrentPitch;
    }

    private void OnGUI()
    {
        if (!showDeveloperOverlay)
            return;

        if (overlayStyle == null)
        {
            overlayStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = overlayColor }
            };
        }

        string bars = new string('■', currentLevel) + new string('□', LevelCount - currentLevel);
        GUI.Label(new Rect(overlayPosition.x, overlayPosition.y, 370f, 30f),
            "Voice Pitch " + currentLevel + "/5  " + bars +
            "  " + CurrentPitch.ToString("0.0") + "x  (E/D)", overlayStyle);
    }

    private void OnValidate()
    {
        initialLevel = Mathf.Clamp(initialLevel, 1, LevelCount);
        if (levelPitches == null || levelPitches.Length != LevelCount)
            levelPitches = new[] { 0.8f, 0.9f, 1f, 1.1f, 1.2f };
    }
}
