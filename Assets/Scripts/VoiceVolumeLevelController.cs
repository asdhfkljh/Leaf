using UnityEngine;

/// <summary>
/// Five-step volume control for the player's recorded-voice playback.
/// Attach this component to the object that owns the playback AudioSource (normally
/// the same object as RealtimeOpenSmileInput). Disable or remove it to hide the UI
/// and return volume control to the normal AudioSource settings.
/// </summary>
[DisallowMultipleComponent]
public class VoiceVolumeLevelController : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private bool enableKeyboardInput = true;
    [SerializeField] private KeyCode louderKey = KeyCode.Q;
    [SerializeField] private KeyCode quieterKey = KeyCode.A;

    [Header("Five volume levels")]
    [Tooltip("The selected level when the scene starts. Q raises it and A lowers it.")]
    [SerializeField, Range(1, 5)] private int initialLevel = 3;
    [Tooltip("Playback volume for each level, from quietest to loudest.")]
    [SerializeField] private float[] levelVolumes = { 0.35f, 0.5f, 0.65f, 0.8f, 1f };

    [Header("Developer display")]
    [Tooltip("Disable this to keep the controls active but hide the on-screen level display.")]
    [SerializeField] private bool showDeveloperOverlay = true;
    [SerializeField] private Vector2 overlayPosition = new Vector2(16f, 16f);
    [SerializeField] private Color overlayColor = Color.white;

    [Header("Audio target")]
    [Tooltip("If empty, the AudioSource on this GameObject is used.")]
    [SerializeField] private AudioSource targetSource;

    private const int LevelCount = 5;
    private int currentLevel;
    private GUIStyle overlayStyle;

    /// <summary>Current volume step, in the inclusive range 1 through 5.</summary>
    public int CurrentLevel => currentLevel;
    public float CurrentVolume => VolumeForLevel(currentLevel);

    private void Awake()
    {
        if (targetSource == null)
            targetSource = GetComponent<AudioSource>();

        currentLevel = Mathf.Clamp(initialLevel, 1, LevelCount);
        ApplyVolume();
    }

    private void Update()
    {
        if (!enableKeyboardInput)
            return;

        if (Input.GetKeyDown(louderKey))
            SetLevel(currentLevel + 1);
        else if (Input.GetKeyDown(quieterKey))
            SetLevel(currentLevel - 1);
    }

    // RealtimeOpenSmileInput resets AudioSource.volume immediately before playing a
    // completed recording. Applying here keeps the selected level for that replay.
    private void LateUpdate()
    {
        ApplyVolume();
    }

    /// <summary>Sets the volume step. Useful for UI Buttons or other input systems.</summary>
    public void SetLevel(int level)
    {
        int clampedLevel = Mathf.Clamp(level, 1, LevelCount);
        if (clampedLevel == currentLevel)
            return;

        currentLevel = clampedLevel;
        ApplyVolume();
    }

    public void RaiseVolume() => SetLevel(currentLevel + 1);
    public void LowerVolume() => SetLevel(currentLevel - 1);

    private float VolumeForLevel(int level)
    {
        int index = Mathf.Clamp(level, 1, LevelCount) - 1;
        if (levelVolumes == null || levelVolumes.Length != LevelCount)
            return Mathf.Lerp(0.35f, 1f, index / (float)(LevelCount - 1));

        return Mathf.Clamp01(levelVolumes[index]);
    }

    private void ApplyVolume()
    {
        // RealtimeOpenSmileInput may create its AudioSource in its own Awake.
        // Retry here so Unity script execution order cannot leave this unbound.
        if (targetSource == null)
            targetSource = GetComponent<AudioSource>();

        if (targetSource != null)
            targetSource.volume = CurrentVolume;
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
        GUI.Label(new Rect(overlayPosition.x, overlayPosition.y, 330f, 30f),
            "Voice Volume " + currentLevel + "/5  " + bars + "  (Q/A)", overlayStyle);
    }

    private void OnValidate()
    {
        initialLevel = Mathf.Clamp(initialLevel, 1, LevelCount);
        if (levelVolumes == null || levelVolumes.Length != LevelCount)
            levelVolumes = new[] { 0.35f, 0.5f, 0.65f, 0.8f, 1f };
    }
}
