"""Scan a save for all type-10 walk actions, resolve owner class, print key fields."""
from __future__ import annotations
import struct, sys, gzip
from pathlib import Path
import parse_a5_object_bodies as probe
import dump_walk


def scan(path):
    meta = probe.parse(Path(path))
    classes = {b.object_id: b.class_name for b in meta[0]}
    with gzip.open(path, "rb") as s:
        r = dump_walk.R(s.read())
    r.pos = meta[21] + 4
    oid = r.i32()
    rows = []
    while oid != -1:
        atype = r.i32()
        if atype == 10:
            w = dump_walk.read_walk(r)
            banks_pop = sum(len(b["nonzeroSlots"]) for b in w["banks"])
            rows.append((oid, classes.get(oid, "?"), w, banks_pop))
        else:
            probe.skip_action_payload(r, atype)
        oid = r.i32()
    return rows


def main():
    for path in sys.argv[1:]:
        rows = scan(path)
        gnome_walks = [(o, c, w, bp) for (o, c, w, bp) in rows if c == "Zwerg"]
        print(f"\n#### {Path(path).name}: {len(rows)} walks, {len(gnome_walks)} Zwerg walks")
        for oid, cls, w, bp in rows:
            c = w["common"]
            print(f"  id={oid} class={cls} RandomPath={w['RandomPath']} "
                  f"banksNonzeroSlots={bp} Status={c['StatusOrOpaque']} "
                  f"Arg={c['Argument']!r:.40} Target={[round(x,2) for x in w['Target']]} "
                  f"TargetDist={round(w['TargetDistance'],3)} MoveFlags={w['MoveFlags']} "
                  f"StopAt={w['StopAt']} Waving={w['Waving']} ObjBreakDist={w['ObjectBreakDistance']} "
                  f"ClimbRotY={round(w['ClimbRotation'][1],3)} DiagSqueeze={w['DiagonalSqueeze']} "
                  f"collCb={w['WalkCollisionCallback']!r:.24} useCb={w['ObjectUseCallback']!r:.20}")


if __name__ == "__main__":
    main()
