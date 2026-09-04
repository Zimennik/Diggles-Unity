"""Field-level decoder for CAct_Walk (type 10) action rows in 0xA5 saves.

Usage: python dump_walk.py FILE [owner_id ...]
If no owner ids are given, dumps every type-10 row.
"""
from __future__ import annotations
import struct, sys, gzip
import parse_a5_object_bodies as probe


def f(bits):
    return struct.unpack("<f", struct.pack("<I", bits & 0xFFFFFFFF))[0]


class R(probe.Reader):
    def i(self):
        return self.i32()
    def flt(self):
        return f(self.i32())
    def b(self):
        v = self.data[self.pos]; self.pos += 1; return v
    def ns(self):
        return self.native_string()


def read_common(r):
    arg = r.ns()
    owner = r.i()
    fin = r.ns()
    brk = r.ns()
    status = r.i()
    return dict(Argument=arg, OwnerObjectId=owner, FinishCode=fin, BreakCode=brk,
               StatusOrOpaque=status, StatusHex=f"0x{status & 0xFFFFFFFF:08X}")


def read_walk(r):
    common = read_common(r)
    banks = []
    for bnk in range(4):
        state = r.i()
        slots = [[r.i32() for _ in range(5)] for _ in range(24)]
        nonzero = [(si, sv) for si, sv in enumerate(slots) if any(x != 0 for x in sv)]
        banks.append(dict(BankStateOrAnimSet=state, nonzeroSlots=nonzero))
    move_needed = r.i()
    waving = r.b()
    dontstop = r.b()
    stopat = r.flt()
    target = [r.flt() for _ in range(3)]
    canclimb = r.b()
    useobjects = r.b()
    randompath = r.i()
    randomz = r.i()
    nonudge = r.b()
    moveflags = [r.b() for _ in range(3)]
    obreakclass = r.i()
    obreakdist = r.flt()
    targetdist = r.flt()
    climbrot = [r.flt() for _ in range(3)]
    diagsqueeze = r.b()
    randpathwall = r.flt()
    walktimercounter = r.i()
    tickcounter = r.i()
    speedtype = r.i()
    cb_timer = r.ns()
    cb_collision = r.ns()
    cb_objuse = r.ns()
    return dict(common=common, banks=banks, MoveNeededOrApproach=move_needed,
                Waving=waving, DontStop=dontstop, StopAt=stopat, Target=target,
                CanClimb=canclimb, UseObjects=useobjects, RandomPath=randompath,
                RandomZ=randomz, NoNudge=nonudge, MoveFlags=moveflags,
                ObjectBreakClassId=obreakclass, ObjectBreakDistance=obreakdist,
                TargetDistance=targetdist, ClimbRotation=climbrot,
                DiagonalSqueeze=diagsqueeze, RandomPathWallDistance=randpathwall,
                WalkTimerCounter=walktimercounter, TickCounter=tickcounter,
                SpeedType=speedtype, WalkTimerCallback=cb_timer,
                WalkCollisionCallback=cb_collision, ObjectUseCallback=cb_objuse)


def main():
    path = sys.argv[1]
    wanted = set(int(x) for x in sys.argv[2:])
    meta = probe.parse(__import__("pathlib").Path(path))
    with gzip.open(path, "rb") as s:
        r = R(s.read())
    r.pos = meta[21] + 4
    print(f"FILE {path}")
    oid = r.i32()
    while oid != -1:
        atype = r.i32()
        start = r.pos
        if atype == 10 and (not wanted or oid in wanted):
            w = read_walk(r)
            print(f"\n===== WALK owner={oid} payloadBytes={r.pos-start} =====")
            c = w["common"]
            print(f"  Argument={c['Argument']!r} OwnerObjectId={c['OwnerObjectId']} "
                  f"Finish={c['FinishCode']!r} Break={c['BreakCode']!r} "
                  f"Status={c['StatusOrOpaque']} ({c['StatusHex']})")
            for bi, bank in enumerate(w["banks"]):
                print(f"  bank[{bi}] state={bank['BankStateOrAnimSet']} "
                      f"nonzeroSlotCount={len(bank['nonzeroSlots'])} "
                      f"first3={bank['nonzeroSlots'][:3]}")
            print(f"  MoveNeededOrApproach={w['MoveNeededOrApproach']} Waving={w['Waving']} "
                  f"DontStop={w['DontStop']} StopAt={w['StopAt']!r}")
            print(f"  Target={w['Target']!r}")
            print(f"  CanClimb={w['CanClimb']} UseObjects={w['UseObjects']} "
                  f"RandomPath={w['RandomPath']} RandomZ={w['RandomZ']} NoNudge={w['NoNudge']} "
                  f"MoveFlags={w['MoveFlags']}")
            print(f"  ObjectBreakClassId={w['ObjectBreakClassId']} "
                  f"ObjectBreakDistance={w['ObjectBreakDistance']!r} "
                  f"TargetDistance={w['TargetDistance']!r}")
            print(f"  ClimbRotation={w['ClimbRotation']!r} DiagonalSqueeze={w['DiagonalSqueeze']} "
                  f"RandomPathWallDistance={w['RandomPathWallDistance']!r}")
            print(f"  WalkTimerCounter={w['WalkTimerCounter']} TickCounter={w['TickCounter']} "
                  f"SpeedType={w['SpeedType']}")
            print(f"  WalkTimerCallback={w['WalkTimerCallback']!r}")
            print(f"  WalkCollisionCallback={w['WalkCollisionCallback']!r}")
            print(f"  ObjectUseCallback={w['ObjectUseCallback']!r}")
        else:
            probe.skip_action_payload(r, atype)
        oid = r.i32()


if __name__ == "__main__":
    main()
