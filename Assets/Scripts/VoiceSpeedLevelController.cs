using UnityEngine;

/// <summary>Five-step playback-speed control that preserves the selected voice pitch.</summary>
[DisallowMultipleComponent]
public class VoiceSpeedLevelController : MonoBehaviour
{
    [SerializeField] private bool enableKeyboardInput = true;
    [SerializeField] private KeyCode fasterKey = KeyCode.W;
    [SerializeField] private KeyCode slowerKey = KeyCode.S;
    [SerializeField, Range(1, 5)] private int initialLevel = 3;
    [SerializeField] private float[] levelSpeeds = { 0.8f, 0.9f, 1f, 1.1f, 1.2f };
    [SerializeField] private bool showDeveloperOverlay = true;
    [SerializeField] private Vector2 overlayPosition = new Vector2(16f, 44f);
    [SerializeField] private Color overlayColor = Color.white;

    private const int LevelCount = 5;
    private int currentLevel;
    private IndependentVoicePlayback playback;
    private GUIStyle overlayStyle;
    public int CurrentLevel => currentLevel;
    public float CurrentSpeed => ValueForLevel(currentLevel);

    private void Awake() { currentLevel = Mathf.Clamp(initialLevel, 1, LevelCount); playback = GetComponent<IndependentVoicePlayback>(); ApplySpeed(); }
    private void Update()
    {
        if (!enableKeyboardInput) return;
        if (Input.GetKeyDown(fasterKey)) SetLevel(currentLevel + 1);
        else if (Input.GetKeyDown(slowerKey)) SetLevel(currentLevel - 1);
    }
    public void SetLevel(int level) { currentLevel = Mathf.Clamp(level, 1, LevelCount); ApplySpeed(); }
    public void IncreaseSpeed() => SetLevel(currentLevel + 1);
    public void DecreaseSpeed() => SetLevel(currentLevel - 1);
    private float ValueForLevel(int level)
    {
        int index = Mathf.Clamp(level, 1, LevelCount) - 1;
        return levelSpeeds != null && levelSpeeds.Length == LevelCount ? Mathf.Clamp(levelSpeeds[index], 0.5f, 2f) : Mathf.Lerp(0.8f, 1.2f, index / (float)(LevelCount - 1));
    }
    private void ApplySpeed() { if (playback == null) playback = GetComponent<IndependentVoicePlayback>(); if (playback != null) playback.SetSpeed(CurrentSpeed); }
    private void OnGUI()
    {
        if (!showDeveloperOverlay) return;
        if (overlayStyle == null) overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = overlayColor } };
        string bars = new string('■', currentLevel) + new string('□', LevelCount - currentLevel);
        GUI.Label(new Rect(overlayPosition.x, overlayPosition.y, 370f, 30f), "Voice Speed " + currentLevel + "/5  " + bars + "  " + CurrentSpeed.ToString("0.0") + "x  (W/S)", overlayStyle);
    }
    private void OnValidate() { initialLevel = Mathf.Clamp(initialLevel, 1, LevelCount); if (levelSpeeds == null || levelSpeeds.Length != LevelCount) levelSpeeds = new[] { 0.8f, 0.9f, 1f, 1.1f, 1.2f }; }
}
