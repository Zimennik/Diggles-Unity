"""Field-by-field diff of the 53-byte tile records between two saves.
Usage: tile_diff.py native.sav port.sav"""
import gzip, struct, sys, collections

FIELDS = [
    ("blockIndex",  0, 4),
    ("navState",    4, 1),
    ("digState",    5, 4),
    ("digOrderIds", 9, 4),
    ("floorExit",  13, 1),
    ("tmplFlags",  14, 1),
    ("cellId",     15, 2),
    ("terrState",  17, 4),
    ("fluid1C",    21, 4),
    ("fluid20",    25, 4),
    ("fluid24",    29, 4),
    ("fluid28",    33, 4),
    ("fluid2C",    37, 4),
    ("fluid30",    41, 4),
    ("fog",        45, 8),
]

def load(path):
    with gzip.open(path, "rb") as s:
        return s.read()

def tiles_block(d):
    pos = 4
    slen = struct.unpack_from("<i", d, pos)[0]; pos += 4 + slen
    pos += 8
    assert struct.unpack_from("<i", d, pos)[0] == 1; pos += 4 + 205
    assert struct.unpack_from("<i", d, pos)[0] == 2; pos += 4
    assert struct.unpack_from("<i", d, pos)[0] == 3; pos += 4
    w, h = struct.unpack_from("<ii", d, pos); pos += 8 + 16
    return pos, w, h

def val(d, base, off, size):
    if size == 1: return d[base + off]
    if size == 2: return struct.unpack_from("<H", d, base + off)[0]
    if size == 4: return struct.unpack_from("<I", d, base + off)[0]
    return struct.unpack_from("<Q", d, base + off)[0]

a = load(sys.argv[1]); b = load(sys.argv[2])
pa, wa, ha = tiles_block(a); pb, wb, hb = tiles_block(b)
assert (wa, ha) == (wb, hb), f"dims {wa}x{ha} vs {wb}x{hb}"
print(f"comparing {wa}x{ha} tiles: {sys.argv[1].split(chr(92))[-1]} vs {sys.argv[2].split(chr(92))[-1]}")

for name, off, size in FIELDS:
    ndiff = 0
    samples = []
    exdist = collections.Counter()
    for t in range(wa * ha):
        va = val(a, pa + t * 53, off, size)
        vb = val(b, pb + t * 53, off, size)
        if va != vb:
            ndiff += 1
            if len(samples) < 6:
                samples.append((t % wa, t // wa, va, vb))
            exdist[(va, vb)] += 1
    if ndiff:
        top = sorted(exdist.items(), key=lambda kv: -kv[1])[:5]
        pairs = "  ".join(f"{x[0]}->{x[1]}:{n}" for x, n in top)
        print(f"  {name:12s} diff={ndiff:7d}  top: {pairs}")
        for x, y, va, vb in samples:
            print(f"      ({x:3d},{y:3d}) native={va} port={vb}")
    else:
        print(f"  {name:12s} identical")
