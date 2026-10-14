"""Stdlib-only helpers: Crystal .Lib (v2/v3) read/write, PNG read/write, frame layout."""
import struct, zlib, gzip, math

# Client/MirObjects/Frames.cs FrameSet.Player - the actions inside one 416-frame gender block
ACTIONS = [("Standing", 0, 4), ("Walking", 32, 6), ("Running", 80, 6), ("Stance", 128, 1),
           ("Attack1", 136, 6), ("Attack2", 184, 6), ("Attack3", 232, 8), ("Spell", 296, 6),
           ("Harvest", 344, 2), ("Struck", 360, 3), ("Die", 384, 4)]
DIRS = ["Up", "UpRight", "Right", "DownRight", "Down", "DownLeft", "Left", "UpLeft"]
GENDERS = [("male", 0), ("female", 416)]


def frame_indices(goff):
    """Yields (action, dir, k, lib index) for every frame of one gender block."""
    for aname, start, n in ACTIONS:
        for d in range(8):
            for k in range(n):
                yield aname, d, k, goff + start + d * n + k


class Img:
    """RGBA image, rows of floats-free bytes. x/y = library draw offset."""
    def __init__(self, w, h, x=0, y=0, rgba=None):
        self.w, self.h, self.x, self.y = w, h, x, y
        self.rgba = bytearray(rgba) if rgba is not None else bytearray(w * h * 4)

    def px(self, i, j):
        o = (j * self.w + i) * 4
        return self.rgba[o:o + 4]


def read_lib(path):
    f = open(path, "rb")
    ver, count = struct.unpack("<ii", f.read(8))
    if ver >= 3: f.read(4)
    index = struct.unpack("<%di" % count, f.read(4 * count))
    out = []
    for i in range(count):
        if index[i] == 0:
            out.append(None); continue
        f.seek(index[i])
        w, h, x, y, sx, sy = struct.unpack("<6h", f.read(12)); f.read(1)
        ln = struct.unpack("<i", f.read(4))[0]
        if w <= 0 or h <= 0 or ln <= 0:
            out.append(None); continue
        bgra = gzip.decompress(f.read(ln))
        rgba = bytearray(bgra)
        rgba[0::4], rgba[2::4] = bgra[2::4], bgra[0::4]
        out.append(Img(w, h, x, y, rgba))
    return out


def write_lib(path, frames):
    """Writes a v2 .Lib (no frame table), same layout the client reads; None = empty slot."""
    count = len(frames)
    head = 8 + 4 * count
    blobs, index, pos = [], [], head
    for im in frames:
        if im is None or im.w == 0:
            index.append(0); continue
        bgra = bytearray(im.rgba)
        bgra[0::4], bgra[2::4] = im.rgba[2::4], im.rgba[0::4]
        data = gzip.compress(bytes(bgra), 9)
        blob = struct.pack("<6hBi", im.w, im.h, im.x, im.y, 0, 0, 0, len(data)) + data
        index.append(pos); blobs.append(blob); pos += len(blob)
    with open(path, "wb") as f:
        f.write(struct.pack("<ii", 2, count)); f.write(struct.pack("<%di" % count, *index))
        for b in blobs: f.write(b)


def write_png(path, w, h, rgba):
    raw = b"".join(b"\0" + bytes(rgba[r * w * 4:(r + 1) * w * 4]) for r in range(h))
    ch = lambda t, d: struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    open(path, "wb").write(b"\x89PNG\r\n\x1a\n" + ch(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
                           + ch(b"IDAT", zlib.compress(raw, 9)) + ch(b"IEND", b""))


def read_png(path):
    """8-bit non-interlaced PNG (gray, RGB, palette, gray+alpha, RGBA) -> Img (RGBA)."""
    d = open(path, "rb").read()
    assert d[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG"
    p, idat, plte, trns = 8, b"", None, None
    while p < len(d):
        ln = struct.unpack(">I", d[p:p + 4])[0]; t = d[p + 4:p + 8]; c = d[p + 8:p + 8 + ln]; p += 12 + ln
        if t == b"IHDR": w, h, bd, ct, _, _, il = struct.unpack(">IIBBBBB", c)
        elif t == b"PLTE": plte = c
        elif t == b"tRNS": trns = c
        elif t == b"IDAT": idat += c
    if bd != 8 or il: raise SystemExit("PNG must be 8-bit and non-interlaced (re-save it from any editor)")
    bpp = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ct]
    raw = zlib.decompress(idat); stride = w * bpp
    out, prev, q = bytearray(), bytearray(stride), 0
    rows = []
    for _ in range(h):
        ft = raw[q]; line = bytearray(raw[q + 1:q + 1 + stride]); q += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0; b = prev[i]; c = prev[i - bpp] if i >= bpp else 0
            if ft == 1: line[i] = (line[i] + a) & 255
            elif ft == 2: line[i] = (line[i] + b) & 255
            elif ft == 3: line[i] = (line[i] + (a + b) // 2) & 255
            elif ft == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line); prev = line
    for line in rows:
        for i in range(w):
            if ct == 6: out += line[i * 4:i * 4 + 4]
            elif ct == 2: out += line[i * 3:i * 3 + 3] + b"\xff"
            elif ct == 0: out += bytes((line[i],) * 3) + b"\xff"
            elif ct == 4: out += bytes((line[i * 2],) * 3) + bytes((line[i * 2 + 1],))
            else:
                k = line[i]; out += plte[k * 3:k * 3 + 3] + bytes((trns[k] if trns and k < len(trns) else 255,))
    return Img(w, h, 0, 0, out)
