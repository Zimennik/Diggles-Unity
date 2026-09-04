"""Compare the serialized fog layer between saves: per-player byte histogram,
fogBegin rows, fade-list sizes, and a coarse density map for player 0."""
import gzip, struct, sys, collections
from pathlib import Path

def load(path):
    with gzip.open(path, "rb") as s:
        return s.read()

def audit(path):
    d = load(path)
    pos = 0
    ver = struct.unpack_from("<i", d, pos)[0]; pos += 4
    assert ver == 0xA5, hex(ver)
    slen = struct.unpack_from("<i", d, pos)[0]; pos += 4 + slen
    pos += 8
    assert struct.unpack_from("<i", d, pos)[0] == 1; pos += 4
    pos += 205
    assert struct.unpack_from("<i", d, pos)[0] == 2; pos += 4
    assert struct.unpack_from("<i", d, pos)[0] == 3; pos += 4
    w, h = struct.unpack_from("<ii", d, pos); pos += 8
    pos += 16
    tiles = pos
    pos += w * h * 53
    fog_begin = struct.unpack_from("<8i", d, pos)
    pos += 40
    primary = struct.unpack_from("<i", d, pos)[0]; pos += 4 + primary * 37
    secondary = struct.unpack_from("<i", d, pos)[0]; pos += 4 + secondary * 41
    pos += 8
    for _ in range(4):
        c = struct.unpack_from("<i", d, pos)[0]; pos += 4 + c * 8
    pos += 1
    fade_counts = []
    fade0 = []
    for p in range(8):
        c = struct.unpack_from("<i", d, pos)[0]; pos += 4
        if p == 0:
            for i in range(c):
                fade0.append(struct.unpack_from("<ii", d, pos + i * 8))
        fade_counts.append(c)
        pos += c * 8
    assert struct.unpack_from("<i", d, pos)[0] == 4

    hist = [collections.Counter() for _ in range(8)]
    for t in range(w * h):
        off = tiles + t * 53 + 45
        for p in range(8):
            hist[p][d[off + p]] += 1

    print(f"== {Path(path).name}  {w}x{h}")
    print(f"  fogBegin={list(fog_begin)}  fadeCounts={fade_counts}")
    for p in range(8):
        if len(hist[p]) == 1 and 0 in hist[p]:
            continue
        top = sorted(hist[p].items(), key=lambda kv: -kv[1])[:8]
        print(f"  p{p}: " + "  ".join(f"{v}:{n}" for v, n in top))
    # density map for player 0: rows are tileY bands, cols tileX bands
    bx, by = max(1, w // 64), max(1, h // 32)
    print("  p0 map (.=0 black, -=1 grey, o=2..254 fading, #=255 visible):")
    for yb in range(0, h, by * 4):
        row = []
        for xb in range(0, w, bx * 2):
            best = 0
            for y in range(yb, min(yb + by * 4, h)):
                for x in range(xb, min(xb + bx * 2, w)):
                    v = d[tiles + (y * w + x) * 53 + 45]
                    if v > best:
                        best = v
            row.append("#" if best == 255 else "o" if best >= 2 else "-" if best == 1 else ".")
        line = "".join(row)
        if set(line) != {"."}:
            print("   ", line)
    if fade0[:12]:
        print(f"  p0 fade head: {fade0[:12]}")

for f in sys.argv[1:]:
    audit(f)
