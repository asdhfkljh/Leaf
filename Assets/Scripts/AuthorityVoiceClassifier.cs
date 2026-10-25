using System;
using UnityEngine;

/// <summary>Classifies a completed recording against editable voice-feature ranges.</summary>
public class AuthorityVoiceClassifier : MonoBehaviour
{
    [Serializable]
    private struct FloatRange
    {
        public float minimum;
        public float maximum;

        public bool Contains(float value) => value >= minimum && value <= maximum;
    }

    [Serializable]
    private class FeatureRules
    {
        [Header("F0 median (Hz)")]
        public FloatRange f0Median;
        [Header("F0 span (semitones)")]
        public FloatRange f0SpanSemitones;
        [Header("F0 relative standard deviation")]
        public FloatRange f0StddevNorm;
        [Header("Loudness median (openSMILE units)")]
        public FloatRange loudnessMedian;
        [Header("Loudness span (P80-P20, openSMILE units)")]
        public FloatRange loudnessSpan;
    }

    [Header("Classifier")]
    [Tooltip("All five measured features must be inside their ranges for High mode. Missing values count as Low.")]
    [SerializeField] private FeatureRules maleRules = new FeatureRules
    {
        f0Median = new FloatRange { minimum = 119f, maximum = 149f },
        f0SpanSemitones = new FloatRange { minimum = 5.47f, maximum = 9.19f },
        f0StddevNorm = new FloatRange { minimum = 0.234f, maximum = 0.322f },
        loudnessMedian = new FloatRange { minimum = 0.97f, maximum = 1.54f },
        loudnessSpan = new FloatRange { minimum = 1.93f, maximum = 2.66f }
    };
    [SerializeField] private FeatureRules femaleRules = new FeatureRules
    {
        f0Median = new FloatRange { minimum = 193f, maximum = 214f },
        f0SpanSemitones = new FloatRange { minimum = 5.15f, maximum = 7.15f },
        f0StddevNorm = new FloatRange { minimum = 0.130f, maximum = 0.184f },
        loudnessMedian = new FloatRange { minimum = 0.54f, maximum = 0.84f },
        loudnessSpan = new FloatRange { minimum = 0.94f, maximum = 1.27f }
    };

    [Header("Scene References")]
    [SerializeField] private RealtimeOpenSmileInput openSmileInput;
    [SerializeField] private AuthorityVisualController visualController;
    [SerializeField] private bool logClassification = true;

    [Serializable]
    private class FeaturePacket
    {
        public string type;
        public string speaker_gender;
        public float f0_median_hz;
        public float f0_span_semitones;
        public float f0_stddev_norm;
        public float loudness_median;
        public float loudness_span;
    }

    private void Awake()
    {
        if (openSmileInput == null)
            openSmileInput = GetComponent<RealtimeOpenSmileInput>();
        if (visualController == null)
            visualController = GetComponent<AuthorityVisualController>();

        if (openSmileInput != null)
            openSmileInput.OnFeaturesJsonReceived.AddListener(ClassifyJson);
        else
            Debug.LogError("AuthorityVoiceClassifier: assign or add RealtimeOpenSmileInput.", this);

        if (visualController == null)
            Debug.LogError("AuthorityVoiceClassifier: assign or add AuthorityVisualController.", this);
    }

    private void OnDestroy()
    {
        if (openSmileInput != null)
            openSmileInput.OnFeaturesJsonReceived.RemoveListener(ClassifyJson);
    }

    public void ClassifyJson(string json)
    {
        if (visualController == null || string.IsNullOrEmpty(json))
            return;

        FeaturePacket packet;
        try
        {
            packet = JsonUtility.FromJson<FeaturePacket>(json);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("AuthorityVoiceClassifier: could not parse features: " + exception.Message, this);
            return;
        }

        if (packet == null || packet.type != "features")
            return;

        bool classifiedAsFemaleVoice;
        if (packet.speaker_gender == "female")
            classifiedAsFemaleVoice = true;
        else if (packet.speaker_gender == "male")
            classifiedAsFemaleVoice = false;
        else
        {
            return;
        }

        FeatureRules rules = classifiedAsFemaleVoice ? femaleRules : maleRules;
        bool isHigh = packet.f0_median_hz != 0f && packet.f0_span_semitones != 0f &&
                      packet.f0_stddev_norm != 0f && packet.loudness_median != 0f &&
                      packet.loudness_span != 0f &&
                      rules.f0Median.Contains(packet.f0_median_hz) &&
                      rules.f0SpanSemitones.Contains(packet.f0_span_semitones) &&
                      rules.f0StddevNorm.Contains(packet.f0_stddev_norm) &&
                      rules.loudnessMedian.Contains(packet.loudness_median) &&
                      rules.loudnessSpan.Contains(packet.loudness_span);

        if (isHigh)
            visualController.PlaySequence();
        else
            visualController.PlayLowSequence();

        if (logClassification)
            Debug.Log("Voice classification: " + (isHigh ? "HIGH" : "LOW") +
                      " (model-predicted voice group=" + (classifiedAsFemaleVoice ? "Female" : "Male") + ")", this);
    }
}
