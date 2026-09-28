from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import soundfile as sf
from kokoro_onnx import Kokoro
from misaki.zh import ZHG2P


def estimate_median_f0(samples: np.ndarray, sample_rate: int) -> float:
    frame_length = max(1, int(sample_rate * 0.04))
    hop_length = max(1, int(sample_rate * 0.02))
    minimum_lag = max(1, int(sample_rate / 300))
    maximum_lag = max(minimum_lag + 1, int(sample_rate / 65))
    values: list[float] = []

    for start in range(
        0,
        max(0, len(samples) - frame_length),
        hop_length,
    ):
        frame = samples[start : start + frame_length].astype(
            np.float64,
            copy=False,
        )
        frame = frame - frame.mean()
        energy = float(np.sqrt(np.mean(frame * frame)))
        if energy < 0.008:
            continue

        correlation = np.correlate(frame, frame, mode="full")[
            frame_length - 1 :
        ]
        window = correlation[: maximum_lag + 1]
        if window[0] <= 0:
            continue
        window = window / window[0]
        peak = minimum_lag + int(np.argmax(window[minimum_lag : maximum_lag + 1]))
        peak_value = float(window[peak])
        if peak_value >= 0.32:
            values.append(sample_rate / peak)

    return float(np.median(values)) if values else 0.0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate and rank Kokoro Chinese male voices.",
    )
    parser.add_argument("--model", required=True)
    parser.add_argument("--voices", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--speed", type=float, default=1.08)
    parser.add_argument(
        "--text",
        default="贾维斯系统已就绪。请下达指令，我会立即执行。",
    )
    args = parser.parse_args()

    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    print("Loading Kokoro...", flush=True)
    kokoro = Kokoro(args.model, args.voices)
    g2p = ZHG2P()
    phonemes, _ = g2p(args.text)
    if not phonemes:
        raise RuntimeError("Chinese G2P produced no phonemes.")

    male_voices = sorted(
        voice for voice in kokoro.get_voices() if voice.startswith("zm_")
    )
    results: list[dict[str, float | int | str]] = []
    for index, voice in enumerate(male_voices, start=1):
        print(f"[{index}/{len(male_voices)}] {voice}", flush=True)
        samples, sample_rate = kokoro.create(
            phonemes,
            voice=voice,
            speed=args.speed,
            is_phonemes=True,
            trim=True,
        )
        samples = np.asarray(samples, dtype=np.float32)
        peak = float(np.max(np.abs(samples))) if len(samples) else 0.0
        if peak > 0:
            samples = samples * min(0.92 / peak, 1.0)
        path = output / f"{voice}.wav"
        sf.write(path, samples, sample_rate, subtype="PCM_16")
        spectrum = np.abs(np.fft.rfft(samples * np.hanning(len(samples))))
        frequencies = np.fft.rfftfreq(len(samples), 1 / sample_rate)
        spectral_centroid = (
            float(np.sum(frequencies * spectrum) / np.sum(spectrum))
            if np.sum(spectrum) > 0
            else 0.0
        )
        results.append(
            {
                "voice": voice,
                "durationSeconds": round(len(samples) / sample_rate, 3),
                "medianF0Hz": round(estimate_median_f0(samples, sample_rate), 2),
                "spectralCentroidHz": round(spectral_centroid, 2),
                "rms": round(float(np.sqrt(np.mean(samples * samples))), 5),
            }
        )

    # Low pitch is the first approximation of a calm JARVIS-like male voice.
    results.sort(key=lambda item: float(item["medianF0Hz"]))
    (output / "ranking.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    for result in results[:15]:
        print(
            "{voice}: f0={medianF0Hz}Hz centroid={spectralCentroidHz}Hz "
            "duration={durationSeconds}s".format(**result),
            flush=True,
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
