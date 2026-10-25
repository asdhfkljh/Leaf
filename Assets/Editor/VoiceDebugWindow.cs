#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

/// <summary>Editor-only monitor. Never creates objects or UI in the player.</summary>
public sealed class VoiceDebugWindow : EditorWindow
{
    [SerializeField] private RealtimeOpenSmileInput input;
    private Vector2 scroll;
    private string lastJson;
    private string lastIssue;
    private FeatureSnapshot features;
    private bool showJson;

    [Serializable]
    private sealed class FeatureSnapshot
    {
        public string speaker_gender;
        public float analysis_duration_seconds;
        public int sample_rate;
        public float f0_median_hz;
        public float f0_span_semitones;
        public float f0_stddev_norm;
        public float loudness_median;
        public float loudness_span;
        public float rms_dbfs;
    }

    [MenuItem("Window/Analysis/Voice Debug")]
    public static void Open()
    {
        VoiceDebugWindow window = GetWindow<VoiceDebugWindow>("语音调试");
        window.minSize = new Vector2(340f, 420f);
        window.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += Refresh;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        Application.logMessageReceived += CaptureIssue;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Refresh;
        EditorApplication.playModeStateChanged -= PlayModeChanged;
        Application.logMessageReceived -= CaptureIssue;
    }

    private void PlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            ClearResult();
            lastIssue = null;
        }
        Repaint();
    }

    private void ClearResult()
    {
        lastJson = null;
        features = null;
    }

    private void CaptureIssue(string message, string stackTrace, LogType type)
    {
        if (!EditorApplication.isPlaying || input == null ||
            (type != LogType.Error && type != LogType.Exception && type != LogType.Warning))
            return;
        if (message.StartsWith("RealtimeOpenSmileInput:", StringComparison.Ordinal) ||
            message.StartsWith("openSMILE worker:", StringComparison.Ordinal) ||
            message.StartsWith("AuthorityVoiceClassifier:", StringComparison.Ordinal))
            lastIssue = message;
    }

    private void Refresh()
    {
        if (!EditorApplication.isPlaying)
            return;
        if (input == null)
            input = FindObjectOfType<RealtimeOpenSmileInput>();
        if (input != null && input.LatestFeaturesJson != lastJson)
        {
            lastJson = input.LatestFeaturesJson;
            features = null;
            if (!string.IsNullOrEmpty(lastJson))
            {
                try { features = JsonUtility.FromJson<FeatureSnapshot>(lastJson); }
                catch (Exception exception) { lastIssue = "结果解析失败：" + exception.Message; }
            }
        }
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("仅开发者可见 · 不会包含在 Build 中。进入 Play 后自动连接录音组件。", MessageType.Info);
        EditorGUI.BeginChangeCheck();
        input = (RealtimeOpenSmileInput)EditorGUILayout.ObjectField("录音组件", input, typeof(RealtimeOpenSmileInput), true);
        if (EditorGUI.EndChangeCheck())
        {
            ClearResult();
            lastIssue = null;
        }

        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("等待进入 Play 模式。", MessageType.None);
            return;
        }
        if (input == null)
        {
            EditorGUILayout.HelpBox("场景中未找到 RealtimeOpenSmileInput，请指定录音组件。", MessageType.Warning);
            return;
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        bool running = input.DebugWorkerRunning;
        string status = !input.isActiveAndEnabled ? "组件未启用" :
            !running ? "分析进程未运行 / 已退出" :
            input.IsRecording ? "● 正在录音" :
            input.IsProcessing ? "正在分析，请稍候…" :
            input.DebugStartRequested ? "等待模型就绪后开始录音…" :
            input.DebugWorkerReady ? "就绪，等待录音" : "正在加载模型…";
        EditorGUILayout.LabelField("当前状态", status, EditorStyles.boldLabel);
        EditorGUILayout.LabelField("麦克风", string.IsNullOrEmpty(input.DebugMicrophone) ? "尚未启动" : input.DebugMicrophone);
        if (input.IsRecording)
            EditorGUILayout.LabelField("录音时长", input.DebugRecordingSeconds.ToString("F1") + " 秒");

        using (new EditorGUI.DisabledScope(!input.isActiveAndEnabled || input.IsProcessing || !running))
        {
            if (GUILayout.Button(input.IsRecording ? "停止录音并分析" :
                input.DebugStartRequested ? "取消等待录音" : "开始录音"))
            {
                lastIssue = null;
                input.ToggleCapture();
            }
        }
        EditorGUILayout.HelpBox("也可在 Game 窗口按原先配置的录音快捷键（默认数字 1）。", MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("最近一次分析结果（录音期间保留上次结果）", EditorStyles.boldLabel);
        if (features == null)
            EditorGUILayout.LabelField("尚无结果，停止录音后等待分析完成。");
        else
        {
            AuthorityVoiceClassifier classifier = input.GetComponent<AuthorityVoiceClassifier>();
            EditorGUILayout.LabelField("判定", classifier == null || string.IsNullOrEmpty(classifier.DebugLastClassification)
                ? "未产生判定（请检查分类器引用）" : classifier.DebugLastClassification);
            EditorGUILayout.LabelField("模型声音分组", features.speaker_gender ?? "未知");
            Value("分析音频时长", features.analysis_duration_seconds, " 秒");
            EditorGUILayout.LabelField("采样率", features.sample_rate + " Hz");
            Value("F0 中位数", features.f0_median_hz, " Hz");
            Value("F0 跨度", features.f0_span_semitones, " 半音");
            Value("F0 相对标准差", features.f0_stddev_norm);
            Value("响度中位数", features.loudness_median);
            Value("响度跨度", features.loudness_span);
            Value("RMS", features.rms_dbfs, " dBFS");
            EditorGUILayout.HelpBox("缺失 / null 的指标在此显示为 0；原始 JSON 可核对实际值。", MessageType.None);
            showJson = EditorGUILayout.Foldout(showJson, "原始 JSON", true);
            if (showJson)
                EditorGUILayout.TextArea(lastJson, GUILayout.MinHeight(100f));
        }

        if (!string.IsNullOrEmpty(lastIssue))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("最近警告 / 错误：\n" + lastIssue, MessageType.Warning);
            if (GUILayout.Button("清除提示")) lastIssue = null;
        }
        EditorGUILayout.EndScrollView();
    }

    private static void Value(string label, float value, string unit = "")
    {
        EditorGUILayout.LabelField(label, value.ToString("F3") + unit);
    }
}
#endif
