"""Read-only inventory of zone-bearing game files across base, update and every DLC pack.

Scans ``common.rpf``, ``update/update.rpf`` and each ``update/x64/dlcpacks/*/dlc.rpf`` (with nested
archives) for members whose file name marks a zone table, records exact coordinates and digests, and
parses population-zone ``.ipl`` rows (code, label, bounds). Nothing is written into the game folder.
The output is evidence for the Location coverage ledger: which surfaces carry zone tables, and which
zone codes each surface declares. Requires the pinned fivefury wheel (fivefury.lock.v1.json).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

from fivefury.crypto import GameCrypto
from fivefury.rpf import RpfArchive

ZONE_NAME = re.compile(r"(?i)(popzone|zonebind|mapzone|areazone|zones?\b|zone_)")
MAX_NESTED_BYTES = 768 * 1024 * 1024


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def parse_popzone(text: str) -> list[dict]:
    rows = []
    for line in text.splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) >= 8 and re.fullmatch(r"[A-Za-z0-9_]+", parts[0] or "") and all(
                re.fullmatch(r"-?[0-9.]+", p) for p in parts[1:7]):
            rows.append({"code": parts[0], "min": [float(v) for v in parts[1:4]],
                         "max": [float(v) for v in parts[4:7]], "label": parts[7]})
    return rows


def scan(archive: RpfArchive, coordinate: str, crypto: GameCrypto, found: list, skipped: list, depth: int = 0) -> int:
    count = 0
    for entry in archive.iter_entries():
        if not entry.is_file:
            continue
        count += 1
        path = getattr(entry, "full_path", None) or entry.name
        name = Path(path).name
        if name.lower().endswith(".rpf") and depth < 2:
            size = entry.get_file_size() if hasattr(entry, "get_file_size") else 0
            if size and size > MAX_NESTED_BYTES:
                skipped.append({"coordinate": f"{coordinate}!/{path}", "reason": "nested-archive-over-size-cap", "bytes": size})
                continue
            try:
                nested = RpfArchive.from_bytes(archive.read_entry_bytes(entry, logical=True), name=name, crypto=crypto)
                count += scan(nested, f"{coordinate}!/{path}", crypto, found, skipped, depth + 1)
            except Exception as exc:  # noqa: BLE001 - recorded as an unexamined surface
                skipped.append({"coordinate": f"{coordinate}!/{path}", "reason": "nested-archive-unreadable", "exception": type(exc).__name__})
            continue
        if not ZONE_NAME.search(name):
            continue
        data = archive.read_entry_bytes(entry, logical=True)
        item = {"coordinate": f"{coordinate}!/{path}", "bytes": len(data), "sha256": sha256(data)}
        if name.lower().endswith(".ipl") and "popzone" in name.lower():
            rows = parse_popzone(data.decode("utf-8", "replace"))
            item["popzoneRows"] = len(rows)
            item["codes"] = sorted({r["code"] for r in rows}, key=str.casefold)
            item["labels"] = sorted({r["label"] for r in rows}, key=str.casefold)
        found.append(item)
    return count


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = Path(args.game_root).resolve(strict=True)
    crypto = GameCrypto.from_game(root, gen9=True, use_cache=False)
    surfaces = []
    found: list = []
    skipped: list = []
    outer = [root / "common.rpf", root / "update" / "update.rpf"]
    outer += sorted((root / "update" / "x64" / "dlcpacks").glob("*/dlc.rpf"), key=lambda p: p.as_posix().casefold())
    for path in outer:
        coordinate = path.relative_to(root).as_posix()
        if not path.exists():
            surfaces.append({"coordinate": coordinate, "status": "absent"})
            continue
        before = len(found)
        try:
            archive = RpfArchive.from_path(path, crypto=crypto)
            try:
                members = scan(archive, coordinate, crypto, found, skipped)
            finally:
                archive.close()
            surfaces.append({"coordinate": coordinate, "status": "examined", "fileMembers": members, "zoneMembers": len(found) - before})
        except Exception as exc:  # noqa: BLE001
            surfaces.append({"coordinate": coordinate, "status": "unreadable", "exception": type(exc).__name__})
        print(f"{coordinate}: {surfaces[-1]['status']} zoneMembers={surfaces[-1].get('zoneMembers', 0)}", flush=True)
    inventory = {
        "schemaVersion": 1,
        "kind": "grid.location-registration.game-file-zone-inventory",
        "gameRoot": str(root),
        "surfaces": surfaces,
        "zoneMembers": found,
        "unexamined": skipped,
    }
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(inventory, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    print(f"surfaces={len(surfaces)} zoneMembers={len(found)} unexamined={len(skipped)} -> {args.output}")


if __name__ == "__main__":
    main()
