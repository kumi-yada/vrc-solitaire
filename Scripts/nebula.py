#!/usr/bin/env python3
"""
nebula.py - procedurally render colourful nebula clouds with a star field.

Requires:  pip install numpy pillow
Usage:     python nebula.py --seed 7 --width 1920 --height 1080 -o nebula.png
"""
import argparse
import numpy as np
from PIL import Image


# ----------------------------------------------------------------------------
# Noise helpers
# ----------------------------------------------------------------------------
def value_noise(h, w, cells, rng):
    """Smoothly interpolated random lattice -> (h, w) array in [0, 1]."""
    cx = cells
    cy = max(2, int(np.ceil(cells * h / w)))
    grid = rng.random((cy + 1, cx + 1))

    y = np.linspace(0, cy, h, endpoint=False)
    x = np.linspace(0, cx, w, endpoint=False)
    y0, x0 = y.astype(int), x.astype(int)
    fy, fx = y - y0, x - x0
    fy = (fy * fy * (3 - 2 * fy))[:, None]          # smoothstep easing
    fx = (fx * fx * (3 - 2 * fx))[None, :]

    a = grid[np.ix_(y0, x0)]
    b = grid[np.ix_(y0, x0 + 1)]
    c = grid[np.ix_(y0 + 1, x0)]
    d = grid[np.ix_(y0 + 1, x0 + 1)]
    top = a + (b - a) * fx
    bot = c + (d - c) * fx
    return top + (bot - top) * fy


def fbm(h, w, rng, base=4, octaves=7, persistence=0.5):
    """Fractal Brownian motion, contrast-stretched to [0, 1]."""
    total = np.zeros((h, w))
    amp, norm, cells = 1.0, 0.0, base
    for _ in range(octaves):
        total += amp * value_noise(h, w, cells, rng)
        norm += amp
        amp *= persistence
        cells *= 2
    total /= norm
    lo, hi = np.percentile(total, (1, 99))
    return np.clip((total - lo) / (hi - lo + 1e-9), 0, 1)


def blur(img, sigma):
    """Gaussian blur via FFT (works on 2-D or H x W x C arrays)."""
    if sigma <= 0:
        return img
    if img.ndim == 3:
        return np.stack([blur(img[..., c], sigma) for c in range(img.shape[2])], -1)
    h, w = img.shape
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.rfftfreq(w)[None, :]
    kernel = np.exp(-2 * (np.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    return np.fft.irfft2(np.fft.rfft2(img) * kernel, s=(h, w))


# ----------------------------------------------------------------------------
# Scene layers
# ----------------------------------------------------------------------------
def nebula_layer(H, W, rng):
    palette = np.array([
        (0.95, 0.25, 0.55),   # magenta / H-alpha pink
        (0.25, 0.35, 1.00),   # deep blue
        (0.10, 0.80, 0.80),   # teal / OIII
        (1.00, 0.55, 0.20),   # warm orange
        (0.55, 0.25, 0.95),   # violet
    ])
    order = rng.permutation(len(palette))[:3]
    strengths = [0.55, 0.35, 0.25]

    img = np.zeros((H, W, 3))
    for col, s in zip(palette[order], strengths):
        density = fbm(H, W, rng, base=3)
        mask = fbm(H, W, rng, base=2, octaves=4)
        wisps = (1 - np.abs(2 * fbm(H, W, rng, base=4) - 1)) ** 5   # ridged filaments
        cloud = density ** 2.5 * mask ** 1.5 + 0.5 * wisps * density ** 2 * mask
        img += s * cloud[..., None] * col
    return img


STAR_COLORS = np.array([
    (0.65, 0.75, 1.00), (0.90, 0.93, 1.00), (1.00, 1.00, 1.00),
    (1.00, 0.92, 0.75), (1.00, 0.75, 0.55),
])


def add_spiky_star(img, x, y, b, col, length):
    H, W, _ = img.shape
    L = int(length)
    y0, y1 = max(0, y - L), min(H, y + L + 1)
    x0, x1 = max(0, x - L), min(W, x + L + 1)
    yy, xx = np.mgrid[y0:y1, x0:x1]
    dx, dy = xx - x, yy - y
    spikes = (np.exp(-np.abs(dx) / (L * 0.25)) * np.exp(-dy ** 2 / 0.6)
              + np.exp(-np.abs(dy) / (L * 0.25)) * np.exp(-dx ** 2 / 0.6))
    core = np.exp(-(dx ** 2 + dy ** 2) / (2 * 1.5 ** 2))
    img[y0:y1, x0:x1] += b * (0.6 * spikes + core)[..., None] * col


def star_layer(H, W, rng):
    stars = np.zeros((H, W, 3))

    # field stars
    n = int(W * H * 0.0015)
    xs, ys = rng.integers(0, W, n), rng.integers(0, H, n)
    b = 0.03 + 0.25 * rng.pareto(2.2, n)
    np.add.at(stars, (ys, xs), b[:, None] * STAR_COLORS[rng.integers(0, 5, n)])

    # soft glow around every star
    stars = stars + 4 * blur(stars, 1.0) + 6 * blur(stars, 4.0)

    # a handful of bright foreground stars with diffraction spikes
    for _ in range(rng.integers(5, 12)):
        add_spiky_star(stars, int(rng.integers(0, W)), int(rng.integers(0, H)),
                       rng.uniform(0.6, 2.0), STAR_COLORS[rng.integers(0, 5)],
                       min(W, H) * rng.uniform(0.02, 0.05))
    return stars


# ----------------------------------------------------------------------------
# Render
# ----------------------------------------------------------------------------
def render(W, H, seed, exposure_boost=1.0):
    rng = np.random.default_rng(seed)

    print("Nebula clouds...")
    hdr = nebula_layer(H, W, rng)

    print("Stars...")
    hdr += star_layer(H, W, rng)

    print("Bloom & tone mapping...")
    hdr += 0.6 * blur(hdr, min(W, H) * 0.015)

    lum = hdr.mean(-1)
    exposure = exposure_boost * 2.5 / (np.percentile(lum, 99.7) + 1e-9)
    img = 1 - np.exp(-hdr * exposure)                    # filmic-ish tone curve

    # vignette
    yy, xx = np.mgrid[0:H, 0:W]
    d = np.hypot((xx - W / 2) / (W / 2), (yy - H / 2) / (H / 2))
    img *= (1 - 0.35 * d ** 2)[..., None]

    img = np.clip(img, 0, 1) ** (1 / 1.1)
    return (img * 255).astype(np.uint8)


def main():
    p = argparse.ArgumentParser(description="Procedural nebula generator")
    p.add_argument("--width", type=int, default=1920)
    p.add_argument("--height", type=int, default=1080)
    p.add_argument("--seed", type=int, default=None, help="random seed (omit for random)")
    p.add_argument("--exposure", type=float, default=1.0, help="brightness multiplier")
    p.add_argument("-o", "--output", default="nebula.png")
    args = p.parse_args()

    seed = args.seed if args.seed is not None else np.random.SeedSequence().entropy % 10**6
    print(f"Seed: {seed}")
    img = render(args.width, args.height, seed, args.exposure)
    Image.fromarray(img).save(args.output)
    print(f"Saved {args.output}")


if __name__ == "__main__":
    main()
