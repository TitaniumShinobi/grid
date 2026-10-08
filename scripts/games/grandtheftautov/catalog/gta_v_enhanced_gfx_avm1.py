"""Bounded, read-only AVM1 inspection for GTA V Enhanced ``hud.gfx``.

This preproduction tool validates one frozen member against an acquisition
receipt, decodes only the GFX/SWF structures needed to inspect the weapon-wheel
classes, and emits a deterministic evidence report. It does not register
canonical knowledge or claim that similarly worded localization is related.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


SCHEMA_VERSION = 1
ANALYZER_ID = "grid.gta-v-enhanced.hud-gfx-avm1-inspection"
ANALYZER_VERSION = "1"
EXPECTED_MEMBER = (
    "update/update.rpf!/"
    "x64/data/cdimages/scaleform_generic.rpf!/hud.gfx"
)
EXPECTED_SLOTS = tuple(f"SLOT_WEAPONS_{index}" for index in range(8))
WHEEL_IDENTITIES = (
    "WHEEL_HEAVY",
    "WHEEL_PISTOL",
    "WHEEL_RIFLE",
    "WHEEL_SHOTGUN",
    "WHEEL_SMG",
    "WHEEL_SNIPER",
    "WHEEL_THROWABLE_SPECIAL",
    "WHEEL_UNARMED_MELEE",
)
CATEGORY_LABEL_PROBES = {
    0x01E2FCD8: "Pistols",
    0x8DECAD5D: "Rifles",
    0xA93F6402: "Shotguns",
    0x7C1E09C0: "Sniper Rifles",
    0xE35E583F: "Melee",
    0xAB577FFD: "Heavy Weapon",
    0x34F2A415: "Explosive",
    0xBFA929F1: "Machine Gun",
}
TRACKED_TEXT = (
    *EXPECTED_SLOTS,
    "SET_PLAYER_WEAPON_WHEEL",
    "SET_WEAPON_WHEEL_SLOT_SELECTION",
    "setWeaponLabel",
    "weaponTextLabel",
    "ARG_WEAPON_NAME",
    "SET_TEXT_WITH_TRANSLATION",
    "setTextWithTranslation",
)


class GfxDecodeError(ValueError):
    """Raised when the bounded decoder cannot prove a complete structure."""


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8", "strict")


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _all_offsets(data: bytes, needle: bytes) -> list[int]:
    result: list[int] = []
    start = 0
    while True:
        offset = data.find(needle, start)
        if offset < 0:
            return result
        result.append(offset)
        start = offset + 1


def _u16(data: bytes, offset: int, limit: int | None = None) -> int:
    boundary = len(data) if limit is None else limit
    if offset < 0 or offset + 2 > boundary:
        raise GfxDecodeError(f"Truncated UI16 at byte {offset}")
    return struct.unpack_from("<H", data, offset)[0]


def _u32(data: bytes, offset: int, limit: int | None = None) -> int:
    boundary = len(data) if limit is None else limit
    if offset < 0 or offset + 4 > boundary:
        raise GfxDecodeError(f"Truncated UI32 at byte {offset}")
    return struct.unpack_from("<I", data, offset)[0]


def _cstring(data: bytes, offset: int, limit: int) -> tuple[str, int]:
    end = data.find(b"\x00", offset, limit)
    if end < 0:
        raise GfxDecodeError(f"Unterminated AVM1 string at byte {offset}")
    try:
        value = data[offset:end].decode("utf-8", "strict")
    except UnicodeDecodeError as error:
        raise GfxDecodeError(f"Invalid UTF-8 AVM1 string at byte {offset}") from error
    return value, end + 1


class _BitReader:
    def __init__(self, data: bytes, byte_offset: int) -> None:
        self._data = data
        self.bit_offset = byte_offset * 8

    def read_unsigned(self, count: int) -> int:
        if count < 0 or self.bit_offset + count > len(self._data) * 8:
            raise GfxDecodeError("Truncated GFX bit field")
        value = 0
        for _ in range(count):
            byte = self._data[self.bit_offset // 8]
            bit = 7 - (self.bit_offset % 8)
            value = (value << 1) | ((byte >> bit) & 1)
            self.bit_offset += 1
        return value

    def read_signed(self, count: int) -> int:
        value = self.read_unsigned(count)
        if count and value & (1 << (count - 1)):
            value -= 1 << count
        return value

    def aligned_byte_offset(self) -> int:
        return (self.bit_offset + 7) // 8


@dataclass(frozen=True)
class _Tag:
    code: int
    header_offset: int
    body_offset: int
    body_end: int


def _tags(data: bytes, start: int, end: int) -> Iterable[_Tag]:
    offset = start
    saw_end = False
    while offset < end:
        header_offset = offset
        packed = _u16(data, offset, end)
        offset += 2
        code = packed >> 6
        length = packed & 0x3F
        if length == 0x3F:
            length = _u32(data, offset, end)
            offset += 4
        body_end = offset + length
        if body_end > end:
            raise GfxDecodeError(f"Tag {code} at byte {header_offset} exceeds its container")
        yield _Tag(code, header_offset, offset, body_end)
        offset = body_end
        if code == 0:
            saw_end = True
            if offset != end:
                padding = data[offset:end]
                if any(padding):
                    raise GfxDecodeError(
                        f"Nonzero bytes follow End tag at byte {header_offset}"
                    )
            break
    if not saw_end:
        raise GfxDecodeError(f"Tag stream at byte {start} has no End tag")


ACTION_NAMES = {
    0x00: "End",
    0x04: "NextFrame",
    0x05: "PreviousFrame",
    0x06: "Play",
    0x07: "Stop",
    0x0A: "Add",
    0x0B: "Subtract",
    0x0C: "Multiply",
    0x0D: "Divide",
    0x0E: "Equals",
    0x0F: "Less",
    0x10: "And",
    0x11: "Or",
    0x12: "Not",
    0x13: "StringEquals",
    0x14: "StringLength",
    0x17: "Pop",
    0x18: "ToInteger",
    0x1C: "GetVariable",
    0x1D: "SetVariable",
    0x20: "SetTarget2",
    0x21: "StringAdd",
    0x22: "GetProperty",
    0x23: "SetProperty",
    0x26: "Trace",
    0x29: "StringLess",
    0x2A: "Throw",
    0x30: "RandomNumber",
    0x3C: "DefineLocal",
    0x3D: "CallFunction",
    0x3E: "Return",
    0x3F: "Modulo",
    0x40: "NewObject",
    0x41: "DefineLocal2",
    0x42: "InitArray",
    0x43: "InitObject",
    0x44: "TypeOf",
    0x45: "TargetPath",
    0x46: "Enumerate",
    0x47: "Add2",
    0x48: "Less2",
    0x49: "Equals2",
    0x4A: "ToNumber",
    0x4B: "ToString",
    0x4C: "PushDuplicate",
    0x4D: "StackSwap",
    0x4E: "GetMember",
    0x4F: "SetMember",
    0x50: "Increment",
    0x51: "Decrement",
    0x52: "CallMethod",
    0x53: "NewMethod",
    0x54: "InstanceOf",
    0x55: "Enumerate2",
    0x60: "BitAnd",
    0x61: "BitOr",
    0x62: "BitXor",
    0x63: "BitLShift",
    0x64: "BitRShift",
    0x65: "BitURShift",
    0x66: "StrictEquals",
    0x67: "Greater",
    0x68: "StringGreater",
    0x81: "GotoFrame",
    0x83: "GetURL",
    0x87: "StoreRegister",
    0x88: "ConstantPool",
    0x8A: "WaitForFrame",
    0x8B: "SetTarget",
    0x8C: "GotoLabel",
    0x8D: "WaitForFrame2",
    0x8E: "DefineFunction2",
    0x94: "With",
    0x96: "Push",
    0x99: "Jump",
    0x9A: "GetURL2",
    0x9B: "DefineFunction",
    0x9D: "If",
    0x9E: "Call",
    0x9F: "GotoFrame2",
}


def _push_values(
    data: bytes, start: int, end: int, constants: list[tuple[str, int]]
) -> list[dict[str, Any]]:
    values: list[dict[str, Any]] = []
    offset = start
    while offset < end:
        value_offset = offset
        kind = data[offset]
        offset += 1
        if kind == 0:
            value, offset = _cstring(data, offset, end)
            values.append({"type": "string", "value": value, "offset": value_offset})
        elif kind == 1:
            if offset + 4 > end:
                raise GfxDecodeError(f"Truncated AVM1 float at byte {value_offset}")
            value = struct.unpack_from("<f", data, offset)[0]
            offset += 4
            values.append({"type": "float", "value": value, "offset": value_offset})
        elif kind == 2:
            values.append({"type": "null", "value": None, "offset": value_offset})
        elif kind == 3:
            values.append({"type": "undefined", "value": None, "offset": value_offset})
        elif kind == 4:
            if offset >= end:
                raise GfxDecodeError(f"Truncated AVM1 register at byte {value_offset}")
            values.append({"type": "register", "value": data[offset], "offset": value_offset})
            offset += 1
        elif kind == 5:
            if offset >= end or data[offset] not in (0, 1):
                raise GfxDecodeError(f"Invalid AVM1 boolean at byte {value_offset}")
            values.append({"type": "boolean", "value": bool(data[offset]), "offset": value_offset})
            offset += 1
        elif kind == 6:
            if offset + 8 > end:
                raise GfxDecodeError(f"Truncated AVM1 double at byte {value_offset}")
            raw = data[offset : offset + 8]
            value = struct.unpack("<d", raw[4:8] + raw[0:4])[0]
            offset += 8
            values.append({"type": "double", "value": value, "offset": value_offset})
        elif kind == 7:
            if offset + 4 > end:
                raise GfxDecodeError(f"Truncated AVM1 integer at byte {value_offset}")
            value = struct.unpack_from("<i", data, offset)[0]
            offset += 4
            values.append({"type": "integer", "value": value, "offset": value_offset})
        elif kind in (8, 9):
            width = 1 if kind == 8 else 2
            if offset + width > end:
                raise GfxDecodeError(f"Truncated AVM1 constant index at byte {value_offset}")
            index = data[offset] if width == 1 else _u16(data, offset, end)
            offset += width
            if index >= len(constants):
                raise GfxDecodeError(
                    f"AVM1 constant index {index} at byte {value_offset} exceeds pool"
                )
            value, constant_offset = constants[index]
            values.append(
                {
                    "type": "constant",
                    "index": index,
                    "value": value,
                    "offset": value_offset,
                    "constantOffset": constant_offset,
                }
            )
        else:
            raise GfxDecodeError(f"Unsupported AVM1 push type {kind} at byte {value_offset}")
    return values


def _function_payload(
    data: bytes, opcode: int, start: int, payload_end: int, block_end: int
) -> tuple[dict[str, Any], int, int]:
    offset = start
    name_offset = offset
    name, offset = _cstring(data, offset, payload_end)
    count = _u16(data, offset, payload_end)
    offset += 2
    parameters: list[dict[str, Any]] = []
    if opcode == 0x8E:
        if offset + 3 > payload_end:
            raise GfxDecodeError(f"Truncated DefineFunction2 at byte {start}")
        register_count = data[offset]
        flags = _u16(data, offset + 1, payload_end)
        offset += 3
        for _ in range(count):
            if offset >= payload_end:
                raise GfxDecodeError(f"Truncated function parameter at byte {offset}")
            register = data[offset]
            offset += 1
            parameter_offset = offset
            parameter, offset = _cstring(data, offset, payload_end)
            parameters.append(
                {"name": parameter, "register": register, "offset": parameter_offset}
            )
    else:
        register_count = None
        flags = None
        for _ in range(count):
            parameter_offset = offset
            parameter, offset = _cstring(data, offset, payload_end)
            parameters.append({"name": parameter, "offset": parameter_offset})
    code_size = _u16(data, offset, payload_end)
    offset += 2
    if offset != payload_end:
        raise GfxDecodeError(
            f"Function metadata at byte {start} has trailing bytes"
        )
    code_start = payload_end
    code_end = code_start + code_size
    if code_end > block_end:
        raise GfxDecodeError(f"Function body at byte {code_start} exceeds its action block")
    result: dict[str, Any] = {
        "name": name,
        "nameOffset": name_offset,
        "parameters": parameters,
        "codeOffset": code_start,
        "codeLength": code_size,
    }
    if register_count is not None:
        result["registerCount"] = register_count
        result["flags"] = flags
    return result, code_start, code_end


def _decode_actions(
    data: bytes,
    start: int,
    end: int,
    owner: str,
    findings: list[dict[str, Any]],
    functions: list[dict[str, Any]],
    inherited_constants: list[tuple[str, int]] | None = None,
    require_end: bool = True,
) -> int:
    offset = start
    constants = list(inherited_constants or ())
    count = 0
    saw_end = False
    while offset < end:
        action_offset = offset
        opcode = data[offset]
        offset += 1
        payload_start = offset
        payload_end = offset
        if opcode >= 0x80:
            length = _u16(data, offset, end)
            offset += 2
            payload_start = offset
            payload_end = offset + length
            if payload_end > end:
                raise GfxDecodeError(
                    f"AVM1 action at byte {action_offset} exceeds action block"
                )
            offset = payload_end
        count += 1
        if opcode == 0:
            saw_end = True
            if offset != end and any(data[offset:end]):
                raise GfxDecodeError(f"Nonzero bytes follow AVM1 End at byte {action_offset}")
            break
        if opcode == 0x88:
            pool_count = _u16(data, payload_start, payload_end)
            cursor = payload_start + 2
            pool: list[tuple[str, int]] = []
            for _ in range(pool_count):
                value_offset = cursor
                value, cursor = _cstring(data, cursor, payload_end)
                pool.append((value, value_offset))
                if value in TRACKED_TEXT or value.startswith("SLOT_WEAPONS_"):
                    findings.append(
                        {
                            "kind": "constant-pool-string",
                            "value": value,
                            "offset": value_offset,
                            "actionOffset": action_offset,
                            "owner": owner,
                        }
                    )
            if cursor != payload_end:
                raise GfxDecodeError(f"Constant pool at byte {action_offset} has trailing bytes")
            constants = pool
        elif opcode == 0x96:
            for value in _push_values(data, payload_start, payload_end, constants):
                text_value = value.get("value")
                if isinstance(text_value, str) and (
                    text_value in TRACKED_TEXT or text_value.startswith("SLOT_WEAPONS_")
                ):
                    findings.append(
                        {
                            "kind": "push",
                            **value,
                            "actionOffset": action_offset,
                            "owner": owner,
                        }
                    )
        elif opcode in (0x8E, 0x9B):
            function, code_start, code_end = _function_payload(
                data, opcode, payload_start, payload_end, end
            )
            function_record = {
                **function,
                "offset": action_offset,
                "owner": owner,
                "opcode": ACTION_NAMES[opcode],
            }
            functions.append(function_record)
            nested_owner = f"{owner}/{function['name'] or '<anonymous>'}@{action_offset}"
            _decode_actions(
                data,
                code_start,
                code_end,
                nested_owner,
                findings,
                functions,
                constants,
                False,
            )
            offset = code_end
    if require_end and not saw_end:
        raise GfxDecodeError(f"AVM1 action block at byte {start} has no End action")
    if not saw_end and offset != end:
        raise GfxDecodeError(f"AVM1 action block at byte {start} did not close exactly")
    return count


def _parse_export_assets(data: bytes, tag: _Tag) -> dict[int, dict[str, Any]]:
    count = _u16(data, tag.body_offset, tag.body_end)
    offset = tag.body_offset + 2
    result: dict[int, dict[str, Any]] = {}
    for _ in range(count):
        character_offset = offset
        character_id = _u16(data, offset, tag.body_end)
        offset += 2
        name_offset = offset
        name, offset = _cstring(data, offset, tag.body_end)
        if character_id in result:
            raise GfxDecodeError(f"Duplicate exported character {character_id}")
        result[character_id] = {
            "name": name,
            "characterId": character_id,
            "offset": character_offset,
            "nameOffset": name_offset,
        }
    if offset != tag.body_end:
        raise GfxDecodeError(f"ExportAssets at byte {tag.header_offset} has trailing bytes")
    return result


def _placed_character(data: bytes, tag: _Tag) -> tuple[int, int] | None:
    if tag.code == 26:
        if tag.body_offset + 3 > tag.body_end:
            raise GfxDecodeError(f"Truncated PlaceObject2 at byte {tag.header_offset}")
        flags = data[tag.body_offset]
        cursor = tag.body_offset + 3
        if not flags & 0x02:
            return None
        return _u16(data, cursor, tag.body_end), cursor
    if tag.code == 70:
        if tag.body_offset + 4 > tag.body_end:
            raise GfxDecodeError(f"Truncated PlaceObject3 at byte {tag.header_offset}")
        flags = data[tag.body_offset]
        flags2 = data[tag.body_offset + 1]
        cursor = tag.body_offset + 4
        if flags2 & 0x08 or ((flags2 & 0x10) and (flags & 0x02)):
            _, cursor = _cstring(data, cursor, tag.body_end)
        if not flags & 0x02:
            return None
        return _u16(data, cursor, tag.body_end), cursor
    return None


def _parse_gfx(data: bytes) -> dict[str, Any]:
    if len(data) < 12 or data[:3] != b"GFX" or data[3] != 8:
        raise GfxDecodeError("Expected an uncompressed Scaleform GFX version 8 file")
    declared_length = _u32(data, 4)
    if declared_length != len(data):
        raise GfxDecodeError("GFX declared length does not match exact input bytes")
    bits = _BitReader(data, 8)
    bit_count = bits.read_unsigned(5)
    frame_rect = {
        "xMin": bits.read_signed(bit_count),
        "xMax": bits.read_signed(bit_count),
        "yMin": bits.read_signed(bit_count),
        "yMax": bits.read_signed(bit_count),
        "offset": 8,
    }
    offset = bits.aligned_byte_offset()
    frame_rate_offset = offset
    frame_rate_raw = _u16(data, offset)
    frame_count = _u16(data, offset + 2)
    offset += 4

    top_tags = list(_tags(data, offset, len(data)))
    tag_counts: dict[str, int] = {}
    exports: dict[int, dict[str, Any]] = {}
    sprites: dict[int, dict[str, Any]] = {}
    findings: list[dict[str, Any]] = []
    functions: list[dict[str, Any]] = []
    action_count = 0
    for tag in top_tags:
        key = str(tag.code)
        tag_counts[key] = tag_counts.get(key, 0) + 1
        if tag.code == 56:
            for character_id, export in _parse_export_assets(data, tag).items():
                if character_id in exports:
                    raise GfxDecodeError(f"Duplicate exported character {character_id}")
                exports[character_id] = export
        elif tag.code == 12:
            action_count += _decode_actions(
                data, tag.body_offset, tag.body_end, "DoAction", findings, functions
            )
        elif tag.code == 59:
            if tag.body_offset + 2 > tag.body_end:
                raise GfxDecodeError(f"Truncated DoInitAction at byte {tag.header_offset}")
            sprite_id = _u16(data, tag.body_offset, tag.body_end)
            action_count += _decode_actions(
                data,
                tag.body_offset + 2,
                tag.body_end,
                f"DoInitAction:{sprite_id}",
                findings,
                functions,
            )
        elif tag.code == 39:
            if tag.body_offset + 4 > tag.body_end:
                raise GfxDecodeError(f"Truncated DefineSprite at byte {tag.header_offset}")
            sprite_id = _u16(data, tag.body_offset, tag.body_end)
            if sprite_id in sprites:
                raise GfxDecodeError(f"Duplicate DefineSprite ID {sprite_id}")
            nested_tags = list(_tags(data, tag.body_offset + 4, tag.body_end))
            placements: list[dict[str, Any]] = []
            for nested in nested_tags:
                placed = _placed_character(data, nested)
                if placed is not None:
                    character_id, character_offset = placed
                    placements.append(
                        {
                            "characterId": character_id,
                            "offset": character_offset,
                            "tagOffset": nested.header_offset,
                        }
                    )
            sprites[sprite_id] = {
                "characterId": sprite_id,
                "offset": tag.body_offset,
                "placements": placements,
            }

    slots: list[dict[str, Any]] = []
    by_name = {export["name"]: export for export in exports.values()}
    for slot_name in EXPECTED_SLOTS:
        export = by_name.get(slot_name)
        if export is None:
            raise GfxDecodeError(f"Required exported HUD slot is absent: {slot_name}")
        sprite = sprites.get(export["characterId"])
        if sprite is None:
            raise GfxDecodeError(f"HUD slot has no DefineSprite: {slot_name}")
        members = []
        for placement in sprite["placements"]:
            member_export = exports.get(placement["characterId"])
            if member_export is not None and member_export["name"].startswith("WEAPON_"):
                members.append(
                    {
                        "nativeIdentity": member_export["name"],
                        "characterId": placement["characterId"],
                        "placementOffset": placement["offset"],
                        "exportOffset": member_export["nameOffset"],
                    }
                )
        slots.append(
            {
                "slotIdentity": slot_name,
                "characterId": export["characterId"],
                "exportOffset": export["nameOffset"],
                "spriteOffset": sprite["offset"],
                "weaponMembers": sorted(
                    members,
                    key=lambda item: (item["nativeIdentity"].encode("utf-8"), item["placementOffset"]),
                ),
            }
        )

    tracked_functions = []
    for function in functions:
        code_start = function["codeOffset"]
        code_end = code_start + function["codeLength"]
        contained = sorted(
            {
                finding["value"]
                for finding in findings
                if code_start <= finding["actionOffset"] < code_end
            },
            key=lambda value: value.encode("utf-8"),
        )
        if (
            function["name"] in TRACKED_TEXT
            or any(parameter["name"] in TRACKED_TEXT for parameter in function["parameters"])
            or contained
        ):
            tracked_functions.append({**function, "trackedReferences": contained})
    findings.sort(key=lambda item: (item["offset"], item["kind"], item["value"]))
    tracked_functions.sort(key=lambda item: (item["offset"], item["name"]))
    exact_byte_probes = []
    for value in WHEEL_IDENTITIES:
        exact_byte_probes.append(
            {
                "kind": "wheel-identity",
                "value": value,
                "utf8Offsets": _all_offsets(data, value.encode("utf-8")),
            }
        )
    for hash_value, display_text in sorted(CATEGORY_LABEL_PROBES.items()):
        exact_byte_probes.append(
            {
                "kind": "candidate-localization",
                "hash": f"0x{hash_value:08X}",
                "text": display_text,
                "utf8Offsets": _all_offsets(data, display_text.encode("utf-8")),
                "littleEndianHashOffsets": _all_offsets(data, struct.pack("<I", hash_value)),
                "bigEndianHashOffsets": _all_offsets(data, struct.pack(">I", hash_value)),
            }
        )
    external_weapon_label_contexts = [
        function
        for function in tracked_functions
        if {"ARG_WEAPON_NAME", "weaponTextLabel"}.issubset(function["trackedReferences"])
        and any(parameter["name"] == "weaponData" for parameter in function["parameters"])
    ]
    return {
        "format": {
            "signature": "GFX",
            "version": 8,
            "declaredLength": declared_length,
            "frameRectTwips": frame_rect,
            "frameRateRaw": frame_rate_raw,
            "frameRateOffset": frame_rate_offset,
            "frameCount": frame_count,
            "frameCountOffset": frame_rate_offset + 2,
        },
        "tagCounts": {key: tag_counts[key] for key in sorted(tag_counts, key=int)},
        "decodedActionCount": action_count,
        "weaponSlots": slots,
        "trackedActionReferences": findings,
        "trackedFunctions": tracked_functions,
        "exactByteProbes": exact_byte_probes,
        "externalWeaponLabelContexts": external_weapon_label_contexts,
    }


def _verified_member(receipt_path: Path, input_path: Path, coordinate: str) -> dict[str, Any]:
    receipt = json.loads(receipt_path.read_text(encoding="utf-8", errors="strict"))
    expected_document_digest = receipt.pop("receiptDocumentSha256", None)
    if expected_document_digest != _sha256_bytes(_canonical_bytes(receipt)):
        raise GfxDecodeError("Acquisition receipt document digest mismatch")
    if receipt.get("schemaVersion") != 2 or receipt.get("gameId") != "game.grandtheftautov-enhanced":
        raise GfxDecodeError("Acquisition receipt schema or GameId mismatch")
    if coordinate != EXPECTED_MEMBER:
        raise GfxDecodeError("Only the exact verified Enhanced hud.gfx member is supported")
    matches = []
    for container in receipt.get("containers", []):
        for member in container.get("members", []):
            if member.get("memberCoordinate") == coordinate:
                matches.append((container, member))
    if len(matches) != 1:
        raise GfxDecodeError("Receipt must contain exactly one hud.gfx member binding")
    container, member = matches[0]
    exact_bytes = input_path.read_bytes()
    if len(exact_bytes) != member.get("byteLength") or _sha256_bytes(exact_bytes) != member.get("sha256"):
        raise GfxDecodeError("Frozen hud.gfx bytes do not match the acquisition receipt")
    expected_container = coordinate.split("!/", 1)[0]
    if container.get("containerCoordinate") != expected_container:
        raise GfxDecodeError("hud.gfx receipt is bound to the wrong outer container")
    return {
        "gameId": receipt["gameId"],
        "gameVersionNamespace": receipt.get("gameVersionNamespace"),
        "gameVersion": receipt.get("gameVersion"),
        "acquisitionReceiptDocumentSha256": expected_document_digest,
        "containerCoordinate": container["containerCoordinate"],
        "containerByteLength": container["byteLength"],
        "containerSha256": container["sha256"],
        "memberCoordinate": coordinate,
        "memberByteLength": member["byteLength"],
        "memberSha256": member["sha256"],
        "acquisitionTool": receipt.get("acquisitionTool"),
    }


def inspect(input_path: Path, receipt_path: Path, coordinate: str) -> dict[str, Any]:
    source = _verified_member(receipt_path, input_path, coordinate)
    decoded = _parse_gfx(input_path.read_bytes())
    category_probe_hits = [
        probe
        for probe in decoded["exactByteProbes"]
        if probe.get("utf8Offsets")
        or probe.get("littleEndianHashOffsets")
        or probe.get("bigEndianHashOffsets")
    ]
    external_contexts = decoded["externalWeaponLabelContexts"]
    if category_probe_hits:
        status = "CATEGORY_BRIDGE_CANDIDATE_REQUIRES_SEMANTIC_VALIDATION"
    elif external_contexts:
        status = "NO_EMBEDDED_CATEGORY_LOCALIZATION_BRIDGE"
    else:
        status = "UNRESOLVED"
    report: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "analyzer": {"id": ANALYZER_ID, "version": ANALYZER_VERSION},
        "source": source,
        "decoded": decoded,
        "bridgeAssessment": {
            "status": status,
            "provenEdge": "exact weapon native identity -> numeric HUD slot membership",
            "missingEdge": "numeric HUD slot/category -> localization key/hash",
            "individualWeaponLabelBoundary": (
                "external weaponData.ARG_WEAPON_NAME input -> weaponTextLabel"
                if external_contexts
                else "UNRESOLVED"
            ),
            "categoryProbeHitCount": len(category_probe_hits),
            "canonicalTerminologyAdmitted": False,
        },
    }
    report["reportDigest"] = _sha256_bytes(_canonical_bytes(report))
    return report


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--receipt", required=True)
    parser.add_argument("--member-coordinate", required=True)
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    report = inspect(
        Path(args.input).resolve(strict=True),
        Path(args.receipt).resolve(strict=True),
        args.member_coordinate,
    )
    output = Path(args.output).resolve()
    if output.exists():
        raise GfxDecodeError("Output report must not already exist")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(_canonical_bytes(report) + b"\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
