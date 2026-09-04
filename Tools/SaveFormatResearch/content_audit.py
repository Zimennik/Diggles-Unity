"""Exhaustive CONTENT-level audit of two same-phase A5 saves (native vs port).

Catches the Feuerstelle class of gap: structurally-legal but semantically EMPTY
collections/blocks in the port where the native carries content. For every body,
every ledger field is flattened into metrics (list -> length + nonzero count;
scalar -> value; dict/presence -> flags), aggregated per CLASS, and the two saves'
distributions are diffed both ways.

Usage: python content_audit.py <native.sav> <port.sav>
"""
import importlib.util
import sys
from collections import defaultdict
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("probe", TOOLS / "parse_a5_object_bodies.py")
probe = importlib.util.module_from_spec(spec)
sys.modules["probe"] = probe
sys.modules["parse_a5_object_bodies"] = probe
spec.loader.exec_module(probe)
spec2 = importlib.util.spec_from_file_location("dd", TOOLS / "dump_a5_details.py")
dd = importlib.util.module_from_spec(spec2)
sys.modules["dd"] = dd
spec2.loader.exec_module(dd)


def flatten(prefix, value, out):
    if isinstance(value, bool):
        out[prefix + ".flag"] = 1 if value else 0
    elif isinstance(value, int):
        out[prefix + ".nz"] = 1 if value not in (0, -1) else 0
    elif isinstance(value, float):
        out[prefix + ".nz"] = 1 if abs(value) > 1e-9 else 0
    elif isinstance(value, str):
        out[prefix + ".len"] = len(value)
    elif isinstance(value, (list, tuple)):
        out[prefix + ".count"] = len(value)
        nz = 0
        for item in value:
            if isinstance(item, (int, float)):
                if item not in (0, -1) and abs(item) > 1e-9:
                    nz += 1
            elif item:
                nz += 1
        out[prefix + ".nzItems"] = nz
        if value and isinstance(value[0], dict):
            for i, item in enumerate(value[:1]):
                flatten(prefix + ".rec", item, out)
    elif isinstance(value, dict):
        for k, v in value.items():
            flatten(prefix + "." + str(k), v, out)


def collect(path):
    metadata = probe.parse(path)
    payload = dd.load_payload(Path(path))
    per_class = defaultdict(lambda: defaultdict(list))
    errors = 0
    for summary in metadata[0]:
        try:
            detail = dd.read_object_detail(payload, summary)
        except Exception:
            errors += 1
            continue
        metrics = {}
        for key, value in detail.items():
            if key in ("objectId", "offset", "size"):
                continue
            flatten(key, value, metrics)
        cls = summary.class_name
        for m, v in metrics.items():
            per_class[cls][m].append(v)
    return per_class, errors


def rich_fraction(values):
    if not values:
        return 0.0
    return sum(1 for v in values if v) / len(values)


def main():
    nat_path, port_path = sys.argv[1], sys.argv[2]
    nat, ne = collect(nat_path)
    port, pe = collect(port_path)
    print(f"native classes={len(nat)} errors={ne} | port classes={len(port)} errors={pe}")
    shared = sorted(set(nat) & set(port))
    findings = []
    for cls in shared:
        nm, pm = nat[cls], port[cls]
        for metric in sorted(set(nm) & set(pm)):
            nf, pf = rich_fraction(nm[metric]), rich_fraction(pm[metric])
            # native rich, port empty (the Feuerstelle class) — or the reverse
            if nf >= 0.6 and pf == 0.0:
                findings.append((cls, metric, "NATIVE-RICH/PORT-EMPTY", nf, pf,
                                 max(nm[metric]), max(pm[metric])))
            elif pf >= 0.6 and nf == 0.0:
                findings.append((cls, metric, "PORT-RICH/NATIVE-EMPTY", nf, pf,
                                 max(nm[metric]), max(pm[metric])))
    # group by metric to compress
    by_metric = defaultdict(list)
    for cls, metric, kind, nf, pf, nmax, pmax in findings:
        by_metric[(metric, kind)].append((cls, nf, pf, nmax, pmax))
    for (metric, kind), rows in sorted(by_metric.items()):
        classes = [r[0] for r in rows]
        sample = rows[0]
        print(f"[{kind}] {metric}  classes={len(classes)} "
              f"e.g. {classes[:6]} nativeMax={sample[3]} portMax={sample[4]}")


if __name__ == "__main__":
    main()
