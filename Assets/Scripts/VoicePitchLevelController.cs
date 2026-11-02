using UnityEngine;

/// <summary>Five-step pitch control that preserves the selected playback speed.</summary>
[DisallowMultipleComponent]
public class VoicePitchLevelController : MonoBehaviour
{
    [SerializeField] private bool enableKeyboardInput = true;
    [SerializeField] private KeyCode higherKey = KeyCode.E;
    [SerializeField] private KeyCode lowerKey = KeyCode.D;
    [SerializeField, Range(1, 5)] private int initialLevel = 4;
    [SerializeField] private float[] levelPitches = { 0.8f, 0.9f, 1f, 1.1f, 1.2f };
    [SerializeField] private bool showDeveloperOverlay = true;
    [SerializeField] private Vector2 overlayPosition = new Vector2(16f, 72f);
    [SerializeField] private Color overlayColor = Color.white;
    private const int LevelCount = 5;
    private int currentLevel;
    private IndependentVoicePlayback playback;
    private GUIStyle overlayStyle;
    public int CurrentLevel => currentLevel;
    public float CurrentPitch => ValueForLevel(currentLevel);
    private void Awake() { currentLevel = Mathf.Clamp(initialLevel, 1, LevelCount); playback = GetComponent<IndependentVoicePlayback>(); ApplyPitch(); }
    private void Update()
    {
        if (!enableKeyboardInput) return;
        if (Input.GetKeyDown(higherKey)) SetLevel(currentLevel + 1);
        else if (Input.GetKeyDown(lowerKey)) SetLevel(currentLevel - 1);
    }
    public void SetLevel(int level) { currentLevel = Mathf.Clamp(level, 1, LevelCount); ApplyPitch(); }
    public void RaisePitch() => SetLevel(currentLevel + 1);
    public void LowerPitch() => SetLevel(currentLevel - 1);
    private float ValueForLevel(int level)
    {
        int index = Mathf.Clamp(level, 1, LevelCount) - 1;
        return levelPitches != null && levelPitches.Length == LevelCount ? Mathf.Clamp(levelPitches[index], 0.5f, 2f) : Mathf.Lerp(0.8f, 1.2f, index / (float)(LevelCount - 1));
    }
    private void ApplyPitch() { if (playback == null) playback = GetComponent<IndependentVoicePlayback>(); if (playback != null) playback.SetPitch(CurrentPitch); }
    private void OnGUI()
    {
        if (!showDeveloperOverlay) return;
        if (overlayStyle == null) overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = overlayColor } };
        string bars = new string('■', currentLevel) + new string('□', LevelCount - currentLevel);
        GUI.Label(new Rect(overlayPosition.x, overlayPosition.y, 370f, 30f), "Voice Pitch " + currentLevel + "/5  " + bars + "  " + CurrentPitch.ToString("0.0") + "x  (E/D)", overlayStyle);
    }
    private void OnValidate() { initialLevel = Mathf.Clamp(initialLevel, 1, LevelCount); if (levelPitches == null || levelPitches.Length != LevelCount) levelPitches = new[] { 0.8f, 0.9f, 1f, 1.1f, 1.2f }; }
}
