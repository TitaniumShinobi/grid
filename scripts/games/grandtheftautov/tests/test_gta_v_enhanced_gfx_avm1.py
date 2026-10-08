"""Conformance checks for the bounded GTA V Enhanced hud.gfx decoder."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "catalog" / "gta_v_enhanced_gfx_avm1.py"
SPEC = importlib.util.spec_from_file_location("grid_gta_v_enhanced_gfx_avm1", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Could not load GTA GFX decoder module")
GFX = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = GFX
SPEC.loader.exec_module(GFX)


def _tag(code: int, body: bytes) -> bytes:
    length = len(body)
    if length < 0x3F:
        return struct.pack("<H", (code << 6) | length) + body
    return struct.pack("<HI", (code << 6) | 0x3F, length) + body


def _action(opcode: int, body: bytes = b"") -> bytes:
    if opcode < 0x80:
        if body:
            raise AssertionError("Short AVM1 actions cannot carry a payload")
        return bytes((opcode,))
    return bytes((opcode,)) + struct.pack("<H", len(body)) + body


def _cstring(value: str) -> bytes:
    return value.encode("utf-8") + b"\x00"


def _fixture_gfx() -> bytes:
    exports = []
    sprites = []
    for index in range(8):
        slot_id = 100 + index
        weapon_id = 200 + index
        exports.extend(((slot_id, f"SLOT_WEAPONS_{index}"), (weapon_id, f"WEAPON_FIXTURE_{index}")))
        placement = _tag(26, b"\x02" + struct.pack("<HH", 1, weapon_id))
        sprites.append(_tag(39, struct.pack("<HH", slot_id, 1) + placement + _tag(0, b"")))
    export_body = struct.pack("<H", len(exports)) + b"".join(
        struct.pack("<H", character_id) + _cstring(name)
        for character_id, name in exports
    )
    constants = (
        "setWeaponLabel",
        "setTextWithTranslation",
        "SLOT_WEAPONS_0",
        "ARG_WEAPON_NAME",
        "weaponTextLabel",
    )
    pool_body = struct.pack("<H", len(constants)) + b"".join(_cstring(value) for value in constants)
    function_code = _action(0x96, b"\x08\x03\x08\x04") + _action(0)
    function_body = (
        _cstring("setWeaponLabel")
        + struct.pack("<H", 1)
        + b"\x01"
        + struct.pack("<H", 0)
        + b"\x01"
        + _cstring("weaponData")
        + struct.pack("<H", len(function_code))
    )
    actions = (
        _action(0x88, pool_body)
        + _action(0x96, b"\x08\x02")
        + _action(0x8E, function_body)
        + function_code
        + _action(0)
    )
    tags = (
        b"".join(sprites)
        + _tag(56, export_body)
        + _tag(59, struct.pack("<H", 100) + actions)
        + _tag(0, b"")
    )
    # RECT: Nbits=1 followed by four zero signed values, padded to two bytes.
    body = b"\x08\x00" + struct.pack("<HH", 0, 1) + tags
    return b"GFX\x08" + struct.pack("<I", 8 + len(body)) + body


def _receipt(member_bytes: bytes) -> dict[str, object]:
    receipt: dict[str, object] = {
        "schemaVersion": 2,
        "gameId": "game.grandtheftautov-enhanced",
        "gameVersionNamespace": "valve.steam.app.3240220.build-id",
        "gameVersion": "25261616",
        "acquisitionTool": {
            "toolId": "fixture",
            "exactVersion": "1",
            "artifactSha256": "0" * 64,
            "sourceRevision": "fixture",
            "methodId": "fixture",
            "methodVersion": "1",
            "receiptSchemaVersion": 2,
        },
        "containers": [
            {
                "containerCoordinate": "update/update.rpf",
                "byteLength": 10,
                "sha256": "1" * 64,
                "members": [
                    {
                        "memberCoordinate": GFX.EXPECTED_MEMBER,
                        "memberPath": "x64/data/cdimages/scaleform_generic.rpf!/hud.gfx",
                        "byteLength": len(member_bytes),
                        "sha256": hashlib.sha256(member_bytes).hexdigest(),
                    }
                ],
            }
        ],
    }
    receipt["receiptDocumentSha256"] = hashlib.sha256(GFX._canonical_bytes(receipt)).hexdigest()
    return receipt


class GtaVEnhancedGfxAvm1Tests(unittest.TestCase):
    def _inspect(self, member_bytes: bytes | None = None) -> tuple[dict[str, object], Path]:
        exact_bytes = _fixture_gfx() if member_bytes is None else member_bytes
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        member = root / "hud.gfx"
        member.write_bytes(exact_bytes)
        receipt = root / "receipt.json"
        receipt.write_bytes(GFX._canonical_bytes(_receipt(exact_bytes)) + b"\n")
        return GFX.inspect(member, receipt, GFX.EXPECTED_MEMBER), root

    def test_decodes_exact_slots_actions_and_offsets_deterministically(self) -> None:
        first, _ = self._inspect()
        second, _ = self._inspect()
        self.assertEqual(GFX._canonical_bytes(first), GFX._canonical_bytes(second))
        self.assertEqual(
            "NO_EMBEDDED_CATEGORY_LOCALIZATION_BRIDGE",
            first["bridgeAssessment"]["status"],
        )
        self.assertEqual(0, first["bridgeAssessment"]["categoryProbeHitCount"])
        self.assertEqual(
            "external weaponData.ARG_WEAPON_NAME input -> weaponTextLabel",
            first["bridgeAssessment"]["individualWeaponLabelBoundary"],
        )
        self.assertFalse(first["bridgeAssessment"]["canonicalTerminologyAdmitted"])
        slots = first["decoded"]["weaponSlots"]
        self.assertEqual(list(GFX.EXPECTED_SLOTS), [slot["slotIdentity"] for slot in slots])
        for index, slot in enumerate(slots):
            self.assertEqual(f"WEAPON_FIXTURE_{index}", slot["weaponMembers"][0]["nativeIdentity"])
            self.assertGreater(slot["exportOffset"], 0)
            self.assertGreater(slot["spriteOffset"], 0)
            self.assertGreater(slot["weaponMembers"][0]["placementOffset"], 0)
        references = first["decoded"]["trackedActionReferences"]
        self.assertTrue(any(item["value"] == "SLOT_WEAPONS_0" for item in references))
        self.assertTrue(all(item["offset"] > 0 for item in references))
        functions = first["decoded"]["trackedFunctions"]
        self.assertEqual("setWeaponLabel", functions[0]["name"])
        self.assertEqual("weaponData", functions[0]["parameters"][0]["name"])
        self.assertGreater(functions[0]["parameters"][0]["offset"], 0)

    def test_receipt_coordinate_digest_and_bytes_fail_closed(self) -> None:
        exact_bytes = _fixture_gfx()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            member = root / "hud.gfx"
            member.write_bytes(exact_bytes)
            receipt_value = _receipt(exact_bytes)
            receipt = root / "receipt.json"
            receipt.write_bytes(GFX._canonical_bytes(receipt_value) + b"\n")
            with self.assertRaises(GFX.GfxDecodeError):
                GFX.inspect(member, receipt, "update/update.rpf!/wrong.gfx")
            member.write_bytes(exact_bytes + b"\x00")
            with self.assertRaises(GFX.GfxDecodeError):
                GFX.inspect(member, receipt, GFX.EXPECTED_MEMBER)
            member.write_bytes(exact_bytes)
            receipt_value["containers"][0]["members"][0]["sha256"] = "2" * 64
            receipt.write_bytes(GFX._canonical_bytes(receipt_value) + b"\n")
            with self.assertRaises(GFX.GfxDecodeError):
                GFX.inspect(member, receipt, GFX.EXPECTED_MEMBER)

    def test_malformed_or_wrong_version_gfx_fails_closed(self) -> None:
        exact_bytes = _fixture_gfx()
        for mutated in (
            b"FWS" + exact_bytes[3:],
            exact_bytes[:3] + b"\x09" + exact_bytes[4:],
            exact_bytes[:-1],
        ):
            with self.subTest(prefix=mutated[:8].hex()):
                with self.assertRaises(GFX.GfxDecodeError):
                    self._inspect(mutated)


if __name__ == "__main__":
    unittest.main()
