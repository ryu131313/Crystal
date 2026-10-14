"""Measures the weapon line (pommel end -> tip end) in every frame of a reference weapon .Lib."""
import math

BINS = 12


def measure(im):
    """Principal axis of the opaque pixels. Returns dict or None for an empty frame."""
    pts = []
    for j in range(im.h):
        for i in range(im.w):
            o = (j * im.w + i) * 4
            if im.rgba[o + 3] > 0:
                pts.append((i + 0.5, j + 0.5, im.rgba[o], im.rgba[o + 1], im.rgba[o + 2]))
    if len(pts) < 2: return None
    n = len(pts)
    mx = sum(p[0] for p in pts) / n; my = sum(p[1] for p in pts) / n
    sxx = sum((p[0] - mx) ** 2 for p in pts); syy = sum((p[1] - my) ** 2 for p in pts)
    sxy = sum((p[0] - mx) * (p[1] - my) for p in pts)
    ang = 0.5 * math.atan2(2 * sxy, sxx - syy)
    ux, uy = math.cos(ang), math.sin(ang)
    proj = [(p[0] - mx) * ux + (p[1] - my) * uy for p in pts]
    perp = [-(p[0] - mx) * uy + (p[1] - my) * ux for p in pts]
    t0, t1 = min(proj) - 0.5, max(proj) + 0.5
    # colour profile along the axis (t0 -> t1)
    prof = [[0, 0, 0, 0] for _ in range(BINS)]
    for p, t in zip(pts, proj):
        b = min(BINS - 1, int((t - t0) / (t1 - t0) * BINS))
        prof[b][0] += p[2]; prof[b][1] += p[3]; prof[b][2] += p[4]; prof[b][3] += 1
    prof = [(c[0] / c[3], c[1] / c[3], c[2] / c[3]) if c[3] else None for c in prof]
    # where the grey (low-saturation) guard pixels sit along the axis: -1 = t0 end, +1 = t1 end
    gw = gs = 0.0
    for p, t in zip(pts, proj):
        hi, lo = max(p[2:5]), min(p[2:5])
        grey = 1.0 - (hi - lo) / hi if hi else 0.0
        if grey > 0.6:
            gw += grey; gs += grey * t
    guard = (gs / gw - (t0 + t1) / 2) / ((t1 - t0) / 2) if gw else 0.0
    return dict(cx=mx, cy=my, ux=ux, uy=uy, t0=t0, t1=t1, length=t1 - t0,
                width=max(perp) - min(perp) + 1, npx=n, prof=prof, guard=guard)


def prof_dist(a, b):
    s, c = 0.0, 0
    for p, q in zip(a, b):
        if p and q:
            s += sum((x - y) ** 2 for x, y in zip(p, q)); c += 1
    return s / c if c else 1e9


def endpoints(m, flip):
    """(pommel, tip) in frame-pixel coords; flip = profile runs tip -> pommel."""
    a = (m["cx"] + m["ux"] * m["t0"], m["cy"] + m["uy"] * m["t0"])
    b = (m["cx"] + m["ux"] * m["t1"], m["cy"] + m["uy"] * m["t1"])
    return (b, a) if flip else (a, b)


def orient_all(frames, ref_prof):
    """For each measured frame decide which end is the pommel, by matching the colour profile
    of the master frame (ref_prof runs pommel -> tip). Returns {idx: (pommel, tip, confidence)}."""
    out = {}
    for idx, m in frames.items():
        if abs(m["guard"]) > 0.15:
            # guard sits nearer the pommel: guard on the t1 side means the line runs tip -> pommel
            flip = m["guard"] > 0
            conf = min(1.0, abs(m["guard"]) * 2)
        else:
            fwd = prof_dist(m["prof"], ref_prof); rev = prof_dist(m["prof"], ref_prof[::-1])
            flip = rev < fwd
            conf = 0.5 * abs(fwd - rev) / max(fwd, rev, 1e-9)
        p, t = endpoints(m, flip)
        out[idx] = (p, t, conf)
    return out
