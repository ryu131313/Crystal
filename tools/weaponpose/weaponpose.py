"""Pose a single upright weapon picture into every frame of a Crystal player-weapon .Lib.

  python -I weaponpose.py make-input --ref 00.Lib --out upright.png [--zoom 8]
      Straightens the reference weapon's longest frame into an upright picture
      (tip up, pommel down) - the input format, and the round-trip test input.

  python -I weaponpose.py render --ref 00.Lib --input weapon.png --out DIR
                         [--length 1.0] [--width 1.0] [--lib NN.Lib] [--compare]
      Measures the reference weapon's line (pommel -> tip) in all 832 frames, then
      rotates/foreshortens the picture onto each line with the pommel pinned.
      Writes DIR/<NN>.Lib (--lib), per-action preview sheets and report.txt.
      --compare also writes original | new side-by-side sheets and accuracy numbers.

Input picture: weapon pointing straight UP (tip at the top, pommel at the bottom), on a
transparent background or a flat colour (taken from the top-left pixel). Any size.
--length / --width scale the weapon relative to the reference (1.0 = same length).
--mirror lists the directions that show the weapon's other side (default DownLeft,Left,UpLeft;
"none" to disable). It only matters for lopsided weapons (axes, hooks, single-edged blades).
"""
import sys, os, math, argparse
sys.dont_write_bytecode = True  # no __pycache__ next to the tool in the repo
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mirlib import *
from pose import measure, orient_all, endpoints, prof_dist

SS = 4          # supersamples per axis per output pixel
ALPHA_CUT = 0.4  # coverage needed for an output pixel to be opaque (the client uses hard alpha)


def analyse_ref(lib):
    """Per gender: measured frames, master index, pommel->tip profile, orientations."""
    res = {}
    for g, goff in GENDERS:
        ms = {i: measure(lib[i]) for _, _, _, i in frame_indices(goff) if lib[i]}
        ms = {i: m for i, m in ms.items() if m}
        mi = max(ms, key=lambda i: ms[i]["length"])
        m = ms[mi]
        # the guard (light grey band) sits nearer the pommel: pick the profile direction
        # whose brightest bin is in the pommel half
        lum = [sum(c) if c else 0 for c in m["prof"]]
        bright = lum.index(max(lum))
        prof = m["prof"][::-1] if bright > len(lum) / 2 else m["prof"]  # now pommel -> tip
        poses = orient_all(ms, prof)
        fix_continuity(poses, goff)
        res[g] = dict(goff=goff, ms=ms, master=mi, Lm=m["length"], poses=poses)
    return res


def fix_continuity(poses, goff):
    """Low-confidence frames (short, foreshortened lines): take the orientation whose pommel is
    nearest the pommel of the most confident frame in the same action+direction."""
    for aname, start, n in ACTIONS:
        for d in range(8):
            idxs = [goff + start + d * n + k for k in range(n) if goff + start + d * n + k in poses]
            if not idxs: continue
            best = max(idxs, key=lambda i: poses[i][2])
            bp = poses[best][0]
            for i in idxs:
                p, t, c = poses[i]
                if c < 0.35 and math.dist(t, bp) < math.dist(p, bp):
                    poses[i] = (t, p, c)


def load_input(path, glow=0):
    im = read_png(path)
    if all(im.rgba[k] == 255 for k in range(3, len(im.rgba), 4)):
        # no transparency: key out the top-left colour, but only where it is connected to the picture's edge, so dark
        # pixels inside the weapon (a near-black handle on a black background) stay
        flood_key(im, im.rgba[0:3], KEY_TOL)
        if glow:
            # a glow's dark halo also sits in pockets the edge fill cannot reach (inside loops of lightning)
            for k in range(0, len(im.rgba), 4):
                if is_glow(im.rgba[k], im.rgba[k + 1], im.rgba[k + 2], glow): im.rgba[k + 3] = 0
    keep_largest(im)
    ys = [j for j in range(im.h) if any(im.rgba[(j * im.w + i) * 4 + 3] for i in range(im.w))]
    if not ys: raise SystemExit("input picture has no weapon pixels")
    y0, y1 = ys[0], ys[-1] + 1
    # the picture is upright: the axis is the handle's centre line, taken from the bottom 30%
    # (an axe head or a hook on one side must not pull the axis sideways)
    hx = sorted(i + 0.5 for j in range(y1 - max(2, (y1 - y0) // 5), y1)
                for i in range(im.w) if im.rgba[(j * im.w + i) * 4 + 3])
    ax = hx[len(hx) // 2]
    return im, (ax, float(y1)), (ax, float(y0))


KEY_TOL = 14  # max per-channel difference from the background colour that still counts as background


def is_glow(r, g, b, glow):
    """A dark, strongly coloured pixel (a glow's halo on a black background): brightest channel under `glow` and
    saturation over 0.5. Dark greys (handles, metal shadows) are not glow."""
    hi = max(r, g, b)
    return 0 < hi < glow and (hi - min(r, g, b)) / hi > 0.5


def flood_key(im, key, tol):
    """Clears the background colour where it is connected to the picture's edge."""
    near = lambda p: max(abs(im.rgba[p * 4 + c] - key[c]) for c in range(3)) <= tol
    seen = bytearray(im.w * im.h)
    stack = [p for p in list(range(im.w)) + list(range((im.h - 1) * im.w, im.h * im.w))
             + list(range(0, im.w * im.h, im.w)) + list(range(im.w - 1, im.w * im.h, im.w)) if near(p)]
    for p in stack: seen[p] = 1
    while stack:
        p = stack.pop(); im.rgba[p * 4 + 3] = 0; x, y = p % im.w, p // im.w
        for X, Y in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if 0 <= X < im.w and 0 <= Y < im.h:
                q = Y * im.w + X
                if not seen[q] and near(q): seen[q] = 1; stack.append(q)


def keep_largest(im):
    """Drops everything except the biggest 8-connected opaque shape (background specks)."""
    seen = bytearray(im.w * im.h); best = []
    for start in range(im.w * im.h):
        if seen[start] or not im.rgba[start * 4 + 3]: continue
        comp, stack = [], [start]; seen[start] = 1
        while stack:
            p = stack.pop(); comp.append(p); x, y = p % im.w, p // im.w
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    X, Y = x + dx, y + dy
                    if 0 <= X < im.w and 0 <= Y < im.h:
                        q = Y * im.w + X
                        if not seen[q] and im.rgba[q * 4 + 3]: seen[q] = 1; stack.append(q)
        if len(comp) > len(best): best = comp
    keep = bytearray(im.w * im.h)
    for p in best: keep[p] = 1
    for p in range(im.w * im.h):
        if not keep[p]: im.rgba[p * 4 + 3] = 0


def box_shrink(im, f):
    """Area-average downscale by integer factor f (alpha-weighted colour)."""
    w, h = im.w // f, im.h // f
    out = Img(w, h)
    for j in range(h):
        for i in range(w):
            a = r = g = b = 0
            for y in range(j * f, j * f + f):
                o = (y * im.w + i * f) * 4
                for k in range(f):
                    q = o + k * 4; A = im.rgba[q + 3]
                    if A: a += A; r += im.rgba[q] * A; g += im.rgba[q + 1] * A; b += im.rgba[q + 2] * A
            if a:
                q = (j * w + i) * 4
                out.rgba[q:q + 4] = bytes((r // a, g // a, b // a, min(255, a // (f * f))))
    return out


def direction_of(idx):
    for _, start, n in ACTIONS:
        local = idx % 416 - start
        if 0 <= local < 8 * n: return local // n
    raise ValueError(idx)


def axes(p, t):
    L = math.dist(p, t); ux, uy = (t[0] - p[0]) / L, (t[1] - p[1]) / L
    return L, (ux, uy), (-uy, ux)


def warp(src, sp, st, dp, dt, along, across):
    """Maps src (pommel sp, tip st) so that sp -> dp and the axis runs to dt.
    along = output length / src length on the axis, across = scale perpendicular to it.
    Returns an Img positioned in destination coordinates (x/y = top-left)."""
    Ls, us, ns = axes(sp, st)
    Ld, ud, nd = axes(dp, dt)
    # destination bbox from the 4 corners of the source picture
    xs, ys = [], []
    for cx, cy in ((0, 0), (src.w, 0), (0, src.h), (src.w, src.h)):
        s = (cx - sp[0]) * us[0] + (cy - sp[1]) * us[1]
        r = (cx - sp[0]) * ns[0] + (cy - sp[1]) * ns[1]
        xs.append(dp[0] + s * along * ud[0] + r * across * nd[0])
        ys.append(dp[1] + s * along * ud[1] + r * across * nd[1])
    x0, y0 = math.floor(min(xs)), math.floor(min(ys))
    w, h = math.ceil(max(xs)) - x0, math.ceil(max(ys)) - y0
    out = Img(w, h, x0, y0)
    inv = 1.0 / (SS * SS)
    for j in range(h):
        for i in range(w):
            cov, r_, g_, b_ = 0, 0, 0, 0
            for sj in range(SS):
                for si in range(SS):
                    X = x0 + i + (si + 0.5) / SS - dp[0]; Y = y0 + j + (sj + 0.5) / SS - dp[1]
                    s = (X * ud[0] + Y * ud[1]) / along; r = (X * nd[0] + Y * nd[1]) / across
                    qx = int(math.floor(sp[0] + s * us[0] + r * ns[0]))
                    qy = int(math.floor(sp[1] + s * us[1] + r * ns[1]))
                    if 0 <= qx < src.w and 0 <= qy < src.h:
                        o = (qy * src.w + qx) * 4
                        a = src.rgba[o + 3]
                        if a:
                            af = a / 255; cov += af
                            r_ += src.rgba[o] * af; g_ += src.rgba[o + 1] * af; b_ += src.rgba[o + 2] * af
            if cov * inv >= ALPHA_CUT:
                o = (j * w + i) * 4
                out.rgba[o:o + 4] = bytes((round(r_ / cov), round(g_ / cov), round(b_ / cov), 255))
    return trim(out)


def trim(im):
    xs = [i for j in range(im.h) for i in range(im.w) if im.rgba[(j * im.w + i) * 4 + 3]]
    ys = [j for j in range(im.h) for i in range(im.w) if im.rgba[(j * im.w + i) * 4 + 3]]
    if not xs: return None
    x0, x1, y0, y1 = min(xs), max(xs) + 1, min(ys), max(ys) + 1
    out = Img(x1 - x0, y1 - y0, im.x + x0, im.y + y0)
    for j in range(y0, y1):
        out.rgba[(j - y0) * out.w * 4:(j - y0 + 1) * out.w * 4] = im.rgba[(j * im.w + x0) * 4:(j * im.w + x1) * 4]
    return out


def to_lib_frame(local, ref):
    """local is in the reference frame's pixel space; shift by the reference draw offset."""
    if local is None: return None
    local.x += ref.x; local.y += ref.y
    return local


def sheets(frames, outdir, prefix, pair=None, zoom=1):
    """Gridded per-action sheets on a common anchor. pair = second frame list drawn to the right."""
    for g, goff in GENDERS:
        imgs = [frames[i] for _, _, _, i in frame_indices(goff) if frames[i]]
        if pair: imgs += [pair[i] for _, _, _, i in frame_indices(goff) if pair[i]]
        minx = min(m.x for m in imgs); miny = min(m.y for m in imgs)
        cw = max(m.x + m.w for m in imgs) - minx; ch = max(m.y + m.h for m in imgs) - miny
        parts = 2 if pair else 1
        for aname, start, n in ACTIONS:
            CW, CH = (cw * parts + parts - 1) * zoom, ch * zoom
            W, H = n * (CW + 1) + 1, 8 * (CH + 1) + 1
            buf = bytearray(W * H * 4)
            for gx in range(0, W, CW + 1):
                for yy in range(H): buf[(yy * W + gx) * 4:(yy * W + gx) * 4 + 4] = b"\xff\x00\xff\xff"
            for gy in range(0, H, CH + 1): buf[gy * W * 4:(gy + 1) * W * 4] = b"\xff\x00\xff\xff" * W
            for d in range(8):
                for k in range(n):
                    i = goff + start + d * n + k
                    for part, src in enumerate([frames, pair][:parts]):
                        m = src[i]
                        if pair and part == 0:   # thin divider between original and new
                            for yy in range(CH):
                                o = ((1 + d * (CH + 1) + yy) * W + 1 + k * (CW + 1) + cw * zoom) * 4
                                buf[o:o + 4] = b"\x60\x60\x60\xff"
                        if not m: continue
                        bx = 1 + k * (CW + 1) + part * (cw + 1) * zoom + (m.x - minx) * zoom
                        by = 1 + d * (CH + 1) + (m.y - miny) * zoom
                        for yy in range(m.h * zoom):
                            for xx in range(m.w * zoom):
                                o = ((yy // zoom) * m.w + xx // zoom) * 4
                                if m.rgba[o + 3]:
                                    q = ((by + yy) * W + bx + xx) * 4
                                    buf[q:q + 4] = m.rgba[o:o + 4]
            write_png(os.path.join(outdir, "%s_%s_%s.png" % (prefix, g, aname)), W, H, buf)


def cmd_make_input(a):
    lib = read_lib(a.ref)
    ref = analyse_ref(lib)["male"]
    mi = ref["master"]; src = lib[mi]
    p, t, _ = ref["poses"][mi]
    Z = a.zoom; L = math.dist(p, t)
    W = int(12 * Z); H = int(math.ceil(L * Z)) + 2 * Z
    # upright canvas: pommel at bottom centre, tip straight up
    dp, dt = (W / 2, H - Z), (W / 2, H - Z - L * Z)
    up = warp(src, p, t, dp, dt, Z, Z)
    canvas = Img(W, H)
    for j in range(up.h):
        for i in range(up.w):
            o = (j * up.w + i) * 4
            if up.rgba[o + 3]:
                X, Y = up.x + i, up.y + j
                if 0 <= X < W and 0 <= Y < H: canvas.rgba[(Y * W + X) * 4:(Y * W + X) * 4 + 4] = up.rgba[o:o + 4]
    write_png(a.out, W, H, canvas.rgba)
    print("upright input written: %s (%dx%d) from frame %d" % (a.out, W, H, mi))


def cmd_render(a):
    lib = read_lib(a.ref)
    ref = analyse_ref(lib)
    src, sp, st = load_input(a.input, a.glow)
    Ls = math.dist(sp, st)
    # big pictures: average down first to ~6 source px per output px of the longest frame
    target = max(r["Lm"] for r in ref.values()) * a.length * 6
    f = int(Ls // target)
    if f >= 2:
        src = box_shrink(src, f)
        sp, st = (sp[0] / f, sp[1] / f), (st[0] / f, st[1] / f); Ls = math.dist(sp, st)
    os.makedirs(a.out, exist_ok=True)
    mirror = set() if a.mirror.lower() == "none" else set(x.strip() for x in a.mirror.split(","))
    bad = mirror - set(DIRS)
    if bad: raise SystemExit("unknown --mirror direction(s): %s (use %s or none)" % (", ".join(bad), ", ".join(DIRS)))
    out = [None] * len(lib)
    rep = []
    for g, r in ref.items():
        Lm = r["Lm"]
        across = Lm * a.length / Ls * a.width
        for idx, (p, t, conf) in r["poses"].items():
            Lf = math.dist(p, t)
            along = Lf * a.length / Ls
            # left-facing directions see the weapon from its other side: flip it across its axis
            side = -1 if DIRS[direction_of(idx)] in mirror else 1
            # extend the destination tip in proportion so a longer weapon grows past the old tip
            loc = warp(src, sp, st, p, (p[0] + (t[0] - p[0]) * a.length, p[1] + (t[1] - p[1]) * a.length),
                       along, across * side)
            out[idx] = to_lib_frame(loc, lib[idx])
        lows = sum(1 for v in r["poses"].values() if v[2] < 0.35)
        rep.append("%s: %d frames, master frame %d (length %.1f px), %d low-confidence orientations"
                   % (g, len(r["poses"]), r["master"], Lm, lows))
    if a.lib: write_lib(os.path.join(a.out, a.lib), out)
    sheets(out, a.out, "sheet")
    if a.compare:
        cmpdir = os.path.join(a.out, "compare"); os.makedirs(cmpdir, exist_ok=True)
        sheets(lib, cmpdir, "compare", pair=out, zoom=2)
        rep += compare(lib, out, ref)
    open(os.path.join(a.out, "report.txt"), "w").write("\n".join(rep) + "\n")
    print("\n".join(rep))


def compare(orig, new, ref):
    lines = ["", "Accuracy vs the original frames (male + female):"]
    errs, ious, angs, flips = [], [], [], 0
    worst = []
    for g, r in ref.items():
        for idx, (p, t, conf) in r["poses"].items():
            o, n = orig[idx], new[idx]
            if not n: worst.append((99, idx, "empty")); continue
            mo, mn = set(), set()
            for im, s in ((o, mo), (n, mn)):
                for j in range(im.h):
                    for i in range(im.w):
                        if im.rgba[(j * im.w + i) * 4 + 3]: s.add((im.x + i, im.y + j))
            iou = len(mo & mn) / len(mo | mn); ious.append(iou)
            m2 = measure(n)
            a2, b2 = endpoints(m2, False)
            P = (p[0] + o.x, p[1] + o.y); T = (t[0] + o.x, t[1] + o.y)
            A = (a2[0] + n.x, a2[1] + n.y); B = (b2[0] + n.x, b2[1] + n.y)
            e = min(max(math.dist(P, A), math.dist(T, B)), max(math.dist(P, B), math.dist(T, A)))
            errs.append(e)
            # reversed? compare where the guard sits, with both axes pointing the same way
            mo = measure(o)
            g2 = m2["guard"] if (mo["ux"] * m2["ux"] + mo["uy"] * m2["uy"]) > 0 else -m2["guard"]
            if mo["length"] > 12 and abs(mo["guard"]) > 0.15 and g2 * mo["guard"] < 0:
                flips += 1; worst.append((50, idx, "REVERSED"))
            worst.append((e, idx, "iou %.2f" % iou))
    errs.sort(); ious.sort()
    pct = lambda v, q: v[min(len(v) - 1, int(q * len(v)))]
    lines.append("  endpoint error px  median %.2f  p90 %.2f  p99 %.2f  max %.2f" % (pct(errs, .5), pct(errs, .9), pct(errs, .99), errs[-1]))
    lines.append("  pixel overlap IoU  median %.2f  p10 %.2f  min %.2f" % (pct(ious, .5), pct(ious, .1), ious[0]))
    lines.append("  reversed swords (guard on the wrong end): %d" % flips)
    lines.append("  worst frames: " + ", ".join("%d (%.1fpx, %s)" % (i, e, s) for e, i, s in sorted(worst, reverse=True)[:8]))
    return lines


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    m = sub.add_parser("make-input"); m.add_argument("--ref", required=True); m.add_argument("--out", required=True)
    m.add_argument("--zoom", type=int, default=8)
    r = sub.add_parser("render"); r.add_argument("--ref", required=True); r.add_argument("--input", required=True)
    r.add_argument("--out", required=True); r.add_argument("--length", type=float, default=1.0)
    r.add_argument("--width", type=float, default=1.0); r.add_argument("--lib")
    r.add_argument("--compare", action="store_true")
    r.add_argument("--glow", type=int, default=0,
                   help="also drop a glow's dark coloured halo: brightest channel below this (try 90; 0 = off)")
    r.add_argument("--mirror", default="DownLeft,Left,UpLeft",
                   help="directions drawn flipped across the weapon's axis (comma list, or none)")
    a = ap.parse_args()
    cmd_make_input(a) if a.cmd == "make-input" else cmd_render(a)
