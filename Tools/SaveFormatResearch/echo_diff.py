"""Field-level diff between OUR fresh save and the ORIGINAL's re-save (echo) of it.
Bodies are aligned by object id; every differing dump line is aggregated per field.
Usage: echo_diff.py ours.sav echo.sav [--limit N]"""
import subprocess, sys, collections, re, os

TOOL = os.path.join(os.path.dirname(__file__), "dump_a5_details.py")

def dump_objects(path):
    out = subprocess.run(
        [sys.executable, TOOL, "--objects", path, "--select", "all"],
        capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    objects = {}
    current = None
    for line in out.splitlines():
        m = re.match(r"OBJECT id=(\d+) class='([^']*)'", line)
        if m:
            current = int(m.group(1))
            objects[current] = {"class": m.group(2), "fields": {}}
            continue
        if current is None:
            continue
        f = re.match(r"\s+([A-Za-z0-9_.\-]+)[=:](.*)$", line)
        if f:
            # keep the LAST occurrence per field name; repeated groups get indexed
            name = f.group(1)
            fields = objects[current]["fields"]
            key = name
            index = 1
            while key in fields:
                index += 1
                key = f"{name}#{index}"
            fields[key] = f.group(2).strip()
    return objects

def main():
    ours_path, echo_path = sys.argv[1], sys.argv[2]
    limit = int(sys.argv[sys.argv.index("--limit") + 1]) if "--limit" in sys.argv else 6
    ours = dump_objects(ours_path)
    echo = dump_objects(echo_path)

    only_ours = sorted(set(ours) - set(echo))
    only_echo = sorted(set(echo) - set(ours))
    print(f"objects: ours={len(ours)} echo={len(echo)} "
          f"only-ours={only_ours[:10]} only-echo={only_echo[:10]}")

    per_field = collections.Counter()
    samples = collections.defaultdict(list)
    for oid in sorted(set(ours) & set(echo)):
        a, b = ours[oid], echo[oid]
        if a["class"] != b["class"]:
            print(f"  CLASS MISMATCH #{oid}: {a['class']} vs {b['class']}")
            continue
        keys = set(a["fields"]) | set(b["fields"])
        for key in keys:
            va = a["fields"].get(key, "<absent>")
            vb = b["fields"].get(key, "<absent>")
            if va != vb:
                base = key.split("#")[0]
                per_field[base] += 1
                if len(samples[base]) < limit:
                    samples[base].append((oid, a["class"], va[:90], vb[:90]))

    print(f"\ndiffering field kinds: {len(per_field)}")
    for field, count in per_field.most_common():
        print(f"\n=== {field}  (objects: {count})")
        for oid, cls, va, vb in samples[field]:
            print(f"  #{oid} {cls}\n    ours: {va}\n    echo: {vb}")

if __name__ == "__main__":
    main()
