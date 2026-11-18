#!/usr/bin/env bash
# The sound measurements' check: WAV files made here with known answers (a 440 Hz tone that loops, a fading 220 Hz
# tone, white noise, a slow swell; 16-bit, 24-bit stereo, float) measured by ShelfIndex sounds --measure.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
s="${HOME}/.cache/asset-shelf/scratch/sound-check"
rm -rf "${s:?}"; mkdir -p "$s"
dotnet build "$root/tools/ShelfIndex" -c Release -v q -nologo >/dev/null
python3 -I - "$s" <<'PY'
import math, random, struct, sys, wave
s = sys.argv[1]; rate = 48000
def write(name, frames, channels=1, width=2, fmt=1):
    data = b""
    for f in frames:
        for c in range(channels):
            v = max(-1.0, min(1.0, f))
            data += {2: lambda: struct.pack("<h", int(v * 32767)), 3: lambda: int(v * 8388607).to_bytes(3, "little", signed=True), 4: lambda: struct.pack("<f", v)}[width]()
    head = struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", 36 + len(data), b"WAVE", b"fmt ", 16, fmt, channels, rate, rate * channels * width, channels * width, width * 8, b"data", len(data))
    open(f"{s}/{name}.wav", "wb").write(head + data)
n = rate * 2
write("tone440", [0.5 * math.sin(2 * math.pi * 440 * i / rate) for i in range(rate)])          # exactly 440 cycles: loops
write("fade220", [0.8 * math.sin(2 * math.pi * 220 * i / rate) * (1 - i / n) for i in range(n)], channels=2, width=3)
random.seed(1); write("noise", [random.uniform(-0.3, 0.3) for _ in range(n)], width=4, fmt=3)
write("swell", [0.6 * math.sin(2 * math.pi * 330 * i / rate) * min(1, i / rate) for i in range(n)])
PY
m() { "$root/tools/ShelfIndex/bin/Release/net10.0/ShelfIndex" sounds --measure "$s/$1.wav"; }
check() { python3 -I -c "import json,sys; d=json.loads(sys.argv[1]); assert $2, d" "$(m "$1")" && echo "ok $1: $3"; }
check tone440 'abs(d["pitchHz"]-440)<3 and d["loops"] and abs(d["peakDb"]+6)<0.2 and abs(d["seconds"]-1)<0.01 and 380<d["brightnessHz"]<520' "440 Hz, loops, -6 dB peak"
check fade220 'abs(d["pitchHz"]-220)<2 and not d["loops"] and d["channels"]==2 and d["bits"]==24' "220 Hz, fades out so no loop, 24-bit stereo"
check noise 'd["pitchHz"] is None and d["brightnessHz"]>8000 and d["bits"]==32' "no pitch, bright, float"
check swell 'd["attackMs"]>800 and abs(d["pitchHz"]-330)<3' "a slow attack (about 1 s)"
echo "all passed"; rm -rf "${s:?}"
