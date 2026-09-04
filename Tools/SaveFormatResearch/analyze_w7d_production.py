"""Analyze native production slots and owner attribute pages.

The probe reuses the validated A5 object-body parser and reports only empirical
facts needed by fresh-save synthesis. It does not modify archives.

Examples:
    python analyze_w7d_production.py save.sav
    python analyze_w7d_production.py --glob "Gamesave/quick_Quicksave_2020*.sav"
"""

from __future__ import annotations

import argparse
import glob
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Iterable

import dump_a5_details as details
import parse_a5_object_bodies as probe


ATTRIBUTE_SLOT_COUNT = 512
OWNER_BLOCK_COUNT = 9
DIG_ENABLE_SLOTS = tuple(range(6, 11))
OWNER_SLOTS_OF_INTEREST = (2, 5, 6, 7, 8, 9, 10, 11, 26, 27, 28, 29, 41, 61, 63, 132, 392)


def parse_paths(raw_paths: Iterable[Path], patterns: Iterable[str]) -> list[Path]:
    paths = {path.resolve() for path in raw_paths}
    for pattern in patterns:
        paths.update(Path(match).resolve() for match in glob.glob(pattern))
    return sorted(path for path in paths if path.is_file())


def read_owner_pages(payload: bytes, offset: int) -> list[tuple[list[int], list[int]]]:
    reader = details.DetailReader(payload)
    reader.pos = offset
    return [
        (
            reader.ints(ATTRIBUTE_SLOT_COUNT, "owner primary attribute"),
            reader.ints(ATTRIBUTE_SLOT_COUNT, "owner secondary attribute"),
        )
        for _ in range(OWNER_BLOCK_COUNT)
    ]


def format_counter(values: Counter[Any]) -> str:
    return ",".join(f"{value!r}:{count}" for value, count in sorted(values.items(), key=lambda pair: repr(pair[0])))


def analyze(paths: Iterable[Path]) -> None:
    item_rows: dict[str, dict[str, Counter[Any]]] = defaultdict(
        lambda: defaultdict(Counter)
    )
    class_orders: dict[str, Counter[tuple[str, ...]]] = defaultdict(Counter)
    class_catalog_matches: dict[str, Counter[bool]] = defaultdict(Counter)
    production_words: Counter[tuple[str, int, int, tuple[int, ...]]] = Counter()
    owner_slot_locations: dict[int, Counter[int]] = defaultdict(Counter)
    dig_enable_values: dict[tuple[str, int], Counter[tuple[int, ...]]] = defaultdict(Counter)
    page_values: dict[tuple[str, int, int], Counter[int]] = defaultdict(Counter)
    page_equal_counts: Counter[tuple[int, bool]] = Counter()
    item_counter_owner_values: dict[tuple[str, int], Counter[int]] = defaultdict(Counter)
    production_body_count = 0

    for path in paths:
        metadata = probe.parse(path)
        payload = details.load_payload(path)
        owner_pages = read_owner_pages(payload, metadata[2])

        for block_index, (primary, secondary) in enumerate(owner_pages):
            owner = block_index - 1
            page_equal_counts[(owner, primary == secondary)] += 1
            dig_enable_values[("primary", owner)][tuple(primary[slot] for slot in DIG_ENABLE_SLOTS)] += 1
            dig_enable_values[("secondary", owner)][tuple(secondary[slot] for slot in DIG_ENABLE_SLOTS)] += 1
            for slot in OWNER_SLOTS_OF_INTEREST:
                page_values[("primary", owner, slot)][primary[slot]] += 1
                page_values[("secondary", owner, slot)][secondary[slot]] += 1
                if slot in (132, 392) and primary[slot] != 0:
                    owner_slot_locations[slot][owner] += 1

        save_item_counter_slots: dict[str, set[int]] = defaultdict(set)
        for summary in metadata[0]:
            if summary.object_type not in (5, 6, 7, 10):
                continue
            production_body_count += 1
            detail = details.read_object_detail(payload, summary)
            production = detail["CProd"]
            if production is None:
                raise probe.ArchiveError(f"production body {summary.object_id} has no CProd detail")
            slots = production["slots"]
            slot_order = tuple(slot["className"] for slot in slots)
            class_orders[summary.class_name][slot_order] += 1
            variables = dict(detail["N10.tclVariables"])
            catalog = variables.get(f"tttitems_{summary.class_name}")
            if catalog is not None:
                class_catalog_matches[summary.class_name][tuple(catalog.split()) == slot_order] += 1
            guest_seats = int(variables.get("prod_guest_seats", "-1"))
            production_words[(
                summary.class_name,
                len(slots),
                guest_seats,
                tuple(production["productionWords"]),
            )] += 1
            for slot in slots:
                item = slot["className"]
                row = item_rows[item]
                row["resource"][slot["resourceName"]] += 1
                row["registryValue1"][slot["registryValue1"]] += 1
                if slot["registryValue1"] != -1:
                    save_item_counter_slots[item].add(slot["registryValue1"])
                row["registryValue2"][slot["registryValue2"]] += 1
                row["orderCount"][slot["orderCount"]] += 1
                row["demandWord"][slot["demandWord"]] += 1
                row["invented"][slot["invented"]] += 1
                row["buildable"][slot["buildable"]] += 1

        for item, slots in save_item_counter_slots.items():
            if len(slots) != 1:
                raise probe.ArchiveError(
                    f"{path}: item {item!r} has inconsistent owner-counter slots {sorted(slots)}"
                )
            slot = next(iter(slots))
            for block_index, (primary, _) in enumerate(owner_pages):
                item_counter_owner_values[(item, block_index - 1)][primary[slot]] += 1

    print(f"saves={len(list(paths))} productionBodies={production_body_count} items={len(item_rows)}")
    print("ITEMS")
    for item in sorted(item_rows):
        row = item_rows[item]
        for field in ("resource", "registryValue1", "registryValue2"):
            if len(row[field]) != 1:
                raise probe.ArchiveError(
                    f"item {item!r} has inconsistent {field}: {dict(row[field])}"
                )
        print(
            f"{item}\tresource={format_counter(row['resource'])}"
            f"\tregistryValue1={format_counter(row['registryValue1'])}"
            f"\tregistryValue2={format_counter(row['registryValue2'])}"
            f"\torderCount={format_counter(row['orderCount'])}"
            f"\tdemandWord={format_counter(row['demandWord'])}"
            f"\tinvented={format_counter(row['invented'])}"
            f"\tbuildable={format_counter(row['buildable'])}"
        )

    print("CLASS_ORDERS")
    for class_name in sorted(class_orders):
        print(
            f"{class_name}\torders={format_counter(class_orders[class_name])}"
            f"\tcatalogMatches={format_counter(class_catalog_matches[class_name])}"
        )

    print("PRODUCTION_WORDS")
    for key, count in sorted(production_words.items()):
        class_name, slot_count, guest_seats, words = key
        print(
            f"{class_name}\tslotCount={slot_count}\tguestSeats={guest_seats}"
            f"\twords={words}\tcount={count}"
        )

    print("OWNER_SLOT_LOCATIONS")
    for slot in sorted(owner_slot_locations):
        print(f"slot={slot}\towners={format_counter(owner_slot_locations[slot])}")

    print("ITEM_COUNTER_OWNER_VALUES")
    for key in sorted(item_counter_owner_values):
        item, owner = key
        values = item_counter_owner_values[key]
        nonzero = Counter({bits: count for bits, count in values.items() if bits != 0})
        if nonzero:
            print(f"{item}\towner={owner}\tnonzeroBits={format_counter(nonzero)}")

    print("DIG_ENABLE")
    for key in sorted(dig_enable_values):
        page, owner = key
        print(f"{page}\towner={owner}\tvalues={format_counter(dig_enable_values[key])}")

    print("PAGE_EQUALITY")
    for owner in range(-1, 8):
        counts = Counter({equal: page_equal_counts[(owner, equal)] for equal in (False, True)})
        print(f"owner={owner}\tprimaryEqualsSecondary={format_counter(counts)}")

    print("OWNER_PAGE_VALUES")
    for key in sorted(page_values):
        page, owner, slot = key
        print(f"{page}\towner={owner}\tslot={slot}\tbits={format_counter(page_values[key])}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("saves", nargs="*", type=Path)
    parser.add_argument("--glob", action="append", default=[])
    return parser


def main() -> None:
    args = build_parser().parse_args()
    paths = parse_paths(args.saves, args.glob)
    if not paths:
        raise SystemExit("no save files matched")
    analyze(paths)


if __name__ == "__main__":
    main()
