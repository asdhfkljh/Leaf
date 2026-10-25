# openSMILE voice comparison (offline)

This helper extracts the eGeMAPSv02 feature set from WAV recordings. It creates:

- `comparison_functionals.csv`: one row per recording with summary/functionals (88 features).
- `<recording>_lld.csv`: frame-by-frame low-level descriptors for plotting and comparison.

## Prepare recordings

Record the same short sentence in several voice conditions, for example:

- neutral, natural speaking voice
- deliberately lower pitch / slower speech
- louder, more emphatic speech
- a second speaker using the same sentence

Use separate WAV files, ideally mono, with the same microphone position and recording level. Keep the spoken words and recording conditions consistent when you want to compare delivery rather than content. Give each file a descriptive name such as `speaker1_neutral.wav`.

## Install and run on macOS

Use Python 3.9 or newer. The real-time requirements pin scikit-learn 1.5.2 to match the published SVM model. From Terminal, create an isolated virtual environment outside the Unity project, then install the dependencies:

```sh
python3 -m venv ~/opensmile-venv
source ~/opensmile-venv/bin/activate
python -m pip install -r "/Users/huangruiqing/studio3/Tools/openSMILE/requirements.txt"
```

Put recordings in a folder such as `~/Desktop/voice_samples`, then run:

```sh
python "/Users/huangruiqing/studio3/Tools/openSMILE/analyze_audio.py" \
  ~/Desktop/voice_samples ~/Desktop/voice_analysis
```

The first CSV is the easiest place to compare overall pitch, loudness, voice quality, and spectral features. The LLD CSVs contain time-varying measurements for plotting. openSMILE does not decide whether a voice is authoritative; use these measurements to explore candidate rules against labeled examples before connecting a score to Unity.

## License note

The openSMILE open-source version is for private, research, and educational use; commercial product use requires a separate license. Check the upstream license before distributing or commercializing a build.

## Unity real-time prototype (macOS Editor)

Assets/Scripts/RealtimeOpenSmileInput.cs starts a persistent local Python worker when Play mode begins. The worker loads openSMILE and the voice classifier once, then waits for recordings. Microphone capture is controlled manually and the worker analyzes the complete utterance only after recording stops.

1. Install the real-time dependencies in a Python environment (PyTorch/SpeechBrain are large packages):
   python3 -m venv ~/opensmile-venv
   source ~/opensmile-venv/bin/activate
   python -m pip install -r "/Users/huangruiqing/studio3/Tools/openSMILE/realtime_requirements.txt"
2. In the RealtimeOpenSmileInput component, set Python Executable to the environment's Python path (for example ~/opensmile-venv/bin/python).
3. Enter Play mode in SampleScene. The Python worker loads once in the background; the microphone remains off until you press Alpha1. Wait for the Console message `voice analysis is ready`, then press Alpha1 to record. Press Alpha1 again to stop and analyze the complete utterance. On first run, model files are downloaded from Hugging Face, so network access is required.
4. On stop, openSMILE computes functionals across the full recording and the repository's SpeechBrain ECAPA-TDNN plus SVM predicts the voice group. AuthorityVoiceClassifier selects that group's editable ranges and triggers High only when all five configured metrics are in range; otherwise it triggers Low. The visual sequence restores the text after it finishes, allowing another recording. A recording is limited to 180 seconds by default.

The upstream project describes this as binary voice gender classification and reports test accuracy on VoxCeleb2, Common Voice, and TIMIT. Its README also notes performance may vary with audio quality, recording conditions, and multiple speakers. It predicts a binary voice class; do not treat that output as confirmation of a person's gender identity. See the upstream [model README](https://github.com/griko/voice-gender-classification) and [license](https://github.com/griko/voice-gender-classification/blob/main/LICENSE).

The result is emitted after the second Alpha1 press and full-recording analysis. Empty or very short recordings are rejected. VoicedSegmentsPerSec is not speech rate; add speech recognition or syllable detection for words/characters per minute. The configured authority ranges were collected from recordings, so full-recording analysis aligns better with them than rolling-window analysis.

This prototype launches a local Python process and therefore requires Python plus openSMILE, PyTorch, SpeechBrain, and the gender-classification package on the test computer. It is intended for local research/educational testing. Review the openSMILE license before distribution or commercial use.
