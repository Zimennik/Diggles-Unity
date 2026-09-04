"""Minimal MINIDUMP triage for Diggles.exe crashes: exception code/address, the faulting
module RVA, and the nearest decompiled function. No WinDbg required.
Usage: minidump_triage.py crash.dmp [decompile_dir]"""
import struct, sys, os, bisect

def read_minidump(path):
    d = open(path, "rb").read()
    assert d[:4] == b"MDMP", "not a minidump"
    stream_count, stream_dir_rva = struct.unpack_from("<II", d, 8)
    streams = {}
    for i in range(stream_count):
        stype, size, rva = struct.unpack_from("<III", d, stream_dir_rva + i * 12)
        streams.setdefault(stype, []).append((rva, size))
    return d, streams

def modules(d, streams):
    out = []
    for rva, _ in streams.get(4, []):  # ModuleListStream
        count = struct.unpack_from("<I", d, rva)[0]
        for i in range(count):
            base = rva + 4 + i * 108
            image_base, image_size = struct.unpack_from("<QI", d, base)
            name_rva = struct.unpack_from("<I", d, base + 0x14)[0]
            name_len = struct.unpack_from("<I", d, name_rva)[0]
            name = d[name_rva + 4:name_rva + 4 + name_len].decode("utf-16-le", "replace")
            out.append((name, image_base, image_size))
    return out

def exception(d, streams):
    for rva, _ in streams.get(6, []):  # ExceptionStream
        tid = struct.unpack_from("<I", d, rva)[0]
        code, flags, inner, addr = struct.unpack_from("<IIQQ", d, rva + 8)
        nparams = struct.unpack_from("<I", d, rva + 8 + 24)[0]
        params = struct.unpack_from("<10Q", d, rva + 8 + 32)[:nparams]
        return tid, code, addr, params
    return None

def main():
    dump_path = sys.argv[1]
    dec_dir = sys.argv[2] if len(sys.argv) > 2 else ""
    d, streams = read_minidump(dump_path)
    exc = exception(d, streams)
    mods = modules(d, streams)
    if not exc:
        print("no exception stream"); return
    tid, code, addr, params = exc
    print(f"exception code=0x{code:08X} addr=0x{addr:X} tid={tid} params={[hex(p) for p in params]}")
    hit = None
    for name, base, size in mods:
        if base <= addr < base + size:
            hit = (name, base, size)
        if "diggles" in os.path.basename(name).lower():
            print(f"module Diggles base=0x{base:X} size=0x{size:X} ({name})")
    if hit:
        name, base, size = hit
        rva = addr - base
        print(f"faulting module: {os.path.basename(name)} rva=0x{rva:X}")
        if "diggles" in os.path.basename(name).lower():
            va = 0x400000 + rva
            funcs = sorted(
                int(f.split("_")[0], 16)
                for f in os.listdir(dec_dir) if f.endswith(".c"))
            i = bisect.bisect_right(funcs, va) - 1
            if i >= 0:
                print(f"virtual addr=0x{va:X} → nearest function FUN_{funcs[i]:08x} "
                      f"(+0x{va - funcs[i]:X}); next FUN_{funcs[i+1]:08x}" if i + 1 < len(funcs)
                      else f"virtual addr=0x{va:X} → FUN_{funcs[i]:08x}")
    else:
        print("faulting address is outside all modules (jump through bad pointer)")

if __name__ == "__main__":
    main()
