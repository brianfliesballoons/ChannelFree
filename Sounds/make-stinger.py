"""Cable Package 'Up Next' stinger — original 80s-cable-style synth sting (~3.6 s).
FM bell arpeggio + warm detuned pad + soft bass pluck + light shimmer, through a simple stereo reverb."""
import sys
import numpy as np
from scipy.signal import butter, sosfilt
from scipy.io import wavfile

SR = 48000
LEN = 3.8
N = int(SR * LEN)
t_all = np.arange(N) / SR


def hz(note):  # MIDI -> Hz
    return 440.0 * 2 ** ((note - 69) / 12)


def env_adsr(n, a, d, s, r, sus_len):
    a_n, d_n, r_n = int(a * SR), int(d * SR), int(r * SR)
    s_n = max(0, int(sus_len * SR) - a_n - d_n)
    e = np.concatenate([
        np.linspace(0, 1, a_n, endpoint=False),
        np.linspace(1, s, d_n, endpoint=False),
        np.full(s_n, s),
        np.linspace(s, 0, r_n),
    ])
    return np.pad(e, (0, max(0, n - len(e))))[:n]


def place(buf, sig, start):
    i = int(start * SR)
    j = min(len(buf), i + len(sig))
    buf[i:j] += sig[: j - i]


def fm_bell(freq, dur, amp, ratio=3.5, index=1.1, detune=0.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    idx = index * np.exp(-t * 4)                     # bright attack, mellow tail (DX-style e-piano/bell)
    mod = np.sin(2 * np.pi * freq * ratio * t) * idx
    car = np.sin(2 * np.pi * freq * (1 + detune) * t + mod)
    env = np.minimum(t / 0.025, 1) ** 2 * np.exp(-t * 2.4)
    return amp * car * env


def saw(freq, t):
    # band-limited-ish saw via a few harmonics (soft)
    out = np.zeros_like(t)
    for k in range(1, 12):
        out += ((-1) ** (k + 1)) * np.sin(2 * np.pi * k * freq * t) / k
    return out * (2 / np.pi)


def lowpass(x, cutoff, order=2):
    return sosfilt(butter(order, cutoff, 'low', fs=SR, output='sos'), x)


L = np.zeros(N)
R = np.zeros(N)

# ---- key: D major. 16ths at ~138 bpm
step = 60 / 120 / 4

# 1) FM bell arpeggio (rising, then settles) — original figure
arp = [62, 66, 69, 73, 74, 78, 81, 76]  # D4 F#4 A4 C#5 D5 F#5 A5 E5
for i, note in enumerate(arp):
    start = 0.05 + i * step
    pan = 0.5 + 0.35 * np.sin(i * 1.3)               # gentle ping-pong
    b = fm_bell(hz(note), 1.8, 0.12)
    place(L, b * (1 - pan) * 1.4, start)
    place(R, b * pan * 1.4, start)

# resolve: soft bell chord (D6 + A5 + F#5) on the "one" after the run
res_t = 0.05 + len(arp) * step + step
for note, a in [(86, 0.10), (81, 0.09), (78, 0.08)]:
    b = fm_bell(hz(note), 2.6, a, ratio=2.0, index=0.7)
    place(L, b * 1.0, res_t)
    place(R, fm_bell(hz(note), 2.6, a, ratio=2.0, index=0.7, detune=0.002), res_t)

# 2) warm pad: Dmaj9 (D3 A3 C#4 E4 F#4), detuned saws, low-passed, slow swell
pad_len = LEN - 0.1
t = np.arange(int(pad_len * SR)) / SR
pad_env = env_adsr(len(t), 0.7, 0.6, 0.8, 1.5, pad_len - 1.5)
padL = np.zeros_like(t)
padR = np.zeros_like(t)
for note in [50, 57, 61, 64, 66]:
    f = hz(note)
    padL += saw(f * 0.997, t) + 0.6 * saw(f * 1.004, t)
    padR += saw(f * 1.003, t) + 0.6 * saw(f * 0.996, t)
# filter opens slightly as it swells
padL = lowpass(padL, 1400) * pad_env * 0.030
padR = lowpass(padR, 1400) * pad_env * 0.030
place(L, padL, 0.0)
place(R, padR, 0.0)

# 3) soft bass pluck: D2 at the start and at the resolve
for when, a in [(res_t, 0.10)]:
    tb = np.arange(int(1.4 * SR)) / SR
    f = hz(38)
    bass = np.sin(2 * np.pi * f * tb) + 0.25 * np.sin(2 * np.pi * 2 * f * tb)
    bass *= np.minimum(tb / 0.06, 1) * np.exp(-tb * 1.8) * a
    place(L, bass, when)
    place(R, bass, when)

# 4) light shimmer: filtered noise swell into the resolve (the "whoosh" that makes it feel like a transition)
ns = np.random.default_rng(7).standard_normal(int(0.9 * SR))
ns = sosfilt(butter(2, [3000, 9000], 'band', fs=SR, output='sos'), ns)
ns *= np.linspace(0, 1, len(ns)) ** 2 * 0.020
place(L, ns, res_t - 0.9)
place(R, ns[::-1][::-1], res_t - 0.9)


# ---- simple stereo reverb (Schroeder: 4 combs + 2 allpasses per side), plate-ish 80s sheen
def comb(x, d, g):
    y = np.copy(x)
    for i in range(d, len(x)):
        y[i] += g * y[i - d]
    return y


def allpass(x, d, g):
    y = np.zeros_like(x)
    for i in range(len(x)):
        xd = x[i - d] if i >= d else 0.0
        yd = y[i - d] if i >= d else 0.0
        y[i] = -g * x[i] + xd + g * yd
    return y


def reverb(x, seed):
    ds = [1557, 1617, 1491, 1422] if seed == 0 else [1587, 1647, 1521, 1452]
    wet = sum(comb(x, int(d * SR / 44100), 0.80) for d in ds) / 4
    wet = allpass(wet, int(225 * SR / 44100), 0.5)
    wet = allpass(wet, int(556 * SR / 44100), 0.5)
    return lowpass(wet, 5000)


dryL, dryR = L.copy(), R.copy()
L = dryL + 0.45 * reverb(dryL, 0)
R = dryR + 0.45 * reverb(dryR, 1)

L = lowpass(L, 5500)
R = lowpass(R, 5500)
# final fade + normalize to -3 dBFS peak
fade = np.ones(N)
fn = int(0.5 * SR)
fade[-fn:] = np.linspace(1, 0, fn) ** 1.5
L *= fade
R *= fade
peak = max(np.abs(L).max(), np.abs(R).max())
g = 10 ** (-3 / 20) / peak
out = np.stack([L * g, R * g], axis=1).astype(np.float32)
wavfile.write(sys.argv[1] if len(sys.argv) > 1 else 'stinger.wav', SR, out)
print('ok', LEN, 's, resolve at', round(res_t, 2), 's')
