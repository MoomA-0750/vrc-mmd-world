#!/usr/bin/env python3
"""テスト用のクリック音（メトロノーム）を作る。曲の代わりに、音と踊りの同期を確かめるのに使う。

使い方: make_click_track.py 出力.wav 秒数 [BPM]
"""
import math
import struct
import sys
import wave

RATE = 22050


def main():
    out, seconds = sys.argv[1], float(sys.argv[2])
    bpm = float(sys.argv[3]) if len(sys.argv) > 3 else 120.0
    n = int(seconds * RATE)
    beat = 60.0 / bpm
    samples = bytearray()
    for i in range(n):
        t = i / RATE
        k = int(t / beat)
        dt = t - k * beat
        # 小節の頭（4拍ごと）は高い音
        freq = 1760.0 if k % 4 == 0 else 880.0
        v = math.sin(2 * math.pi * freq * dt) * math.exp(-dt * 40.0) if dt < 0.1 else 0.0
        samples += struct.pack('<h', int(v * 0.6 * 32767))
    with wave.open(out, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(bytes(samples))


if __name__ == '__main__':
    main()
