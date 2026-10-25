using System;
using System.Collections.Concurrent;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using DataReceivedEventArgs = System.Diagnostics.DataReceivedEventArgs;
using System.IO;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class OpenSmileStringEvent : UnityEvent<string> { }

/// <summary>Streams one complete microphone recording to a persistent openSMILE worker for analysis on stop.</summary>
public class RealtimeOpenSmileInput : MonoBehaviour
{
    [Header("Python openSMILE worker")]
    [SerializeField] private string pythonExecutable = "python3";

    [Header("Local speech recognition")]
    [Tooltip("Multilingual faster-whisper model: tiny, base, small, or a local model directory.")]
    [SerializeField] private string whisperModel = "base";
    [Tooltip("auto detects the spoken language; use zh or en to force a language.")]
    [SerializeField] private string speechLanguage = "auto";
    [SerializeField, Min(0.5f)] private float transcriptionIntervalSeconds = 2f;

    [Header("Microphone")]
    [SerializeField] private string microphoneDevice = "";
    [SerializeField] private int targetSampleRate = 16000;
    [SerializeField] private int recordingLengthSeconds = 10;
    [SerializeField] private int chunkMilliseconds = 100;
    [SerializeField, Min(5)] private int maximumRecordingSeconds = 180;
    [SerializeField] private bool autoStart = false;
    [SerializeField] private bool enableKeyboardToggle = true;
    [SerializeField] private KeyCode toggleRecordingKey = KeyCode.Alpha1;

    [Header("Output")]
    [SerializeField] private bool logFeatureJson = true;
    [SerializeField] private OpenSmileStringEvent onFeaturesJsonReceived = new OpenSmileStringEvent();

    private const string WorkerRelativePath = "Tools/openSMILE/realtime_worker.py";
    private readonly ConcurrentQueue<string> stdoutQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> stderrQueue = new ConcurrentQueue<string>();

    private Process worker;
    private AudioClip microphoneClip;
    private string activeDevice;
    private int readPosition;
    private int chunkFrames;
    private bool workerReady;
    private bool capturing;
    private bool startRequested;
    private bool awaitingAnalysis;
    private float recordingStartedAt;
    private AuthorityVisualController visualController;

    public string LatestFeaturesJson { get; private set; }
    public string LatestTranscript { get; private set; }
    public OpenSmileStringEvent OnFeaturesJsonReceived => onFeaturesJsonReceived;
    public bool IsRecording => capturing;
    public bool IsProcessing => awaitingAnalysis;

#if UNITY_EDITOR
    // Read-only diagnostics for the developer window; excluded from player builds.
    public bool DebugWorkerRunning => worker != null && !worker.HasExited;
    public bool DebugWorkerReady => DebugWorkerRunning && workerReady;
    public bool DebugStartRequested => startRequested;
    public string DebugMicrophone => activeDevice;
    public float DebugRecordingSeconds => capturing ? Time.realtimeSinceStartup - recordingStartedAt : 0f;
#endif

    [Serializable]
    private class AudioChunkPacket
    {
        public string type = "audio";
        public int sample_rate;
        public string pcm_f32le_b64;
    }

    [Serializable]
    private class WorkerControlPacket
    {
        public string type;
    }

    [Serializable]
    private class WorkerMessage
    {
        public string type;
        public string message;
        public string text;
        public string transcript;
    }

    private void Awake()
    {
        visualController = GetComponent<AuthorityVisualController>();
    }

    private void Start()
    {
        if (!StartWorker())
            return;

        if (autoStart)
            startRequested = true;
    }

    public void StartCapture()
    {
        if (capturing || startRequested || awaitingAnalysis)
            return;

        if (visualController != null && visualController.IsSequencePlaying)
        {
            Debug.Log("RealtimeOpenSmileInput: wait for the current text effect to finish before recording again.", this);
            return;
        }

        if (worker == null || worker.HasExited)
        {
            workerReady = false;
            if (!StartWorker())
                return;
        }

        startRequested = true;
        if (workerReady)
            StartMicrophoneCapture();
        else
            Debug.Log("RealtimeOpenSmileInput: preparing the voice model; recording will begin when it is ready.", this);
    }

    private void StartMicrophoneCapture()
    {
        if (!startRequested || !workerReady || worker == null)
            return;

        string[] devices = Microphone.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogError("RealtimeOpenSmileInput: no microphone device is available.", this);
            startRequested = false;
            return;
        }

        activeDevice = string.IsNullOrWhiteSpace(microphoneDevice) ? devices[0] : microphoneDevice;
        if (Array.IndexOf(devices, activeDevice) < 0)
        {
            Debug.LogError("RealtimeOpenSmileInput: microphone device not found: " + activeDevice, this);
            startRequested = false;
            return;
        }

        int minFrequency;
        int maxFrequency;
        Microphone.GetDeviceCaps(activeDevice, out minFrequency, out maxFrequency);
        int requestedFrequency = targetSampleRate;
        if (minFrequency > 0 && requestedFrequency < minFrequency)
            requestedFrequency = minFrequency;
        if (maxFrequency > 0 && requestedFrequency > maxFrequency)
            requestedFrequency = maxFrequency;

        microphoneClip = Microphone.Start(
            activeDevice,
            true,
            Mathf.Max(3, recordingLengthSeconds),
            requestedFrequency);

        if (microphoneClip == null)
        {
            Debug.LogError("RealtimeOpenSmileInput: Unity could not start microphone capture.", this);
            startRequested = false;
            return;
        }

        if (!SendWorkerControl("start"))
        {
            Microphone.End(activeDevice);
            microphoneClip = null;
            startRequested = false;
            return;
        }

        startRequested = false;
        chunkFrames = Mathf.Max(1, microphoneClip.frequency * Mathf.Max(20, chunkMilliseconds) / 1000);
        readPosition = Mathf.Max(0, Microphone.GetPosition(activeDevice));
        recordingStartedAt = Time.realtimeSinceStartup;
        capturing = true;
        LatestTranscript = "";
        if (visualController != null)
            visualController.SetSpeechText("");
        Debug.Log("RealtimeOpenSmileInput: recording started on " + activeDevice + ". Press Alpha1 again to analyze and send.", this);
    }

    /// <summary>Stops microphone capture and asks the worker to analyze the complete recording.</summary>
    public void StopCapture()
    {
        if (startRequested && !capturing)
        {
            startRequested = false;
            Debug.Log("RealtimeOpenSmileInput: canceled the pending recording start.", this);
            return;
        }

        if (!capturing)
            return;

        PumpMicrophoneAudio(true);
        capturing = false;
        EndMicrophone();

        if (SendWorkerControl("finalize"))
        {
            awaitingAnalysis = true;
            Debug.Log("RealtimeOpenSmileInput: recording ended; analyzing the complete utterance.", this);
        }
    }

    private bool StartWorker()
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string workerPath = Path.GetFullPath(Path.Combine(projectRoot, WorkerRelativePath));
        if (!File.Exists(workerPath))
        {
            Debug.LogError("RealtimeOpenSmileInput: worker script not found: " + workerPath, this);
            return false;
        }

        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = pythonExecutable;
            info.Arguments =
                "\"" + workerPath + "\"" +
                " --max-recording-seconds " + Mathf.Max(5, maximumRecordingSeconds) +
                " --whisper-model \"" + whisperModel.Replace("\"", "") + "\"" +
                " --speech-language \"" + speechLanguage.Replace("\"", "") + "\"" +
                " --transcription-interval " + Mathf.Max(0.5f, transcriptionIntervalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.WorkingDirectory = projectRoot;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            worker = new Process();
            worker.StartInfo = info;
            worker.EnableRaisingEvents = true;
            worker.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null)
                    stdoutQueue.Enqueue(args.Data);
            };
            worker.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (args.Data != null)
                    stderrQueue.Enqueue(args.Data);
            };

            if (!worker.Start())
            {
                Debug.LogError("RealtimeOpenSmileInput: failed to start Python worker.", this);
                worker.Dispose();
                worker = null;
                return false;
            }

            worker.StandardInput.AutoFlush = true;
            worker.BeginOutputReadLine();
            worker.BeginErrorReadLine();
            workerReady = false;
            Debug.Log("RealtimeOpenSmileInput: loading openSMILE and voice model in the background.", this);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError("RealtimeOpenSmileInput: could not launch Python worker. " + exception.Message, this);
            StopWorker();
            return false;
        }
    }

    private void Update()
    {
        DrainWorkerOutput();

        if (capturing)
        {
            PumpMicrophoneAudio();
            if (Time.realtimeSinceStartup - recordingStartedAt >= maximumRecordingSeconds)
            {
                Debug.LogWarning("RealtimeOpenSmileInput: maximum recording duration reached; analyzing now.", this);
                StopCapture();
            }
        }

        if (enableKeyboardToggle && Input.GetKeyDown(toggleRecordingKey))
            ToggleCapture();
    }

    /// <summary>Toggle recording; can also be connected to a Unity UI Button OnClick event.</summary>
    public void ToggleCapture()
    {
        if (capturing)
            StopCapture();
        else if (startRequested)
        {
            startRequested = false;
            Debug.Log("RealtimeOpenSmileInput: canceled the pending recording start.", this);
        }
        else if (awaitingAnalysis)
            Debug.Log("RealtimeOpenSmileInput: analysis is still running; wait for the current result.", this);
        else
            StartCapture();
    }

    private void PumpMicrophoneAudio(bool flushPartialChunk = false)
    {
        if (!capturing || microphoneClip == null || worker == null || worker.HasExited)
            return;

        int currentPosition = Microphone.GetPosition(activeDevice);
        if (currentPosition < 0)
            return;

        int availableFrames = currentPosition >= readPosition
            ? currentPosition - readPosition
            : microphoneClip.samples - readPosition + currentPosition;

        if (availableFrames > microphoneClip.samples / 2)
        {
            readPosition = currentPosition;
            return;
        }

        while (availableFrames >= chunkFrames || (flushPartialChunk && availableFrames > 0))
        {
            int framesToRead = Mathf.Min(chunkFrames, availableFrames);
            float[] mono = ReadMonoFrames(readPosition, framesToRead);
            if (mono == null)
                break;

            SendAudioChunk(mono, microphoneClip.frequency);
            readPosition = (readPosition + framesToRead) % microphoneClip.samples;
            availableFrames -= framesToRead;
        }
    }

    private float[] ReadMonoFrames(int startFrame, int frameCount)
    {
        int channels = Mathf.Max(1, microphoneClip.channels);
        int firstFrames = Mathf.Min(frameCount, microphoneClip.samples - startFrame);
        float[] interleaved = new float[frameCount * channels];

        float[] first = new float[firstFrames * channels];
        if (!microphoneClip.GetData(first, startFrame))
            return null;
        Array.Copy(first, 0, interleaved, 0, first.Length);

        int remainingFrames = frameCount - firstFrames;
        if (remainingFrames > 0)
        {
            float[] second = new float[remainingFrames * channels];
            if (!microphoneClip.GetData(second, 0))
                return null;
            Array.Copy(second, 0, interleaved, first.Length, second.Length);
        }

        float[] mono = new float[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            float sum = 0f;
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                sum += interleaved[offset + channel];
            mono[frame] = sum / channels;
        }

        return mono;
    }

    private void SendAudioChunk(float[] mono, int sampleRate)
    {
        try
        {
            byte[] pcm = new byte[mono.Length * sizeof(float)];
            Buffer.BlockCopy(mono, 0, pcm, 0, pcm.Length);

            if (!BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < pcm.Length; i += sizeof(float))
                {
                    byte b0 = pcm[i];
                    byte b1 = pcm[i + 1];
                    pcm[i] = pcm[i + 3];
                    pcm[i + 1] = pcm[i + 2];
                    pcm[i + 2] = b1;
                    pcm[i + 3] = b0;
                }
            }

            AudioChunkPacket packet = new AudioChunkPacket();
            packet.sample_rate = sampleRate;
            packet.pcm_f32le_b64 = Convert.ToBase64String(pcm);
            worker.StandardInput.WriteLine(JsonUtility.ToJson(packet));
        }
        catch (Exception exception)
        {
            capturing = false;
            Debug.LogError("RealtimeOpenSmileInput: audio stream write failed. " + exception.Message, this);
            EndMicrophone();
        }
    }

    private bool SendWorkerControl(string command)
    {
        try
        {
            if (worker == null || worker.HasExited)
                return false;
            WorkerControlPacket packet = new WorkerControlPacket { type = command };
            worker.StandardInput.WriteLine(JsonUtility.ToJson(packet));
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError("RealtimeOpenSmileInput: could not send worker command. " + exception.Message, this);
            return false;
        }
    }

    private void DrainWorkerOutput()
    {
        string line;
        while (stdoutQueue.TryDequeue(out line))
        {
            WorkerMessage message = null;
            try
            {
                message = JsonUtility.FromJson<WorkerMessage>(line);
            }
            catch (Exception)
            {
            }

            if (message != null && message.type == "error")
            {
                awaitingAnalysis = false;
                startRequested = false;
                if (capturing)
                {
                    capturing = false;
                    EndMicrophone();
                }
                Debug.LogError("openSMILE worker: " + message.message, this);
                if (visualController != null)
                    visualController.SetSpeechText("");
                continue;
            }

            if (message != null && message.type == "transcript")
            {
                if (capturing)
                {
                    LatestTranscript = message.text ?? "";
                    if (visualController != null)
                        visualController.SetSpeechText(LatestTranscript);
                }
                continue;
            }

            if (message != null && message.type == "transcription_warning")
            {
                Debug.LogWarning("openSMILE worker: transcription preview: " + message.message, this);
                continue;
            }

            if (message != null && message.type == "ready")
            {
                workerReady = true;
                Debug.Log("RealtimeOpenSmileInput: voice analysis is ready.", this);
                if (startRequested)
                    StartMicrophoneCapture();
                continue;
            }

            if (message != null && message.type == "features")
            {
                awaitingAnalysis = false;
                LatestFeaturesJson = line;
                LatestTranscript = message.transcript ?? "";
                if (visualController != null)
                    visualController.SetSpeechText(LatestTranscript, true);
                if (logFeatureJson)
                    Debug.Log("openSMILE full-recording features: " + line, this);
                onFeaturesJsonReceived.Invoke(line);
            }
        }

        while (stderrQueue.TryDequeue(out line))
            Debug.LogWarning("openSMILE worker: " + line, this);
    }

    private void EndMicrophone()
    {
        if (!string.IsNullOrEmpty(activeDevice) && Microphone.IsRecording(activeDevice))
            Microphone.End(activeDevice);
        microphoneClip = null;
    }

    private void StopWorker()
    {
        workerReady = false;
        if (worker == null)
            return;

        try
        {
            if (!worker.HasExited)
            {
                SendWorkerControl("shutdown");
                worker.StandardInput.Close();
                if (!worker.WaitForExit(1000))
                    worker.Kill();
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            worker.Dispose();
            worker = null;
        }
    }

    private void OnDisable()
    {
        capturing = false;
        startRequested = false;
        awaitingAnalysis = false;
        EndMicrophone();
        StopWorker();
    }

    private void OnApplicationQuit()
    {
        capturing = false;
        EndMicrophone();
        StopWorker();
    }
}
