"""Deterministic semantic detail dumper for native Diggles 0xA5 saves.

This is a reverse-engineering tool, not a runtime loader.  It reuses the structural
validator in parse_a5_object_bodies.py, then decodes the typed fields exposed by the
production OriginalSave*Document classes.

Examples:
    python dump_a5_details.py --summary game.sav
    python dump_a5_details.py --objects game.sav --select "class=Zwerg"
    python dump_a5_details.py --anim game.sav --owner 104
    python dump_a5_details.py --actions game.sav
    python dump_a5_details.py --players game.sav
"""

from __future__ import annotations

import argparse
import gzip
import math
import struct
from collections import Counter
from pathlib import Path
from typing import Any, Iterable

import parse_a5_object_bodies as probe


TYPE_NAMES = {
    -1: "none",
    0: "dummy",
    1: "gnome",
    2: "monster",
    3: "material",
    4: "tool",
    5: "production",
    6: "energy",
    7: "protection",
    8: "store",
    9: "transport",
    10: "elevator",
    11: "baby",
    12: "info",
}


class DetailReader(probe.Reader):
    def bytes(self, size: int) -> bytes:
        self.require(size)
        value = self.data[self.pos : self.pos + size]
        self.pos += size
        return value

    def i64(self, label: str = "Int64") -> int:
        self.require(8)
        value = struct.unpack_from("<q", self.data, self.pos)[0]
        self.pos += 8
        return value

    def f32(self, label: str = "Single") -> float:
        self.require(4)
        value = struct.unpack_from("<f", self.data, self.pos)[0]
        self.pos += 4
        return value

    def ints(self, count: int, label: str = "Int32") -> list[int]:
        return [self.i32(label) for _ in range(count)]

    def int_vector(self, label: str) -> list[int]:
        return self.ints(self.count(label), label)

    def string_vector(self, label: str) -> list[str]:
        return [self.native_string(label) for _ in range(self.count(label))]


def load_payload(path: Path) -> bytes:
    with gzip.open(path, "rb") as stream:
        return stream.read()


def float_from_bits(bits: int) -> float:
    return struct.unpack("<f", struct.pack("<I", bits & 0xFFFFFFFF))[0]


def double_from_bits(bits: int) -> float:
    return struct.unpack("<d", struct.pack("<Q", bits & 0xFFFFFFFFFFFFFFFF))[0]


def format_float(value: float) -> str:
    if math.isnan(value):
        return "nan"
    if math.isinf(value):
        return "inf" if value > 0 else "-inf"
    return repr(value)


def format_float_vector(values: Iterable[float]) -> str:
    return "[" + ", ".join(format_float(value) for value in values) + "]"


def format_nonzero(values: Iterable[int]) -> str:
    entries = [f"{index}={value}" for index, value in enumerate(values) if value != 0]
    return "[" + ", ".join(entries) + "]"


def format_nonzero_float_words(values: Iterable[int]) -> str:
    entries = [
        f"{index}={format_float(float_from_bits(value))}(bits={value})"
        for index, value in enumerate(values)
        if value != 0
    ]
    return "[" + ", ".join(entries) + "]"


def read_tcl_variables(reader: DetailReader) -> tuple[bool, list[tuple[str, str]]]:
    presence = reader.i32("Tcl interpreter presence")
    if presence == 0:
        return False, []
    if presence != 1:
        raise probe.ArchiveError(
            f"invalid Tcl interpreter presence {presence} at offset {reader.pos - 4}"
        )
    return True, [
        (
            reader.native_string("Tcl variable name"),
            reader.native_string("Tcl variable value"),
        )
        for _ in range(reader.count("Tcl variable"))
    ]


def read_event_payload(reader: DetailReader) -> dict[str, Any]:
    fixed_name = reader.bytes(32)
    terminator = fixed_name.find(b"\0")
    if terminator < 0:
        raise probe.ArchiveError(
            f"unterminated fixed event name at offset {reader.pos - 32}"
        )
    return {
        "name": fixed_name[:terminator].decode("cp1252"),
        "ownerObjectId": reader.i32(),
        "subject1ObjectId": reader.i32(),
        "subject2ObjectId": reader.i32(),
        "subject3ObjectId": reader.i32(),
        "positionWords": reader.ints(9),
        "text1": reader.native_string("event queue text A"),
        "text2": reader.native_string("event queue text B"),
        "numberWords": reader.ints(3),
        "createdAtBits": reader.i64(),
    }


def read_action_variant(reader: DetailReader) -> dict[str, Any]:
    variant = reader.i32("action variant")
    result: dict[str, Any] = {"variant": variant}
    if variant == 0:
        result["leadingValues"] = reader.ints(8)
        entry_count = reader.count("action variant zero entry")
        result["entryCount"] = entry_count
        result["entryValues"] = reader.ints(entry_count * 10)
        result["valuesBeforeFlag"] = reader.ints(66)
        result["flag"] = reader.byte("action variant zero flag", boolean=True)
    elif variant == 1:
        result["valuesBeforeFlag"] = reader.ints(7)
        result["flag"] = reader.byte("action variant one flag", boolean=True)
        result["valuesAfterFlag"] = reader.ints(2)
    elif variant == 2:
        result["valuesBeforeFlag"] = reader.ints(7)
        result["flag"] = reader.byte("action variant two flag", boolean=True)
        result["valuesAfterFlag"] = reader.ints(1)
        result["vectorValues"] = reader.int_vector("action variant two value")
        result["stringValues"] = reader.string_vector("action variant two string")
        result["trailingValues"] = reader.ints(1)
    else:
        raise probe.ArchiveError(
            f"invalid action variant {variant} at offset {reader.pos - 4}"
        )
    return result


def read_object_detail(
    payload: bytes, summary: probe.BodySummary
) -> dict[str, Any]:
    reader = DetailReader(payload)
    reader.pos = summary.offset
    end = summary.offset + summary.size

    detail: dict[str, Any] = {
        "objectId": summary.object_id,
        "offset": summary.offset,
        "size": summary.size,
        "N01.class": reader.native_string("object body class"),
        "N02.archivedTypeWord": reader.i32("object type"),
    }
    detail["N03.position"] = [reader.f32() for _ in range(3)]
    detail["N04.transientPosition"] = [reader.f32() for _ in range(3)]
    detail["N05.state350"] = reader.ints(3)
    core = reader.ints(13)
    detail["N06.coreWords"] = core
    detail["N06.motionSpeed"] = float_from_bits(core[0])
    detail["N06.requestedVelocity"] = [float_from_bits(value) for value in core[1:4]]
    detail["N06.motionDelta"] = [float_from_bits(value) for value in core[4:7]]
    detail["N06.rotation"] = [float_from_bits(value) for value in core[7:10]]
    detail["N06.previousRotation"] = [float_from_bits(value) for value in core[10:13]]
    detail["N07.inventoryIds"] = reader.int_vector("object child reference")
    detail["N08.stringVector"] = reader.string_vector("object string")
    detail["N09.fogBrightnessCache"] = reader.ints(16)
    has_tcl, tcl_variables = read_tcl_variables(reader)
    detail["N10.hasTclState"] = has_tcl
    detail["N10.tclVariables"] = tcl_variables
    detail["N11.stateScopeRef"] = reader.i32()
    detail["N12.instanceName"] = reader.native_string("object state string")
    state200 = reader.ints(4)
    detail["N13.stateMachineWords"] = state200[:3]
    detail["N14.deadStateWord"] = state200[3]
    detail["N15.gameplayWords"] = reader.ints(4)
    detail["N16.partFrameVector"] = reader.int_vector("route node")
    detail["N17.walkResult"] = reader.i32()
    detail["N18.retiredPlaceholder"] = reader.i32()
    grid_presence = reader.i32("object grid presence")
    detail["N19.attributePagePresent"] = grid_presence != 0
    detail["N19.attributePage"] = reader.ints(512) if grid_presence != 0 else []
    detail["N20.state264"] = reader.ints(2)
    detail["N21.state254"] = reader.ints(4)
    detail["N22.state26C"] = reader.ints(10)
    detail["N23.state298"] = reader.ints(3)
    particle_presence = reader.byte("object particle state presence", boolean=True)
    detail["N24.obj2B0Present"] = particle_presence != 0
    detail["N24.obj2B0Words"] = reader.ints(160) if particle_presence else []
    detail["N26.state2B4"] = reader.byte("state2B4")
    detail["N27.state35C"] = reader.byte("state35C")
    detail["N28.state35D"] = reader.byte("state35D")
    detail["N29.displayState"] = reader.native_string("object display string")
    detail["N30.fogSizes"] = reader.ints(2)
    detail["N31.owner"] = reader.i32()
    detail["N32.state378"] = reader.i32()
    detail["N33.iconWords"] = reader.ints(8)
    detail["N34.state39C"] = reader.ints(5)
    detail["N35.state3B4"] = reader.byte("state3B4")
    detail["N36.state3BC"] = reader.ints(4)
    detail["N37-N42.lateCoreWords"] = reader.ints(6)
    detail["N43.triggerWord"] = reader.i32()

    object_type = detail["N02.archivedTypeWord"]
    if object_type in (1, 11):
        links = []
        for name in ("partner", "mother", "father"):
            links.append((name, reader.i32(), reader.i32(), reader.i32()))
        detail["N44-N47.familyLinks"] = links
        detail["N46.children"] = reader.string_vector("family child")
        detail["N47.pregnancy"] = bool(
            reader.byte("family pregnancy flag", boolean=True)
        )
    else:
        detail["N44-N47.familyLinks"] = None

    if object_type == 1:
        talk_sum = reader.i32()
        talk_entries = []
        sentinel = reader.i32("talk-issue sentinel")
        while sentinel != -1:
            if sentinel != 1:
                raise probe.ArchiveError(
                    f"invalid talk-issue sentinel {sentinel} at offset {reader.pos - 4}"
                )
            talk_entries.append((reader.native_string("talk-issue name"), reader.i32()))
            sentinel = reader.i32("talk-issue sentinel")
        detail["talkIssue.sumWord"] = talk_sum
        detail["talkIssue.entries"] = talk_entries
    else:
        detail["talkIssue.entries"] = None

    if object_type in (5, 6, 7, 10):
        slots = []
        for _ in range(reader.count("production slot")):
            slots.append(
                {
                    "registryValue1": reader.i32(),
                    "registryValue2": reader.i32(),
                    "className": reader.native_string("production slot class"),
                    "resourceName": reader.native_string("production slot resource"),
                    "orderCount": reader.i32(),
                    "demandWord": reader.i32(),
                    "invented": reader.byte("invented"),
                    "buildable": reader.byte("buildable"),
                }
            )
        production = {
            "slots": slots,
            "workState": reader.i32(),
            "assignedWorkerObjectId": reader.i32(),
            "requestBytes": list(reader.bytes(7)),
            "pendingPositionWords": reader.ints(3),
            "flowBytes": list(reader.bytes(8)),
            "productionWords": reader.ints(4),
            "productionEnabled": reader.byte("production enabled"),
            "energyWords": reader.ints(5),
            "guestSeats": [reader.ints(3) for _ in range(16)],
            "state3C": reader.byte("production state3C"),
            "state140": reader.byte("production state140"),
        }
        detail["CProd"] = production
    else:
        detail["CProd"] = None

    detail["plannerWords"] = reader.ints(4) if object_type == 1 else None
    attachment_present = bool(
        reader.byte("attachment history presence", boolean=True)
    )
    attachments = []
    if attachment_present:
        for _ in range(reader.count("attachment history")):
            attachments.append(
                {
                    "routeValues": reader.int_vector("attachment history route node"),
                    "trailingValues": reader.ints(2),
                }
            )
    detail["attachmentHistory"] = {
        "present": attachment_present,
        "records": attachments,
    }
    detail["postFamily.iconHolderWords"] = reader.ints(16)
    detail["postFamily.retiredBytes"] = list(reader.bytes(16))
    detail["postFamily.retiredWords"] = reader.ints(16)
    detail["postFamily.tailFlags"] = [
        reader.byte("tail flag348"),
        reader.i32("count down word"),
        reader.byte("mover state byte"),
    ]
    action_present = bool(reader.byte("action variant presence", boolean=True))
    detail["actionVariant"] = read_action_variant(reader) if action_present else None

    read_cursor = reader.i32("event queue read cursor")
    write_cursor = reader.i32("event queue write cursor")
    present_events = []
    for slot in range(64):
        if reader.byte("event queue slot presence", boolean=True):
            present_events.append((slot, read_event_payload(reader)))
    delayed_events = [
        read_event_payload(reader)
        for _ in range(reader.count("delayed event queue record"))
    ]
    detail["eventQueue"] = {
        "readCursor": read_cursor,
        "writeCursor": write_cursor,
        "presentEvents": present_events,
        "delayedEvents": delayed_events,
    }
    detail["pickTargetMode"] = reader.i32()
    detail["destroyQueued"] = reader.byte("destroy queued")

    if reader.pos != end:
        raise probe.ArchiveError(
            f"object {summary.object_id} ended at offset {reader.pos}, expected {end}"
        )
    return detail


def print_event(prefix: str, event: dict[str, Any]) -> None:
    print(
        f"{prefix} name={event['name']!r} ownerObjectId={event['ownerObjectId']} "
        f"subjectObjectIds={[event['subject1ObjectId'], event['subject2ObjectId'], event['subject3ObjectId']]} "
        f"positionWords={event['positionWords']} text1={event['text1']!r} "
        f"text2={event['text2']!r} numberWords={event['numberWords']} "
        f"createdAtBits={event['createdAtBits']}"
    )


def print_object(detail: dict[str, Any]) -> None:
    object_type = detail["N02.archivedTypeWord"]
    print(
        f"OBJECT id={detail['objectId']} class={detail['N01.class']!r} "
        f"type={object_type}({TYPE_NAMES.get(object_type, 'unknown')}) "
        f"offset={detail['offset']} size={detail['size']}"
    )
    for key in (
        "N03.position",
        "N04.transientPosition",
        "N06.requestedVelocity",
        "N06.motionDelta",
        "N06.rotation",
        "N06.previousRotation",
    ):
        print(f"  {key}={format_float_vector(detail[key])}")
    print(f"  N06.motionSpeed={format_float(detail['N06.motionSpeed'])}")
    for key in (
        "N05.state350",
        "N06.coreWords",
        "N07.inventoryIds",
        "N08.stringVector",
        "N09.fogBrightnessCache",
    ):
        print(f"  {key}={detail[key]!r}")
    print(
        "  N05.state350Singles="
        + format_float_vector(float_from_bits(value) for value in detail["N05.state350"])
    )
    print(f"  N10.hasTclState={detail['N10.hasTclState']}")
    print(f"  N10.tclVariableCount={len(detail['N10.tclVariables'])}")
    for name, value in detail["N10.tclVariables"]:
        print(f"    {name!r}={value!r}")
    for key in (
        "N11.stateScopeRef",
        "N12.instanceName",
        "N13.stateMachineWords",
        "N14.deadStateWord",
        "N15.gameplayWords",
        "N16.partFrameVector",
        "N17.walkResult",
        "N18.retiredPlaceholder",
    ):
        print(f"  {key}={detail[key]!r}")
    attributes = detail["N19.attributePage"]
    print(
        f"  N19.attributePage present={detail['N19.attributePagePresent']} "
        f"totalNonzero={sum(value != 0 for value in attributes)} "
        f"nonzero={format_nonzero_float_words(attributes)}"
    )
    for key in (
        "N20.state264",
        "N21.state254",
        "N22.state26C",
        "N23.state298",
    ):
        print(f"  {key}={detail[key]!r}")
    particle_words = detail["N24.obj2B0Words"]
    print(
        f"  N24.obj2B0 present={detail['N24.obj2B0Present']} "
        f"totalNonzero={sum(value != 0 for value in particle_words)}"
    )
    for key in (
        "N26.state2B4",
        "N27.state35C",
        "N28.state35D",
        "N29.displayState",
        "N30.fogSizes",
        "N31.owner",
        "N32.state378",
        "N33.iconWords",
        "N34.state39C",
        "N35.state3B4",
        "N36.state3BC",
        "N37-N42.lateCoreWords",
        "N43.triggerWord",
    ):
        print(f"  {key}={detail[key]!r}")
    gameplay = detail["N15.gameplayWords"]
    print(
        f"  N15.named inventorySlotUse={gameplay[0]} placeSnapMode={gameplay[1]} "
        f"weaponClassId={gameplay[2]} shieldClassId={gameplay[3]}"
    )
    state26c = detail["N22.state26C"]
    print(
        f"  N22.named fogLastX={format_float(float_from_bits(state26c[0]))} "
        f"fogLastY={format_float(float_from_bits(state26c[1]))} "
        f"linkedObjectId={state26c[3]} linkedSlot={state26c[4]}"
    )
    state39c = detail["N34.state39C"]
    print(
        f"  N34.named carrierSlotId={state39c[0]} carrierSubIndex={state39c[1]}"
    )
    late_core = detail["N37-N42.lateCoreWords"]
    print(
        f"  N37-N42.named objectFlags={late_core[0]} value1={late_core[1]} "
        f"dirtyFlags={late_core[2]} value3={late_core[3]} "
        f"digOrderId={late_core[4]} value5={late_core[5]}"
    )

    links = detail["N44-N47.familyLinks"]
    print(f"  N44.familyPresent={links is not None}")
    if links is not None:
        for name, link_id, state, reference_id in links:
            print(
                f"    {name}: id={link_id} state={state} referenceObjectId={reference_id}"
            )
        print(f"  N46.children={detail['N46.children']!r}")
        print(f"  N47.pregnancy={detail['N47.pregnancy']}")

    talk_entries = detail["talkIssue.entries"]
    print(f"  talkIssue.present={talk_entries is not None}")
    if talk_entries is not None:
        print(f"    sumWord={detail['talkIssue.sumWord']} entries={len(talk_entries)}")
        for name, value in talk_entries:
            print(f"    {name!r}={format_float(float_from_bits(value))} bits={value}")

    production = detail["CProd"]
    print(f"  CProd.present={production is not None}")
    if production is not None:
        print(f"    slotCount={len(production['slots'])}")
        for index, slot in enumerate(production["slots"]):
            print(
                f"    slot[{index}] class={slot['className']!r} "
                f"resource={slot['resourceName']!r} orderCount={slot['orderCount']} "
                f"registryValues={[slot['registryValue1'], slot['registryValue2']]} "
                f"demandWord={slot['demandWord']} invented={slot['invented']} "
                f"buildable={slot['buildable']}"
            )
        print(
            "    summary "
            + " ".join(
                f"{key}={production[key]!r}"
                for key in (
                    "workState",
                    "assignedWorkerObjectId",
                    "productionWords",
                    "productionEnabled",
                    "energyWords",
                )
            )
        )

    print(f"  plannerWords={detail['plannerWords']!r}")
    attachment = detail["attachmentHistory"]
    print(
        f"  attachmentHistory present={attachment['present']} "
        f"recordCount={len(attachment['records'])}"
    )
    for index, record in enumerate(attachment["records"]):
        print(
            f"    record[{index}] routeValues={record['routeValues']} "
            f"trailingValues={record['trailingValues']}"
        )
    icon_words = detail["postFamily.iconHolderWords"]
    print(
        f"  postFamily.iconHolderWords totalNonzero="
        f"{sum(value != 0 for value in icon_words)} nonzero={format_nonzero(icon_words)}"
    )
    print(f"  actionVariant.present={detail['actionVariant'] is not None}")
    if detail["actionVariant"] is not None:
        print(f"    {detail['actionVariant']!r}")
    queue = detail["eventQueue"]
    print(
        f"  eventQueue readCursor={queue['readCursor']} writeCursor={queue['writeCursor']} "
        f"presentCount={len(queue['presentEvents'])} delayedCount={len(queue['delayedEvents'])}"
    )
    for slot, event in queue["presentEvents"]:
        print_event(f"    slot[{slot}]", event)
    for index, event in enumerate(queue["delayedEvents"]):
        print_event(f"    delayed[{index}]", event)
    print(f"  pickTargetMode={detail['pickTargetMode']}")
    print(f"  destroyQueued={detail['destroyQueued']}")


def select_bodies(
    bodies: list[probe.BodySummary], selector: str
) -> list[probe.BodySummary]:
    if selector == "all":
        return bodies
    if selector.startswith("id="):
        try:
            object_id = int(selector[3:], 0)
        except ValueError as error:
            raise probe.ArchiveError(f"invalid object selector {selector!r}") from error
        return [body for body in bodies if body.object_id == object_id]
    if selector.startswith("class="):
        class_name = selector[6:]
        return [body for body in bodies if body.class_name == class_name]
    raise probe.ArchiveError(
        f"invalid selector {selector!r}; use all, id=<number>, or class=<name>"
    )


def dump_objects(path: Path, selector: str) -> None:
    metadata = probe.parse(path)
    payload = load_payload(path)
    selected = select_bodies(metadata[0], selector)
    print(f"FILE {path}")
    print(f"SELECT {selector!r} matched={len(selected)}")
    for summary in selected:
        try:
            print_object(read_object_detail(payload, summary))
        except (probe.ArchiveError, UnicodeDecodeError, struct.error) as error:
            print(
                f"ERROR object id={summary.object_id} class={summary.class_name!r} "
                f"decompressedOffset={summary.offset}: {error}"
            )


def dump_animation(path: Path, owner_selector: str) -> None:
    metadata = probe.parse(path)
    payload = load_payload(path)
    reader = DetailReader(payload)
    reader.pos = metadata[10] + 4
    declarations = {body.object_id: body.class_name for body in metadata[0]}
    records: dict[int, dict[str, Any]] = {}
    try:
        for _ in range(metadata[11]):
            controller_index = reader.i32("animation controller index")
            raw_state_byte = reader.byte("animation raw state")
            native_time_bits = reader.i64("animation native time state")
            declared_value_count = reader.i32("animation declared value count")
            current_value = reader.i32("animation current value")
            values = reader.int_vector("animation controller value")
            records[controller_index] = {
                "controllerIndex": controller_index,
                "rawStateByte": raw_state_byte,
                "nativeTimeStateBits": native_time_bits,
                "nativeTimeState": double_from_bits(native_time_bits),
                "declaredValueCount": declared_value_count,
                "currentOrCompatibilityValue": current_value,
                "values": values,
                "repeatedControllerIndex": reader.i32(),
                "rawControlA": reader.i32(),
                "rawControlB": reader.i32(),
                "modelDatabaseSlot": reader.i32(),
                "animationState": reader.i32(),
            }
        terminator = reader.i32("animation controller terminator")
        if terminator != -1:
            raise probe.ArchiveError(
                f"invalid animation terminator {terminator} at offset {reader.pos - 4}"
            )
    except (probe.ArchiveError, struct.error) as error:
        print(f"ERROR animation decompressedOffset={reader.pos}: {error}")

    print(f"FILE {path}")
    if owner_selector == "all":
        owner_ids = sorted(records)
    else:
        try:
            owner_ids = [int(owner_selector, 0)]
        except ValueError as error:
            raise probe.ArchiveError(f"invalid owner {owner_selector!r}") from error
    for owner_id in owner_ids:
        record = records.get(owner_id)
        class_name = declarations.get(owner_id)
        owner_kind = f"class={class_name!r}" if class_name is not None else "static/unowned"
        print(f"OWNER objectId={owner_id} {owner_kind} recordCount={int(record is not None)}")
        if record is None:
            continue
        print(
            "  "
            + " ".join(
                [
                    f"controllerIndex={record['controllerIndex']}",
                    f"rawStateByte={record['rawStateByte']}",
                    f"nativeTimeStateBits={record['nativeTimeStateBits']}",
                    f"nativeTimeState={format_float(record['nativeTimeState'])}",
                    f"declaredValueCount={record['declaredValueCount']}",
                    f"currentOrCompatibilityValue={record['currentOrCompatibilityValue']}",
                    f"values={record['values']}",
                    f"repeatedControllerIndex={record['repeatedControllerIndex']}",
                    f"rawControlA={record['rawControlA']}",
                    f"rawControlB={record['rawControlB']}",
                    f"modelDatabaseSlot={record['modelDatabaseSlot']}",
                    f"animationState={record['animationState']}",
                ]
            )
        )
    per_declared_owner = Counter(int(object_id in records) for object_id in declarations)
    static_count = sum(object_id not in declarations for object_id in records)
    print(
        "HISTOGRAM recordsPerDeclaredOwner "
        + " ".join(f"{count}={owners}" for count, owners in sorted(per_declared_owner.items()))
    )
    print(f"STATIC_OR_UNOWNED recordCount={static_count}")


def dump_actions(path: Path) -> None:
    metadata = probe.parse(path)
    payload = load_payload(path)
    reader = DetailReader(payload)
    reader.pos = metadata[21] + 4
    declared_ids = {body.object_id for body in metadata[0]}
    seen_ids: set[int] = set()
    print(f"FILE {path}")
    try:
        owner_id = reader.i32("action owner object ID")
        while owner_id != -1:
            action_type = reader.i32("action type")
            payload_start = reader.pos
            probe.skip_action_payload(reader, action_type)
            payload_size = reader.pos - payload_start
            print(f"ownerId={owner_id} type={action_type} payloadBytes={payload_size}")
            seen_ids.add(owner_id)
            owner_id = reader.i32("action owner object ID")
        print(
            f"ownersWithoutRow={len(declared_ids - seen_ids)} "
            f"declaredOwners={len(declared_ids)} actionRows={len(seen_ids)}"
        )
    except (probe.ArchiveError, struct.error) as error:
        print(f"ERROR actions decompressedOffset={reader.pos}: {error}")


def seek_player_state(reader: DetailReader, marker17_offset: int) -> list[int]:
    reader.pos = marker17_offset + 4
    probe.skip_late_player_state(reader)
    reader.skip(128 * 7 * 4)
    reader.skip(reader.count("late five-value record") * 5 * 4)
    for _ in range(8):
        probe.skip_ai_owner_state(reader)
    reader.skip(17 * 4)
    reader.skip(2)
    reader.skip(2 * 4)
    reserved = reader.ints(32, "reserved player framing")
    return reserved


def dump_players(path: Path) -> None:
    metadata = probe.parse(path)
    payload = load_payload(path)
    reader = DetailReader(payload)
    print(f"FILE {path}")
    try:
        reserved = seek_player_state(reader, metadata[35])
        print(f"reservedFraming={reserved}")
        for owner in range(8):
            player_name = reader.native_string("player name")
            slot_state = reader.i32()
            player_owner_id = reader.i32()
            clan_owner_id = reader.i32()
            secondary_name = reader.native_string("secondary player name")
            diplomacy = reader.ints(8)
            print(
                f"slot={owner} playerName={player_name!r} slotState={slot_state} "
                f"playerOwnerId={player_owner_id} clanOwnerId={clan_owner_id} "
                f"secondaryName={secondary_name!r} diplomacy={diplomacy}"
            )
        if reader.pos != metadata[40]:
            raise probe.ArchiveError(
                f"player state ended at offset {reader.pos}, expected marker 18 at {metadata[40]}"
            )
    except (probe.ArchiveError, UnicodeDecodeError, struct.error) as error:
        print(f"ERROR players decompressedOffset={reader.pos}: {error}")


def dump_summary(path: Path) -> None:
    metadata = probe.parse(path)
    bodies = metadata[0]
    types = Counter(body.object_type for body in bodies)
    print(f"FILE {path}")
    print(f"validated {len(bodies)} object bodies and {metadata[1]} support records")
    print(f"object body stream ends at decompressed offset {metadata[2]}")
    print(
        f"marker 7 offset={metadata[3]} classEntries={metadata[4]} "
        f"spatialRecords={metadata[5]}"
    )
    print(f"marker 8 offset={metadata[9]} controlStateEntries={metadata[6]}")
    print(
        f"marker 0x50 offset={metadata[10]} controlRecords={metadata[7]} "
        f"nestedRecords={metadata[8]}"
    )
    print(
        f"marker 9 offset={metadata[13]} animationControllers={metadata[11]} "
        f"vectorValues={metadata[12]}"
    )
    print(f"marker 10 offset={metadata[15]} graphicsEntries={metadata[14]}")
    print(
        f"marker 11 offset={metadata[19]} logicStringRecords={metadata[16]} "
        f"descriptors={metadata[17]} nestedRecords={metadata[18]}"
    )
    print(f"marker 12 offset={metadata[21]} logicMatrixValues={metadata[20]}")
    print(
        f"marker 13 offset={metadata[26]} actions={metadata[22]} "
        f"actionTypes={dict(sorted(metadata[23].items()))} "
        f"auxiliaryRecords={metadata[24]} auxiliaryVectorRecords={metadata[25]}"
    )
    print(f"marker 14 offset={metadata[28]} logicPairRecords={metadata[27]}")
    print(
        f"marker 15 offset={metadata[31]} schedulerSlots={metadata[29]} "
        f"schedulerRecords={metadata[30]}"
    )
    print(f"marker 16 offset={metadata[33]} eightValueRecords={metadata[32]}")
    print(f"marker 17 offset={metadata[35]} namedRecords={metadata[34]}")
    print(
        f"marker 18 offset={metadata[40]} latePlayerRecords="
        f"{metadata[36]}/{metadata[37]}/{metadata[38]} "
        f"lateFiveValueRecords={metadata[39]}"
    )
    print(f"finalBlockOffset={metadata[41]} payloadFullyConsumed=true")
    print("typeCounts=" + repr(dict(sorted(types.items()))))
    print("largestBodies:")
    for body in sorted(bodies, key=lambda value: value.size, reverse=True)[:10]:
        print(
            f"  id={body.object_id} type={body.object_type} size={body.size} "
            f"offset={body.offset} class={body.class_name!r}"
        )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--summary", type=Path, metavar="FILE")
    modes.add_argument("--objects", type=Path, metavar="FILE")
    modes.add_argument("--anim", type=Path, metavar="FILE")
    modes.add_argument("--actions", type=Path, metavar="FILE")
    modes.add_argument("--players", type=Path, metavar="FILE")
    parser.add_argument("--select", default="all")
    parser.add_argument("--owner", default="all")
    return parser


def main() -> None:
    args = build_parser().parse_args()
    try:
        if args.summary is not None:
            dump_summary(args.summary)
        elif args.objects is not None:
            dump_objects(args.objects, args.select)
        elif args.anim is not None:
            dump_animation(args.anim, args.owner)
        elif args.actions is not None:
            dump_actions(args.actions)
        elif args.players is not None:
            dump_players(args.players)
    except (OSError, probe.ArchiveError, UnicodeDecodeError, struct.error) as error:
        print(f"ERROR decompressedOffset=unknown: {error}")


if __name__ == "__main__":
    main()
