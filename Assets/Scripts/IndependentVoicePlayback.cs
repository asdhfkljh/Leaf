using UnityEngine;

/// <summary>Granular overlap-add processor for independently changing completed-recording speed and pitch.</summary>
[DisallowMultipleComponent]
public class IndependentVoicePlayback : MonoBehaviour
{
    [SerializeField, Range(256, 2048)] private int grainFrames = 1024;
    [SerializeField, Range(0.1f, 0.75f)] private float overlap = 0.75f;
    private AudioSource source;
    private AudioClip originalClip;
    private AudioClip processedClip;
    private float speed = 1f;
    private float pitch = 1f;
    private float volume = 1f;

    private void Awake()
    {
        source = GetComponent<AudioSource>();
        if (source == null) source = gameObject.AddComponent<AudioSource>();
        source.pitch = 1f;
    }
    public void SetSpeed(float value) { speed = Mathf.Clamp(value, 0.5f, 2f); RebuildIfPlaying(); }
    public void SetPitch(float value) { pitch = Mathf.Clamp(value, 0.5f, 2f); RebuildIfPlaying(); }
    public void Play(AudioClip clip, float playbackVolume) { originalClip = clip; volume = playbackVolume; RebuildAndPlay(); }
    private void RebuildIfPlaying() { if (originalClip != null && source != null && source.isPlaying) RebuildAndPlay(); }
    private void RebuildAndPlay()
    {
        if (source == null || originalClip == null) return;
        AudioClip next = CreateProcessedClip(originalClip);
        if (next == null) return;
        source.Stop();
        if (processedClip != null && processedClip != originalClip) Destroy(processedClip);
        processedClip = next;
        source.pitch = 1f;
        source.volume = volume;
        source.clip = processedClip;
        source.Play();
    }
    private AudioClip CreateProcessedClip(AudioClip input)
    {
        int channels = input.channels;
        int inputFrames = input.samples;
        if (channels < 1 || inputFrames < 2) return input;
        float[] inputData = new float[inputFrames * channels];
        if (!input.GetData(inputData, 0)) return input;
        int frames = Mathf.Clamp(grainFrames, 256, 2048);
        int hop = Mathf.Max(1, Mathf.RoundToInt(frames * (1f - overlap)));
        int outputFrames = Mathf.Max(1, Mathf.CeilToInt(inputFrames / speed));
        float[] output = new float[outputFrames * channels];
        float[] weights = new float[outputFrames];
        for (int outputStart = 0; outputStart < outputFrames; outputStart += hop)
        {
            float inputCenter = outputStart * speed;
            for (int frame = 0; frame < frames; frame++)
            {
                int outputFrame = outputStart + frame - frames / 2;
                float inputFrame = inputCenter + (frame - frames / 2) * pitch;
                if (outputFrame < 0 || outputFrame >= outputFrames || inputFrame < 0f || inputFrame >= inputFrames - 1) continue;
                float window = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * frame / (frames - 1));
                int left = Mathf.FloorToInt(inputFrame);
                float fraction = inputFrame - left;
                for (int channel = 0; channel < channels; channel++)
                    output[outputFrame * channels + channel] += Mathf.Lerp(inputData[left * channels + channel], inputData[(left + 1) * channels + channel], fraction) * window;
                weights[outputFrame] += window;
            }
        }
        for (int frame = 0; frame < outputFrames; frame++)
        {
            if (weights[frame] <= 0.0001f) continue;
            for (int channel = 0; channel < channels; channel++) output[frame * channels + channel] /= weights[frame];
        }
        AudioClip result = AudioClip.Create("Processed Voice Recording", outputFrames, channels, input.frequency, false);
        result.SetData(output, 0);
        return result;
    }
    private void OnDestroy() { if (processedClip != null && processedClip != originalClip) Destroy(processedClip); }
}
