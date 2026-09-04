"""Validate the version 0xA5 native object-body stream.

This is a reverse-engineering probe, not a runtime loader. It follows the instruction-level
serialization order of FUN_005d0ce0 and its nested helpers, then proves each calculated body
boundary against the next declared object ID. Keeping this parser separate prevents partially
named fields from leaking into the production save model.

Usage:
    python Tools/SaveFormatResearch/parse_a5_object_bodies.py path/to/save.sav
"""

from __future__ import annotations

import argparse
import gzip
import struct
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


MAX_COUNT = 4 * 1024 * 1024
MAX_OBJECT_ID = 0x1FFF


class ArchiveError(ValueError):
    pass


class Reader:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.pos = 0

    def require(self, size: int) -> None:
        if size < 0 or self.pos + size > len(self.data):
            raise ArchiveError(
                f"read of {size} bytes exceeds the payload at decompressed offset {self.pos}"
            )

    def skip(self, size: int) -> None:
        self.require(size)
        self.pos += size

    def byte(self, label: str, boolean: bool = False) -> int:
        self.require(1)
        value = self.data[self.pos]
        self.pos += 1
        if boolean and value not in (0, 1):
            raise ArchiveError(f"invalid {label} byte {value} at offset {self.pos - 1}")
        return value

    def i32(self, label: str = "Int32") -> int:
        self.require(4)
        value = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return value

    def count(self, label: str, maximum: int = MAX_COUNT) -> int:
        offset = self.pos
        value = self.i32(label)
        if value < 0 or value > maximum:
            raise ArchiveError(f"invalid {label} count {value} at offset {offset}")
        return value

    def marker(self, expected: int) -> None:
        offset = self.pos
        actual = self.i32("marker")
        if actual != expected:
            raise ArchiveError(
                f"expected marker {expected}, found {actual} at decompressed offset {offset}"
            )

    def native_string(self, label: str = "native string") -> str:
        offset = self.pos
        length = self.i32(f"{label} length")
        if length <= 0 or length > 1024 * 1024:
            raise ArchiveError(f"invalid {label} length {length} at offset {offset}")
        self.require(length)
        raw = self.data[self.pos : self.pos + length]
        self.pos += length
        if raw[-1] != 0:
            raise ArchiveError(f"unterminated {label} at offset {offset}")
        return raw[:-1].decode("cp1252")


@dataclass(frozen=True)
class BodySummary:
    object_id: int
    class_name: str
    object_type: int
    offset: int
    size: int


def skip_i32(reader: Reader, count: int) -> None:
    reader.skip(count * 4)


def skip_int_vector(reader: Reader, label: str) -> int:
    count = reader.count(label)
    skip_i32(reader, count)
    return count


def skip_string_vector(reader: Reader, label: str) -> int:
    count = reader.count(label)
    for _ in range(count):
        reader.native_string(label)
    return count


def skip_tcl_variables(reader: Reader) -> None:
    present = reader.i32("Tcl interpreter presence")
    if present == 0:
        return
    if present != 1:
        raise ArchiveError(
            f"invalid Tcl interpreter presence {present} at offset {reader.pos - 4}"
        )
    count = reader.count("Tcl variable")
    for _ in range(count):
        reader.native_string("Tcl variable name")
        reader.native_string("Tcl variable value")


def skip_support_payload(reader: Reader) -> None:
    reader.skip(32)
    skip_i32(reader, 4)
    skip_i32(reader, 9)
    reader.native_string("support string A")
    reader.native_string("support string B")
    skip_i32(reader, 3)
    reader.skip(8)


def skip_support_records(reader: Reader) -> int:
    count = reader.count("object support record")
    for _ in range(count):
        reader.skip(4 + 8 + 8 + 4 + 4)
        skip_support_payload(reader)
    return count


def skip_route_vector(reader: Reader) -> None:
    skip_int_vector(reader, "route node")
    skip_i32(reader, 2)


def skip_actor_link(reader: Reader) -> None:
    skip_i32(reader, 3)


def skip_actor_link_group(reader: Reader) -> None:
    for _ in range(3):
        skip_actor_link(reader)
    skip_string_vector(reader, "actor link string")
    reader.byte("actor link flag", boolean=True)


def skip_named_index(reader: Reader) -> None:
    skip_i32(reader, 1)
    sentinel = reader.i32("named index sentinel")
    while sentinel != -1:
        if sentinel != 1:
            raise ArchiveError(
                f"invalid named index sentinel {sentinel} at offset {reader.pos - 4}"
            )
        reader.native_string("named index key")
        skip_i32(reader, 1)
        sentinel = reader.i32("named index sentinel")


def skip_mobile_state(reader: Reader) -> None:
    count = reader.count("mobile state entry")
    for _ in range(count):
        skip_i32(reader, 2)
        reader.native_string("mobile state class")
        reader.native_string("mobile state resource")
        skip_i32(reader, 2)
        reader.skip(2)

    skip_i32(reader, 1)
    skip_i32(reader, 1)
    reader.skip(7)
    skip_i32(reader, 3)
    reader.skip(8)
    skip_i32(reader, 4)
    reader.skip(1)
    skip_i32(reader, 5)
    for _ in range(16):
        skip_i32(reader, 3)
    reader.skip(2)


def skip_attachment_history(reader: Reader) -> None:
    count = reader.count("attachment history")
    for _ in range(count):
        skip_route_vector(reader)


def skip_action_variant_zero(reader: Reader) -> None:
    skip_i32(reader, 8)
    count = reader.count("action variant zero entry")
    skip_i32(reader, count * 10)
    skip_i32(reader, 3)
    for _ in range(16):
        skip_i32(reader, 3)
    skip_i32(reader, 4)
    skip_i32(reader, 1)
    skip_i32(reader, 1)
    skip_i32(reader, 1)
    skip_i32(reader, 8)
    reader.byte("action variant zero flag", boolean=True)


def skip_action_variant(reader: Reader) -> None:
    variant = reader.i32("action variant")
    if variant == 0:
        skip_action_variant_zero(reader)
    elif variant == 1:
        skip_i32(reader, 3)
        skip_i32(reader, 2)
        skip_i32(reader, 1)
        skip_i32(reader, 1)
        reader.byte("action variant one flag", boolean=True)
        skip_i32(reader, 2)
    elif variant == 2:
        skip_i32(reader, 1)
        skip_i32(reader, 1)
        skip_i32(reader, 1)
        skip_i32(reader, 3)
        skip_i32(reader, 1)
        reader.byte("action variant two flag", boolean=True)
        skip_i32(reader, 1)
        skip_int_vector(reader, "action variant two value")
        skip_string_vector(reader, "action variant two string")
        skip_i32(reader, 1)
    else:
        raise ArchiveError(
            f"invalid action variant {variant} at offset {reader.pos - 4}"
        )


def skip_extended_support_table(reader: Reader) -> None:
    skip_i32(reader, 2)
    for _ in range(64):
        if reader.byte("extended support presence", boolean=True):
            skip_support_payload(reader)
    count = reader.count("extended support record")
    for _ in range(count):
        skip_support_payload(reader)


def parse_object_body(
    reader: Reader, object_id: int, declared_class: str
) -> BodySummary:
    offset = reader.pos
    class_name = reader.native_string("object body class")
    if class_name != declared_class:
        raise ArchiveError(
            f"object {object_id} body class {class_name!r} does not match declaration "
            f"{declared_class!r} at offset {offset}"
        )
    object_type = reader.i32("object type")
    if object_type < -1 or object_type > 12:
        raise ArchiveError(
            f"invalid object type {object_type} for ID {object_id} at offset {reader.pos - 4}"
        )

    skip_i32(reader, 6)
    skip_i32(reader, 3)
    skip_i32(reader, 13)

    reference_count = reader.count("object child reference")
    skip_i32(reader, reference_count)
    skip_string_vector(reader, "object string")
    skip_i32(reader, 16)
    skip_tcl_variables(reader)
    skip_i32(reader, 1)
    reader.native_string("object state string")

    skip_i32(reader, 4)
    skip_i32(reader, 4)
    skip_route_vector(reader)

    if reader.i32("object grid presence") != 0:
        skip_i32(reader, 512)

    skip_i32(reader, 2)
    skip_i32(reader, 4)
    skip_i32(reader, 10)
    skip_i32(reader, 3)

    if reader.byte("object particle state presence", boolean=True):
        skip_i32(reader, 160)

    reader.skip(3)
    reader.native_string("object display string")
    skip_i32(reader, 4)
    skip_i32(reader, 8)
    skip_i32(reader, 5)
    reader.skip(1)
    skip_i32(reader, 4)
    skip_i32(reader, 6)
    skip_i32(reader, 1)

    if object_type in (1, 11):
        skip_actor_link_group(reader)
    if object_type == 1:
        skip_named_index(reader)
    if object_type in (5, 6, 7, 10):
        skip_mobile_state(reader)

    if object_type == 1:
        skip_i32(reader, 4)

    if reader.byte("attachment history presence", boolean=True):
        skip_attachment_history(reader)

    skip_i32(reader, 16)
    reader.skip(16)
    skip_i32(reader, 16)
    reader.skip(1)
    skip_i32(reader, 1)
    reader.skip(1)

    if reader.byte("action variant presence", boolean=True):
        skip_action_variant(reader)

    skip_extended_support_table(reader)
    skip_i32(reader, 1)
    reader.skip(1)

    return BodySummary(object_id, class_name, object_type, offset, reader.pos - offset)


def seek_to_objects(reader: Reader) -> tuple[dict[int, str], int, int, int]:
    version = reader.i32("version")
    if version != 0xA5:
        raise ArchiveError(f"this probe requires version 0xA5, found 0x{version:X}")
    reader.native_string("header")
    reader.skip(8)
    reader.marker(1)
    reader.skip(205)
    reader.marker(2)
    reader.marker(3)

    width = reader.i32("world width")
    height = reader.i32("world height")
    if width <= 0 or height <= 0 or width * height > MAX_COUNT:
        raise ArchiveError(f"invalid world dimensions {width}x{height}")
    reader.skip(16)
    reader.skip(width * height * 53)
    reader.skip(40)

    primary = reader.count("primary fluid")
    reader.skip(primary * 37)
    secondary = reader.count("secondary fluid")
    reader.skip(secondary * 41)
    reader.skip(8)
    for _ in range(4):
        reader.skip(reader.count("fluid sparse") * 8)
    reader.skip(1)
    for _ in range(8):
        reader.skip(reader.count("fog sparse") * 8)

    reader.marker(4)
    reader.marker(5)
    reader.marker(6)

    declarations: dict[int, str] = {}
    previous_id = 1
    while True:
        object_id = reader.i32("object declaration ID")
        if object_id == -1:
            break
        if object_id <= previous_id or object_id > MAX_OBJECT_ID:
            raise ArchiveError(f"invalid ordered object declaration ID {object_id}")
        declarations[object_id] = reader.native_string("object declaration class")
        previous_id = object_id

    separator = reader.native_string("object declaration separator")
    if separator:
        raise ArchiveError(f"expected an empty object separator, found {separator!r}")
    support_count = skip_support_records(reader)
    return declarations, support_count, width, height


def skip_manager_object_array(reader: Reader) -> int:
    count = reader.count("object manager array")
    for _ in range(count):
        reader.native_string("object manager array class")
        skip_i32(reader, 7)
        reader.skip(3)
        reader.native_string("object manager array Tcl value")
        skip_i32(reader, 1)
    return count


def skip_object_manager_tail(reader: Reader) -> None:
    skip_i32(reader, 18 * 512)
    skip_i32(reader, 2)
    for _ in range(3):
        skip_manager_object_array(reader)
        skip_manager_object_array(reader)
        skip_int_vector(reader, "object manager value")
        reader.skip(2)

    action_record_count = reader.count("object manager action record")
    if action_record_count != 0:
        raise ArchiveError(
            "the current fixture contains object-manager action records; "
            "their class-specific payloads are not part of this boundary probe yet"
        )


def skip_class_table_entry(reader: Reader) -> None:
    skip_i32(reader, 16)
    skip_i32(reader, 4)
    skip_int_vector(reader, "class table route")
    skip_i32(reader, 2)
    reader.native_string("class table resource")
    reader.skip(1)
    skip_i32(reader, 4)
    reader.skip(2)
    skip_i32(reader, 10)
    reader.native_string("class table string")
    reader.skip(3)
    skip_i32(reader, 1)
    skip_i32(reader, 6)
    reader.skip(1)


def skip_spatial_index_record(reader: Reader) -> None:
    skip_i32(reader, 6)
    reader.native_string("spatial index class")
    skip_i32(reader, 4)
    reader.skip(2)


def skip_current_class_table(reader: Reader, width: int, height: int) -> tuple[int, int]:
    skip_i32(reader, 1)
    skip_i32(reader, 0x3000)
    skip_i32(reader, 1)
    reader.skip(1)
    skip_int_vector(reader, "class table free slot")
    skip_int_vector(reader, "class table occupied slot")

    entry_count = 0
    previous_index = -1
    index = reader.i32("class table entry index")
    while index != -1:
        if index <= previous_index or index >= 0x3000:
            raise ArchiveError(f"invalid ordered class table entry index {index}")
        skip_class_table_entry(reader)
        previous_index = index
        entry_count += 1
        index = reader.i32("class table entry index")

    bucket_count = ((width + 7) // 8) * ((height + 7) // 8)
    spatial_record_count = 0
    for _ in range(bucket_count):
        record_count = reader.count("spatial index bucket")
        spatial_record_count += record_count
        for _ in range(record_count):
            skip_spatial_index_record(reader)
    return entry_count, spatial_record_count


def skip_current_control_state(reader: Reader) -> int:
    entry_count = 0
    previous_index = -1
    index = reader.i32("control-state index")
    while index != -1:
        if index <= previous_index or index >= 0x800:
            raise ArchiveError(f"invalid ordered control-state index {index}")
        skip_i32(reader, 9)
        previous_index = index
        entry_count += 1
        index = reader.i32("control-state index")
    return entry_count


def skip_control_records(reader: Reader) -> tuple[int, int]:
    record_count = reader.count("control record")
    nested_record_count = 0
    for _ in range(record_count):
        skip_i32(reader, 3)
        nested_count = reader.count("nested control record")
        nested_record_count += nested_count
        skip_i32(reader, nested_count * 3)
    return record_count, nested_record_count


def skip_animation_controllers(reader: Reader) -> tuple[int, int]:
    controller_count = 0
    vector_value_count = 0
    previous_index = -1
    index = reader.i32("animation controller index")
    while index != -1:
        if index <= previous_index or index > MAX_OBJECT_ID:
            raise ArchiveError(f"invalid ordered animation controller index {index}")
        reader.skip(1)
        reader.skip(8)
        skip_i32(reader, 2)
        value_count = reader.count("animation controller value")
        vector_value_count += value_count
        skip_i32(reader, value_count)
        skip_i32(reader, 5)
        previous_index = index
        controller_count += 1
        index = reader.i32("animation controller index")
    return controller_count, vector_value_count


def skip_graphics_entries(reader: Reader) -> int:
    entry_count = 0
    previous_index = -1
    index = reader.i32("graphics entry index")
    while index != -1:
        if index <= previous_index or index >= 0x1000:
            raise ArchiveError(f"invalid ordered graphics entry index {index}")
        skip_i32(reader, 9)
        reader.skip(1)
        skip_i32(reader, 6)
        previous_index = index
        entry_count += 1
        index = reader.i32("graphics entry index")
    return entry_count


def skip_logic_nested_record(reader: Reader) -> None:
    reader.native_string("logic nested record name")
    first_count = reader.count("logic nested value")
    for _ in range(first_count):
        reader.native_string("logic nested value name")
        skip_i32(reader, 4)
        reader.skip(1)
    skip_i32(reader, 3)
    skip_i32(reader, reader.count("logic nested four-value record") * 4)
    skip_i32(reader, reader.count("logic nested five-value record") * 5)


def skip_logic_descriptor_table(reader: Reader) -> int:
    count = reader.count("logic descriptor")
    for _ in range(count):
        reader.native_string("logic descriptor name")
        skip_i32(reader, 2)
        reader.skip(1)
        skip_int_vector(reader, "logic descriptor vector value")
        skip_i32(reader, 8)
        reader.skip(1)
        skip_i32(reader, 6)
        reader.skip(1)
        skip_i32(reader, 3)
    return count


def skip_first_logic_manager(reader: Reader) -> tuple[int, int, int]:
    string_record_count = reader.count("logic string record")
    for _ in range(string_record_count):
        for _ in range(3):
            reader.native_string("logic string record value")
        reader.skip(1)

    descriptor_count = skip_logic_descriptor_table(reader)

    nested_record_count = reader.count("logic nested record")
    for _ in range(nested_record_count):
        skip_logic_nested_record(reader)

    reader.native_string("logic manager name")
    skip_i32(reader, 11)

    pair_record_count = reader.count("logic string-pair record")
    for _ in range(pair_record_count):
        reader.native_string("logic string-pair first value")
        reader.native_string("logic string-pair second value")
        skip_i32(reader, 1)

    skip_i32(reader, reader.count("logic raw value") )
    skip_i32(reader, 2)

    named_value_count = reader.count("logic named value")
    for _ in range(named_value_count):
        reader.native_string("logic named value name")
        skip_i32(reader, 1)

    reader.native_string("logic trailing name")
    skip_i32(reader, 5)
    reader.skip(1)
    skip_i32(reader, 1)
    reader.skip(1)
    skip_i32(reader, 1)
    return string_record_count, descriptor_count, nested_record_count


def skip_logic_matrix(reader: Reader) -> int:
    value_count = 0
    for _ in range(9 * 9):
        count = reader.count("logic matrix vector value")
        value_count += count
        skip_i32(reader, count)
    return value_count


def skip_action_common_state(reader: Reader) -> None:
    """Skip the state shared by the current CAct_* serializers."""
    reader.native_string("action Tcl state A")
    skip_i32(reader, 1)  # Object reference serialized by FUN_00555d70.
    reader.native_string("action Tcl state B")
    reader.native_string("action Tcl state C")
    skip_i32(reader, 1)


def skip_action_payload(reader: Reader, action_type: int) -> None:
    if action_type == 0:
        return
    if action_type == 2:
        # CAct_Slide is retained only as a legacy type code in the current archive version.
        return
    if action_type in (1, 3):
        skip_action_common_state(reader)
        reader.skip(8)
        return
    if action_type == 4:
        skip_action_common_state(reader)
        skip_i32(reader, 2)
        return
    if action_type == 5:
        skip_action_common_state(reader)
        skip_i32(reader, 7)
        return
    if action_type == 6:
        reader.native_string("attack native state")
        reader.native_string("attack Tcl state")
        skip_i32(reader, 5)
        reader.skip(8)
        reader.skip(3)
        skip_i32(reader, 1)
        reader.skip(4)
        skip_i32(reader, 5)
        reader.skip(1)
        skip_action_common_state(reader)
        return
    if action_type == 7:
        skip_i32(reader, 2)
        skip_int_vector(reader, "ballistic action first vector value")
        skip_i32(reader, 2)
        skip_int_vector(reader, "ballistic action second vector value")
        skip_i32(reader, 2)
        reader.skip(1)
        skip_i32(reader, 3)
        reader.skip(2)
        skip_i32(reader, 1)
        reader.skip(8)
        skip_action_common_state(reader)
        return
    if action_type == 8:
        skip_action_common_state(reader)
        skip_i32(reader, 12)
        reader.skip(1)
        skip_i32(reader, 1)
        reader.skip(1)
        return
    if action_type == 9:
        skip_action_common_state(reader)
        skip_i32(reader, 2)
        target_count = reader.count("lore action target")
        skip_i32(reader, target_count * 2)
        table_count = reader.count("lore action table record")
        skip_i32(reader, table_count * 8)
        return
    if action_type == 10:
        skip_action_common_state(reader)
        for _ in range(4):
            skip_i32(reader, 1 + 24 * 5)
        skip_i32(reader, 1)
        reader.skip(2)
        skip_i32(reader, 4)
        reader.skip(2)
        skip_i32(reader, 2)
        reader.skip(4)
        skip_i32(reader, 6)
        reader.skip(1)
        skip_i32(reader, 4)
        reader.native_string("walk Tcl state")
        reader.native_string("walk native state A")
        reader.native_string("walk native state B")
        return
    if action_type == 11:
        skip_action_common_state(reader)
        skip_i32(reader, 10)
        return
    raise ArchiveError(f"unsupported current action type {action_type}")


def skip_action_table(reader: Reader) -> tuple[int, Counter[int], int, int]:
    action_count = 0
    action_types: Counter[int] = Counter()
    seen_object_ids: set[int] = set()
    object_id = reader.i32("action owner object ID")
    while object_id != -1:
        if object_id < 0 or object_id > MAX_OBJECT_ID or object_id in seen_object_ids:
            raise ArchiveError(f"invalid action owner object ID {object_id}")
        action_type = reader.i32("action type")
        if action_type < 0 or action_type > 11:
            raise ArchiveError(f"invalid action type {action_type} for object ID {object_id}")
        skip_action_payload(reader, action_type)
        seen_object_ids.add(object_id)
        action_count += 1
        action_types[action_type] += 1
        object_id = reader.i32("action owner object ID")

    first_sentinel_offset = reader.pos
    first_sentinel = reader.i32("action table sentinel")
    if first_sentinel != 0x626C6133:
        raise ArchiveError(
            f"invalid action table sentinel {first_sentinel:#x} at offset {first_sentinel_offset}"
        )

    auxiliary_count = reader.count("action auxiliary record")
    auxiliary_vector_value_count = 0
    for _ in range(auxiliary_count):
        skip_i32(reader, 1)
        vector_count = reader.count("action auxiliary vector record")
        auxiliary_vector_value_count += vector_count
        skip_i32(reader, vector_count * 8)
        skip_i32(reader, 3)

    second_sentinel_offset = reader.pos
    second_sentinel = reader.i32("action auxiliary table sentinel")
    if second_sentinel != 0x626C6134:
        raise ArchiveError(
            f"invalid action auxiliary table sentinel {second_sentinel:#x} "
            f"at offset {second_sentinel_offset}"
        )
    return action_count, action_types, auxiliary_count, auxiliary_vector_value_count


def skip_scheduler_record(reader: Reader) -> None:
    skip_i32(reader, 4)
    reader.native_string("scheduler Tcl state A")
    reader.skip(8 * 4)
    reader.skip(1)
    skip_i32(reader, 1)
    reader.skip(8 * 2)
    skip_i32(reader, 1)
    reader.native_string("scheduler resource name")
    skip_i32(reader, 1)
    reader.native_string("scheduler Tcl state B")
    skip_i32(reader, 1)
    reader.native_string("scheduler Tcl state C")
    reader.native_string("scheduler Tcl state D")


def skip_scheduler_table(reader: Reader) -> tuple[int, int]:
    slot_count = reader.count("scheduler slot")
    record_count = 0
    for _ in range(slot_count):
        presence = reader.i32("scheduler record presence")
        if presence != 0:
            skip_scheduler_record(reader)
            record_count += 1
    return slot_count, record_count


def skip_late_player_state(reader: Reader) -> tuple[int, int, int]:
    first_count = reader.count("late player primary record")
    for _ in range(first_count):
        reader.native_string("late player primary name")
        skip_i32(reader, 8)
        reader.skip(2)
        reader.native_string("late player primary Tcl state")
        reader.skip(8)
        reader.skip(1)

    second_count = reader.count("late player secondary record")
    for _ in range(second_count):
        reader.native_string("late player secondary name")
        skip_i32(reader, 1)
        reader.native_string("late player secondary Tcl state")
        skip_i32(reader, 2)

    skip_i32(reader, 1)
    third_count = reader.count("late player pair record")
    skip_i32(reader, third_count * 2)
    return first_count, second_count, third_count


def skip_ai_owner_state(reader: Reader) -> None:
    reader.skip(1)
    skip_i32(reader, 1)
    reader.native_string("AI owner script name")
    skip_tcl_variables(reader)


def skip_late_owner_state(reader: Reader) -> None:
    reader.native_string("late owner native state")
    skip_i32(reader, 3)
    reader.native_string("late owner Tcl state")
    skip_i32(reader, 8)


def parse(
    path: Path,
) -> tuple[
    list[BodySummary], int, int, int, int, int, int, int, int, int, int, int, int, int, int,
    int, int, int, int, int, int, int, int, Counter[int], int, int, int, int, int, int, int, int,
    int, int, int, int, int, int, int, int, int, int
]:
    with gzip.open(path, "rb") as stream:
        reader = Reader(stream.read())
    declarations, support_count, width, height = seek_to_objects(reader)

    bodies: list[BodySummary] = []
    expected_ids = iter(declarations)
    while True:
        id_offset = reader.pos
        object_id = reader.i32("object body ID")
        if object_id == -1:
            break
        try:
            expected_id = next(expected_ids)
        except StopIteration as error:
            raise ArchiveError(f"unexpected extra object body ID {object_id}") from error
        if object_id != expected_id:
            raise ArchiveError(
                f"expected object body ID {expected_id}, found {object_id} at offset {id_offset}"
            )
        try:
            bodies.append(parse_object_body(reader, object_id, declarations[object_id]))
        except ArchiveError as error:
            raise ArchiveError(
                f"failed while parsing object ID {object_id} ({declarations[object_id]}) "
                f"from offset {id_offset}: {error}"
            ) from error

    try:
        missing_id = next(expected_ids)
    except StopIteration:
        missing_id = None
    if missing_id is not None:
        raise ArchiveError(f"object body stream ended before declared ID {missing_id}")
    body_stream_end_offset = reader.pos
    skip_object_manager_tail(reader)
    class_entry_count, spatial_record_count = skip_current_class_table(
        reader, width, height
    )
    marker7_offset = reader.pos
    reader.marker(7)
    control_state_count = skip_current_control_state(reader)
    marker8_offset = reader.pos
    reader.marker(8)
    control_record_count, nested_control_record_count = skip_control_records(reader)
    marker50_offset = reader.pos
    reader.marker(0x50)
    animation_controller_count, animation_vector_value_count = skip_animation_controllers(reader)
    marker9_offset = reader.pos
    reader.marker(9)
    graphics_entry_count = skip_graphics_entries(reader)
    marker10_offset = reader.pos
    reader.marker(10)
    (
        logic_string_record_count,
        logic_descriptor_count,
        logic_nested_record_count,
    ) = skip_first_logic_manager(reader)
    marker11_offset = reader.pos
    reader.marker(11)
    logic_matrix_value_count = skip_logic_matrix(reader)
    marker12_offset = reader.pos
    reader.marker(12)
    (
        action_count,
        action_types,
        action_auxiliary_count,
        action_auxiliary_vector_value_count,
    ) = skip_action_table(reader)
    marker13_offset = reader.pos
    reader.marker(13)
    logic_pair_record_count = reader.count("logic global pair record")
    skip_i32(reader, logic_pair_record_count * 2)
    marker14_offset = reader.pos
    reader.marker(14)
    scheduler_slot_count, scheduler_record_count = skip_scheduler_table(reader)
    marker15_offset = reader.pos
    reader.marker(15)
    logic_eight_value_record_count = reader.count("logic eight-value record")
    skip_i32(reader, logic_eight_value_record_count * 8)
    marker16_offset = reader.pos
    reader.marker(16)
    logic_named_record_count = reader.count("logic named record")
    for _ in range(logic_named_record_count):
        skip_i32(reader, 4)
        reader.native_string("logic named record value")
        skip_i32(reader, 1)
    marker17_offset = reader.pos
    reader.marker(17)
    (
        late_player_primary_count,
        late_player_secondary_count,
        late_player_pair_count,
    ) = skip_late_player_state(reader)
    skip_i32(reader, 128 * 7)
    late_five_value_record_count = reader.count("late five-value record")
    skip_i32(reader, late_five_value_record_count * 5)
    for _ in range(8):
        skip_ai_owner_state(reader)
    skip_i32(reader, 17)
    reader.skip(2)
    skip_i32(reader, 2)
    skip_i32(reader, 32)
    for _ in range(8):
        skip_late_owner_state(reader)
    marker18_offset = reader.pos
    reader.marker(18)
    final_block_offset = reader.pos
    reader.skip(0x1000)
    if reader.pos != len(reader.data):
        raise ArchiveError(
            f"expected end of payload at offset {reader.pos}, found "
            f"{len(reader.data) - reader.pos} trailing bytes"
        )
    return (
        bodies,
        support_count,
        body_stream_end_offset,
        marker7_offset,
        class_entry_count,
        spatial_record_count,
        control_state_count,
        control_record_count,
        nested_control_record_count,
        marker8_offset,
        marker50_offset,
        animation_controller_count,
        animation_vector_value_count,
        marker9_offset,
        graphics_entry_count,
        marker10_offset,
        logic_string_record_count,
        logic_descriptor_count,
        logic_nested_record_count,
        marker11_offset,
        logic_matrix_value_count,
        marker12_offset,
        action_count,
        action_types,
        action_auxiliary_count,
        action_auxiliary_vector_value_count,
        marker13_offset,
        logic_pair_record_count,
        marker14_offset,
        scheduler_slot_count,
        scheduler_record_count,
        marker15_offset,
        logic_eight_value_record_count,
        marker16_offset,
        logic_named_record_count,
        marker17_offset,
        late_player_primary_count,
        late_player_secondary_count,
        late_player_pair_count,
        late_five_value_record_count,
        marker18_offset,
        final_block_offset,
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("save", type=Path)
    args = parser.parse_args()

    (
        bodies,
        support_count,
        body_stream_end_offset,
        marker7_offset,
        class_entry_count,
        spatial_record_count,
        control_state_count,
        control_record_count,
        nested_control_record_count,
        marker8_offset,
        marker50_offset,
        animation_controller_count,
        animation_vector_value_count,
        marker9_offset,
        graphics_entry_count,
        marker10_offset,
        logic_string_record_count,
        logic_descriptor_count,
        logic_nested_record_count,
        marker11_offset,
        logic_matrix_value_count,
        marker12_offset,
        action_count,
        action_types,
        action_auxiliary_count,
        action_auxiliary_vector_value_count,
        marker13_offset,
        logic_pair_record_count,
        marker14_offset,
        scheduler_slot_count,
        scheduler_record_count,
        marker15_offset,
        logic_eight_value_record_count,
        marker16_offset,
        logic_named_record_count,
        marker17_offset,
        late_player_primary_count,
        late_player_secondary_count,
        late_player_pair_count,
        late_five_value_record_count,
        marker18_offset,
        final_block_offset,
    ) = parse(args.save)
    types = Counter(body.object_type for body in bodies)
    largest = sorted(bodies, key=lambda body: body.size, reverse=True)[:10]
    print(f"validated {len(bodies)} object bodies and {support_count} support records")
    print(f"object body stream ends at decompressed offset {body_stream_end_offset}")
    print(
        f"marker 7 is at offset {marker7_offset}; class entries={class_entry_count}, "
        f"spatial records={spatial_record_count}"
    )
    print(
        f"marker 8 is at offset {marker8_offset}; control-state entries={control_state_count}"
    )
    print(
        f"marker 0x50 is at offset {marker50_offset}; control records={control_record_count}, "
        f"nested records={nested_control_record_count}"
    )
    print(
        f"marker 9 is at offset {marker9_offset}; animation controllers="
        f"{animation_controller_count}, vector values={animation_vector_value_count}"
    )
    print(f"marker 10 is at offset {marker10_offset}; graphics entries={graphics_entry_count}")
    print(
        f"marker 11 is at offset {marker11_offset}; logic string records="
        f"{logic_string_record_count}, descriptors={logic_descriptor_count}, "
        f"nested records={logic_nested_record_count}"
    )
    print(
        f"marker 12 is at offset {marker12_offset}; logic matrix values="
        f"{logic_matrix_value_count}"
    )
    print(
        f"marker 13 is at offset {marker13_offset}; actions={action_count}, "
        f"action types={dict(sorted(action_types.items()))}, auxiliary records="
        f"{action_auxiliary_count}, auxiliary vector records="
        f"{action_auxiliary_vector_value_count}"
    )
    print(f"marker 14 is at offset {marker14_offset}; logic pair records={logic_pair_record_count}")
    print(
        f"marker 15 is at offset {marker15_offset}; scheduler slots={scheduler_slot_count}, "
        f"records={scheduler_record_count}"
    )
    print(
        f"marker 16 is at offset {marker16_offset}; eight-value records="
        f"{logic_eight_value_record_count}"
    )
    print(
        f"marker 17 is at offset {marker17_offset}; named records={logic_named_record_count}"
    )
    print(
        f"marker 18 is at offset {marker18_offset}; late player records="
        f"{late_player_primary_count}/{late_player_secondary_count}/{late_player_pair_count}, "
        f"late five-value records={late_five_value_record_count}"
    )
    print(f"final 4096-byte block starts at offset {final_block_offset}; payload fully consumed")
    print("type counts: " + ", ".join(f"{key}={value}" for key, value in sorted(types.items())))
    print("largest bodies:")
    for body in largest:
        print(
            f"  id={body.object_id:4d} type={body.object_type:2d} size={body.size:6d} "
            f"offset={body.offset:8d} class={body.class_name}"
        )


if __name__ == "__main__":
    main()
