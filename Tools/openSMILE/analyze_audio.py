#!/usr/bin/env python3
"""Batch-extract eGeMAPSv02 speech features from WAV files with openSMILE."""

import argparse
from pathlib import Path

import audiofile
import numpy as np
import opensmile
import pandas as pd


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Extract openSMILE eGeMAPSv02 features from WAV recordings."
    )
    parser.add_argument("input_dir", type=Path, help="Folder containing .wav recordings")
    parser.add_argument("output_dir", type=Path, help="Folder for the result CSV files")
    args = parser.parse_args()

    files = sorted(p for p in args.input_dir.glob("*") if p.is_file() and p.suffix.lower() == ".wav")
    if not files:
        raise SystemExit(f"No WAV files found in: {args.input_dir}")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    functional_extractor = opensmile.Smile(
        feature_set=opensmile.FeatureSet.eGeMAPSv02,
        feature_level=opensmile.FeatureLevel.Functionals,
    )
    lld_extractor = opensmile.Smile(
        feature_set=opensmile.FeatureSet.eGeMAPSv02,
        feature_level=opensmile.FeatureLevel.LowLevelDescriptors,
    )

    functionals = []
    for audio_file in files:
        signal, sampling_rate = audiofile.read(str(audio_file))
        signal = np.asarray(signal)
        if signal.ndim == 1:
            selected_channel = 0
            selected_signal = signal
        else:
            channel_rms = np.sqrt(np.mean(np.square(signal.astype(np.float64)), axis=1))
            selected_channel = int(np.argmax(channel_rms))
            selected_signal = signal[selected_channel]

        signal_rms = float(np.sqrt(np.mean(np.square(selected_signal.astype(np.float64)))))
        print(
            f"Analyzing: {audio_file.name} "
            f"(channel {selected_channel + 1}, RMS {signal_rms:.5f})"
        )
        row = functional_extractor.process_signal(
            selected_signal, sampling_rate, file=audio_file.name
        ).reset_index(drop=True)
        row.insert(0, "file", audio_file.name)
        row.insert(1, "selected_channel", selected_channel + 1)
        row.insert(2, "selected_channel_rms", signal_rms)
        functionals.append(row)

        lld = lld_extractor.process_signal(
            selected_signal, sampling_rate, file=audio_file.name
        ).reset_index()
        lld.to_csv(args.output_dir / f"{audio_file.stem}_lld.csv", index=False)

    pd.concat(functionals, ignore_index=True).to_csv(
        args.output_dir / "comparison_functionals.csv", index=False
    )
    print(f"\nDone. {len(files)} recordings analyzed.")
    print(f"Summary features: {args.output_dir / 'comparison_functionals.csv'}")
    print(f"Frame-by-frame LLD files: {args.output_dir}")
    print("These are acoustic measurements, not an authority classification.")


if __name__ == "__main__":
    main()
