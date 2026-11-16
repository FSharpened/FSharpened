#!/usr/bin/env python3
"""Render the FSharpened mark to PNG icons with the standard library only (no Pillow, no Cairo, any OS).

The geometry is the one in branding/mark.svg. Output goes to branding/icons/ and, for the hub admin
console (which has no build step), is copied into worker/admin/. `--check` fails if either is stale.

    python tools/site/icons.py            # regenerate
    python tools/site/icons.py --check    # CI: verify the committed icons are current
"""
import math, pathlib, struct, sys, zlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "branding" / "icons"
HUB = ROOT / "worker" / "admin"

TILE_A, TILE_B = (0x43, 0x38, 0xCA), (0x1E, 0x1B, 0x4B)  # indigo -> deep indigo (branding/README.md)
WHITE, CYAN = (255, 255, 255), (0x22, 0xD3, 0xEE)
F_POLY = [(140, 104), (408, 104), (346, 176), (228, 176), (228, 240), (336, 240), (296, 312), (228, 312), (228, 408), (140, 408)]
DIAMOND = [(372, 296), (424, 348), (372, 400), (320, 348)]
GLYPH_CENTRE = (282.0, 256.0)  # centre of the F + diamond bounding box in the 512 design grid
SS = 4  # sub-rows per pixel (horizontal coverage is exact)


def _poly_spans(poly, y):
    xs = []
    n = len(poly)
    for i in range(n):
        (x0, y0), (x1, y1) = poly[i], poly[(i + 1) % n]
        if (y0 <= y < y1) or (y1 <= y < y0):
            xs.append(x0 + (y - y0) * (x1 - x0) / (y1 - y0))
    xs.sort()
    return [(xs[i], xs[i + 1]) for i in range(0, len(xs) - 1, 2)]


def _tile_spans(size, radius, y, rounded):
    if not rounded:
        return [(0.0, float(size))]
    if y < radius:
        dx = radius - math.sqrt(max(radius * radius - (radius - y) ** 2, 0.0))
    elif y > size - radius:
        dx = radius - math.sqrt(max(radius * radius - (y - (size - radius)) ** 2, 0.0))
    else:
        dx = 0.0
    return [(dx, size - dx)] if size - dx > dx else []


def _add_spans(diff, spans, weight):
    """Accumulate exact horizontal coverage of [a, b) spans into a difference array."""
    n = len(diff) - 1
    for a, b in spans:
        a, b = max(a, 0.0), min(b, float(n))
        if b <= a:
            continue
        i0, i1 = int(a), int(b)
        if i0 == i1:
            diff[i0] += (b - a) * weight
            diff[i0 + 1] -= (b - a) * weight
            continue
        first = (i0 + 1 - a) * weight
        diff[i0] += first
        diff[i0 + 1] -= first
        if i1 > i0 + 1:
            diff[i0 + 1] += weight
            diff[i1] -= weight
        last = (b - i1) * weight
        if last > 0:
            diff[i1] += last
            diff[i1 + 1] -= last


def _coverage(size, spans_at):
    rows = []
    for py in range(size):
        diff = [0.0] * (size + 2)
        for s in range(SS):
            _add_spans(diff, spans_at(py + (s + 0.5) / SS), 1.0 / SS)
        acc, row = 0.0, []
        for px in range(size):
            acc += diff[px]
            row.append(min(max(acc, 0.0), 1.0))
        rows.append(row)
    return rows


def render(size, *, rounded=True, glyph_scale=1.0, recentre=False, opaque=False):
    """Return RGBA (or RGB when opaque) pixel rows for the mark at `size` px.

    By default the glyph keeps the exact placement it has in branding/mark.svg. Full-bleed icons (maskable, Apple)
    recentre it and shrink it so it survives the platform's own cropping."""
    k = size / 512.0
    radius = 112 * k

    def place(poly):
        if not recentre:
            return [(x * k, y * k) for x, y in poly]
        return [(size / 2 + (x - GLYPH_CENTRE[0]) * glyph_scale * k, size / 2 + (y - GLYPH_CENTRE[1]) * glyph_scale * k) for x, y in poly]

    f_poly, diamond = place(F_POLY), place(DIAMOND)
    cov_tile = _coverage(size, lambda y: _tile_spans(size, radius, y, rounded))
    cov_f = _coverage(size, lambda y: _poly_spans(f_poly, y))
    cov_d = _coverage(size, lambda y: _poly_spans(diamond, y))
    rows = []
    for py in range(size):
        out = bytearray()
        for px in range(size):
            t = min(max((px + 0.5 + py + 0.5) / (2.0 * size), 0.0), 1.0)
            tile = tuple(TILE_A[i] + (TILE_B[i] - TILE_A[i]) * t for i in range(3))
            a = cov_tile[py][px]
            premult = [tile[i] * a for i in range(3)]
            for cov, col in ((cov_f[py][px], WHITE), (cov_d[py][px], CYAN)):
                if cov > 0:
                    premult = [col[i] * cov + premult[i] * (1 - cov) for i in range(3)]
                    a = cov + a * (1 - cov)
            if a <= 0:
                out += bytes((0, 0, 0)) if opaque else bytes((0, 0, 0, 0))
                continue
            rgb = [min(255, max(0, int(round(premult[i] / a)))) for i in range(3)]
            out += bytes(rgb) if opaque else bytes(rgb + [int(round(a * 255))])
        rows.append(bytes(out))
    return rows


def png_bytes(rows, size, opaque):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + r for r in rows)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 2 if opaque else 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def ico_bytes(images):
    """images: list of (size, png_bytes). PNG-in-ICO is supported by every current browser and OS."""
    head = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset + len(blobs))
        blobs += data
    return head + entries + blobs


# name -> (size, kwargs). "any" icons keep the rounded tile; maskable and apple icons are full-bleed.
SPECS = {
    "icon-192.png": (192, {}),
    "icon-512.png": (512, {}),
    "icon-maskable-512.png": (512, {"rounded": False, "glyph_scale": 0.82, "recentre": True, "opaque": True}),  # glyph inside the 80% safe zone
    "icon-180.png": (180, {"rounded": False, "glyph_scale": 0.9, "recentre": True, "opaque": True}),  # apple-touch-icon; iOS rounds it
    "favicon-32.png": (32, {}),
    "favicon-48.png": (48, {}),
}
HUB_FILES = ["icon-192.png", "icon-512.png", "icon-maskable-512.png", "icon-180.png", "favicon-32.png", "favicon.ico"]


def build():
    files = {}
    for name, (size, kw) in SPECS.items():
        files[name] = png_bytes(render(size, **kw), size, kw.get("opaque", False))
    files["favicon.ico"] = ico_bytes([(32, files["favicon-32.png"]), (48, files["favicon-48.png"])])
    return files


def png_canon(data):
    """The header and the *decompressed* pixels: two PNGs that differ only in how zlib packed them (a different zlib
    build on CI) are the same picture, so --check compares this instead of raw bytes."""
    pos, ihdr, idat = 8, b"", b""
    while pos < len(data):
        (n,) = struct.unpack(">I", data[pos:pos + 4])
        tag, body = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + n]
        if tag == b"IHDR":
            ihdr = body
        elif tag == b"IDAT":
            idat += body
        pos += 12 + n
    return ihdr, zlib.decompress(idat)


def canon(name, data):
    if name.endswith(".ico"):
        (count,) = struct.unpack("<H", data[4:6])
        out = []
        for i in range(count):
            w, h, _c, _r, planes, bpp, size, off = struct.unpack("<BBBBHHII", data[6 + 16 * i:22 + 16 * i])
            out.append((w, h, planes, bpp, png_canon(data[off:off + size])))
        return out
    return png_canon(data)


def same(name, a, b):
    try:
        return canon(name, a) == canon(name, b)
    except Exception:  # noqa: BLE001 - a corrupt committed file is simply stale
        return False


def main(argv):
    files = build()
    stale = []
    for dest_dir, names in ((OUT, list(files)), (HUB, HUB_FILES)):
        for name in names:
            path = dest_dir / name
            if "--check" in argv:
                if not path.exists() or not same(name, path.read_bytes(), files[name]):
                    stale.append(str(path.relative_to(ROOT)))
            else:
                dest_dir.mkdir(parents=True, exist_ok=True)
                path.write_bytes(files[name])
    if "--check" in argv:
        if stale:
            print("icons are stale; run python tools/site/icons.py:\n  " + "\n  ".join(stale))
            return 1
        print(f"icons: OK ({len(files)} files)")
        return 0
    print(f"icons: wrote {len(files)} files to {OUT.relative_to(ROOT)} and {len(HUB_FILES)} to {HUB.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
