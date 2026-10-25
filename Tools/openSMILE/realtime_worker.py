#!/usr/bin/env python3
"""Keep the speech models warm and analyze each complete recording on finalize."""

import argparse
import base64
import contextlib
import json
import math
import sys
import warnings
import threading
from speech_transcription import SpeechTranscriber

GENDER_MODEL = "griko/gender_cls_svm_ecapa_voxceleb"
OUTPUT_LOCK = threading.Lock()


def emit(packet):
    with OUTPUT_LOCK:
        print(json.dumps(packet, ensure_ascii=False, allow_nan=False), flush=True)


def finite_value(value):
    value = float(value)
    return value if math.isfinite(value) else None


def main():
    warnings.filterwarnings(
        "ignore",
        message=r"torchaudio\._backend\.list_audio_backends has been deprecated.*",
        category=UserWarning,
    )
    with contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        import opensmile
        import torch
        import torchaudio
        from voice_gender_classification import GenderClassificationPipeline
        from faster_whisper import WhisperModel

    parser = argparse.ArgumentParser()
    parser.add_argument("--max-recording-seconds", type=int, default=180)
    parser.add_argument("--whisper-model", default="base")
    parser.add_argument("--speech-language", default="auto")
    parser.add_argument("--transcription-interval", type=float, default=2.0)
    args = parser.parse_args()

    with contextlib.redirect_stdout(sys.stderr):
        smile = opensmile.Smile(
            feature_set=opensmile.FeatureSet.eGeMAPSv02,
            feature_level=opensmile.FeatureLevel.Functionals,
        )
        gender_classifier = GenderClassificationPipeline.from_pretrained(GENDER_MODEL, device="cpu")
        whisper = WhisperModel(args.whisper_model, device="cpu", compute_type="int8", cpu_threads=4)
    transcriber = SpeechTranscriber(whisper, args.speech_language, emit)
    emit({"type": "ready"})

    recording = False
    chunks = []
    active_rate = None
    recorded_samples = 0
    preview_samples = 0

    for line in sys.stdin:
        try:
            packet = json.loads(line)
            command = packet.get("type")

            if command == "shutdown":
                transcriber.close()
                break
            if command == "start":
                transcriber.reset()
                chunks = []
                active_rate = None
                recorded_samples = 0
                preview_samples = 0
                recording = True
                continue
            if command == "cancel":
                transcriber.reset()
                chunks = []
                active_rate = None
                recorded_samples = 0
                recording = False
                continue
            if command == "finalize":
                recording = False
                try:
                    if not chunks or active_rate is None:
                        raise ValueError("No microphone audio was captured in this recording.")

                    signal = np.concatenate(chunks).astype(np.float32, copy=False)
                    duration = signal.size / active_rate
                    if duration < 0.5:
                        raise ValueError("Recording is too short. Speak for at least 1 second, then stop.")

                    row = smile.process_signal(signal, active_rate).iloc[0]
                    rms = float(np.sqrt(np.mean(np.square(signal.astype(np.float64)))))
                    rms_dbfs = 20.0 * math.log10(max(rms, 1e-8))
                    voiced_segments_per_second = finite_value(row["VoicedSegmentsPerSec"])
                    if (
                        rms_dbfs <= -70.0
                        or voiced_segments_per_second is None
                        or voiced_segments_per_second <= 0.25
                    ):
                        raise ValueError("No clear voiced speech was detected. Check the microphone and try again.")

                    speech_signal = torch.from_numpy(signal.copy())
                    if active_rate != 16000:
                        speech_signal = torchaudio.functional.resample(speech_signal, active_rate, 16000)
                    transcript = transcriber.finalize(speech_signal.numpy())
                    if not transcript:
                        raise ValueError("No speech text was recognized. Please speak clearly and try again.")

                    gender_waveform = torch.from_numpy(signal.copy())
                    if active_rate != 16000:
                        gender_waveform = torchaudio.functional.resample(
                            gender_waveform, active_rate, 16000
                        )
                    with contextlib.redirect_stdout(sys.stderr):
                        prediction = gender_classifier(gender_waveform)
                    if not prediction or prediction[0] not in ("female", "male"):
                        raise ValueError("The voice gender model did not return a valid prediction.")

                    f0_median_st = finite_value(row["F0semitoneFrom27.5Hz_sma3nz_percentile50.0"])
                    f0_p20_st = finite_value(row["F0semitoneFrom27.5Hz_sma3nz_percentile20.0"])
                    f0_p80_st = finite_value(row["F0semitoneFrom27.5Hz_sma3nz_percentile80.0"])
                    f0_hz = 27.5 * (2.0 ** (f0_median_st / 12.0)) if f0_median_st is not None else None
                    f0_p20_hz = 27.5 * (2.0 ** (f0_p20_st / 12.0)) if f0_p20_st is not None else None
                    f0_p80_hz = 27.5 * (2.0 ** (f0_p80_st / 12.0)) if f0_p80_st is not None else None

                    result = {
                        "type": "features",
                        "transcript": transcript,
                        "speaker_gender": prediction[0],
                        "analysis_duration_seconds": duration,
                        "sample_rate": active_rate,
                        "f0_median_hz": finite_value(f0_hz) if f0_hz is not None else None,
                        "f0_p20_hz": finite_value(f0_p20_hz) if f0_p20_hz is not None else None,
                        "f0_p80_hz": finite_value(f0_p80_hz) if f0_p80_hz is not None else None,
                        "f0_span_semitones": finite_value(row["F0semitoneFrom27.5Hz_sma3nz_pctlrange0-2"]),
                        "f0_stddev_norm": finite_value(row["F0semitoneFrom27.5Hz_sma3nz_stddevNorm"]),
                        "loudness_median": finite_value(row["loudness_sma3_percentile50.0"]),
                        "loudness_span": finite_value(row["loudness_sma3_pctlrange0-2"]),
                        "equivalent_level_dbp": finite_value(row["equivalentSoundLevel_dBp"]),
                        "rms_dbfs": finite_value(rms_dbfs),
                        "jitter_local": finite_value(row["jitterLocal_sma3nz_amean"]),
                        "shimmer_local_db": finite_value(row["shimmerLocaldB_sma3nz_amean"]),
                        "hnr_db": finite_value(row["HNRdBACF_sma3nz_amean"]),
                        "voiced_segments_per_second": voiced_segments_per_second,
                    }
                    emit(result)
                except Exception as exception:
                    transcriber.reset()
                    emit({"type": "error", "message": str(exception)})
                finally:
                    chunks = []
                    active_rate = None
                    recorded_samples = 0
                continue

            if not recording or "pcm_f32le_b64" not in packet:
                continue

            sample_rate = int(packet["sample_rate"])
            pcm = np.frombuffer(
                base64.b64decode(packet["pcm_f32le_b64"]),
                dtype="<f4",
            ).astype(np.float32, copy=False)
            if sample_rate <= 0 or pcm.size == 0:
                continue
            if active_rate is None:
                active_rate = sample_rate
            elif active_rate != sample_rate:
                raise ValueError("Microphone sample rate changed during recording.")

            recorded_samples += pcm.size
            if recorded_samples > (max(1, args.max_recording_seconds) + 1) * active_rate:
                raise ValueError("Maximum recording duration exceeded.")
            chunks.append(pcm.copy())
            if recorded_samples - preview_samples >= max(0.5, args.transcription_interval) * active_rate:
                preview_samples = recorded_samples
                preview_signal = torch.from_numpy(np.concatenate(chunks).astype(np.float32, copy=False))
                if active_rate != 16000:
                    preview_signal = torchaudio.functional.resample(preview_signal, active_rate, 16000)
                transcriber.preview(preview_signal.numpy())

        except Exception as exception:
            transcriber.reset()
            recording = False
            chunks = []
            active_rate = None
            recorded_samples = 0
            emit({"type": "error", "message": str(exception)})


if __name__ == "__main__":
    try:
        main()
    except Exception as exception:
        emit({"type": "error", "message": str(exception)})
