"""Read-only game acquisition for mounted Item families; writes only a new evidence bundle.

No executable inspection is performed. The existing FiveFury key cache must match the
installed executable's metadata. A missing/stale cache fails closed. Source XML is
retained verbatim; the index is routing data, never substituted for source evidence.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path, PurePosixPath
import re
import xml.etree.ElementTree as ET

from fivefury.crypto import GameCrypto
from fivefury.rpf import RpfArchive, RpfFileEntry

from gta_v_enhanced_actor_acquire import (
    _parse_dlclist_occurrences, _normalize_mount_path, _parse_setup,
    _parse_content, _steam_observation, _sha256_file,
)

MANIFEST_PATH = Path(__file__).with_name('gta_v_enhanced_item_source_families.v1.json')
LOCK_PATH = Path(__file__).with_name('fivefury.lock.v1.json')
MAX_BYTES = 64 * 1024 * 1024
FORMAT_PREFIX = 'rockstar.gta-v.item.'


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(',', ':')).encode('utf-8')


def relative(value):
    if not value or '\\' in value or value.startswith('/') or any(p in ('', '.', '..') for p in value.split('/')):
        raise ValueError('Noncanonical member coordinate')
    return value


def parse_xml(exact, root):
    if not exact or len(exact) > MAX_BYTES:
        raise ValueError('XML source is empty or exceeds limit')
    text = exact.decode('utf-8-sig', 'strict')
    if '<!DOCTYPE' in text.upper() or '<!ENTITY' in text.upper():
        raise ValueError('DTD/entities are prohibited')
    result = ET.fromstring(text)
    if result.tag != root:
        raise ValueError(f'Expected XML root {root}, got {result.tag}')
    return result


def resolve_declared_member(filename, device):
    """CRC is the declared DLC device alias, not a different physical archive."""
    if filename.count(':/') != 1:
        raise ValueError('Invalid DLC filename')
    declared_device, member = filename.split(':/', 1)
    if declared_device.casefold() not in (device.casefold(), (device + 'CRC').casefold()):
        return None  # cross-device declaration: retained unresolved, never guessed
    return relative(member.replace('%PLATFORM%', 'x64').replace('%platform%', 'x64'))


def cached_crypto(root):
    exe = (root / 'GTA5_Enhanced.exe').resolve(strict=True)
    cache = Path(os.environ['LOCALAPPDATA']) / 'fivefury' / 'keys.json'
    item = json.loads(cache.read_text('utf-8')).get(str(exe).lower())
    stat = exe.stat()
    if not item or item.get('size') != stat.st_size or item.get('mtime_ns') != stat.st_mtime_ns:
        raise ValueError('A validated existing FiveFury cache is required; executable inspection is prohibited')
    return GameCrypto.from_aes_key(base64.b64decode(item['aes_key'], validate=True))


def acquire(root, output, observed, wheel):
    root = root.resolve(strict=True)
    if output.exists():
        raise ValueError('Output must be a new directory')
    lock = json.loads(LOCK_PATH.read_text('utf-8'))
    if importlib.metadata.version('fivefury') != lock['exactVersion'] or _sha256_file(wheel)[1] != lock['windowsX64WheelSha256']:
        raise ValueError('FiveFury dependency is not the pinned version/artifact')
    manifest = json.loads(MANIFEST_PATH.read_text('utf-8'))
    platform = _steam_observation(root)
    crypto = cached_crypto(root)
    output.mkdir(parents=True)
    containers, sources, ledger = {}, [], []
    registered = set()

    def open_outer(coordinate):
        path = root / relative(coordinate)
        if coordinate not in containers:
            size, digest = _sha256_file(path)
            containers[coordinate] = dict(containerCoordinate=coordinate, byteLength=size, sha256=digest, artifacts=[])
        return RpfArchive.from_path(path, crypto=crypto)

    def register(outer, member, exact, family, pack='', ordinal=-1, declaration=None):
        coordinate = outer + '!/' + member
        if coordinate in registered:
            return
        if len(exact) > MAX_BYTES and family != 'localization-container':
            raise ValueError('Source exceeds acquisition limit')
        if family in manifest['xmlRoots']:
            parse_xml(exact, manifest['xmlRoots'][family])
        fmt = 'rockstar.gta-v.gxt2-binary' if family == 'localization' else FORMAT_PREFIX + family + '-xml'
        if family == 'localization-container': fmt = 'rockstar.rpf7-container'
        frozen = 'members/' + outer.replace('.rpf', '.rpf.container') + '/' + member.replace('!/', '.nested/')
        destination = output / PurePosixPath(frozen)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(exact)
        digest = hashlib.sha256(exact).hexdigest()
        containers[outer]['artifacts'].append(dict(sourceCoordinate=coordinate, memberPath=member,
            frozenRelativePath=frozen, byteLength=len(exact), sha256=digest, formatId=fmt, formatVersion='1', role=family))
        sources.append(dict(sourceCoordinate=coordinate, family=family, pack=pack, mountOrdinal=ordinal,
            locale='en-US' if family == 'localization' else None, declaration=declaration))
        registered.add(coordinate)

    def read(archive, member):
        entry = archive.find_entry(member)
        if not isinstance(entry, RpfFileEntry): return None
        return archive.read_entry_bytes(entry, logical=True)

    def localize(outer, archive, prefix='', pack='', ordinal=-1, candidates=()):
        for member in candidates:
            exact = read(archive, member)
            if exact is None: continue
            register(outer, prefix + member, exact, 'localization-container', pack, ordinal)
            nested = RpfArchive.from_bytes(exact, name=PurePosixPath(member).name, crypto=crypto)
            try:
                gxt = read(nested, 'global.gxt2')
                if gxt is not None: register(outer, prefix + member + '!/global.gxt2', gxt, 'localization', pack, ordinal)
            finally: nested.close()

    fixed = [('vehicles', 'data/levels/gta5/vehicles.meta'), ('weapons', 'data/ai/weapons.meta'),
             ('components', 'data/ai/weaponcomponents.meta'), ('pickups', 'data/pickups.meta')]
    for outer, pre, ordinal in [('common.rpf', '', -3), ('update/update.rpf', 'common/', -2)]:
        arc = open_outer(outer)
        try:
            for family, member in fixed:
                exact = read(arc, pre + member)
                if exact is not None: register(outer, pre + member, exact, family, ordinal=ordinal)
            if outer.startswith('update/'):
                dlclist = read(arc, 'common/data/dlclist.xml')
                if dlclist is None: raise ValueError('DLC mount list absent')
                register(outer, 'common/data/dlclist.xml', dlclist, 'dlc-list')
                localize(outer, arc, ordinal=ordinal, candidates=['x64/patch/data/lang/american_rel.rpf'])
        finally: arc.close()
    arc = open_outer('x64b.rpf')
    try: localize('x64b.rpf', arc, ordinal=-3, candidates=['data/lang/american_rel.rpf'])
    finally: arc.close()

    physical = {p.name.casefold(): p.name for p in (root / 'update/x64/dlcpacks').iterdir() if p.is_dir()}
    seen = set()
    for occurrence in _parse_dlclist_occurrences(dlclist):
        scheme, pack, normalized = _normalize_mount_path(occurrence['rawMountPath'])
        row = dict(occurrence, pack=pack, normalizedMountPath=normalized)
        if normalized in seen:
            row['status'] = 'duplicate-mount-occurrence'; ledger.append(row); continue
        seen.add(normalized)
        outer, prefix, parent = '', '', None
        if scheme == 'platform':
            outer = 'x64w.rpf'; parent = open_outer(outer)
            nested_member = f'dlcpacks/{pack}/dlc.rpf'
            entry = next((e for e in parent.iter_entries() if e.path.casefold() == nested_member.casefold()), None)
            if entry is None:
                parent.close(); row['status'] = 'declared-container-absent'; ledger.append(row); continue
            prefix = entry.path + '!/'
            arc = RpfArchive.from_bytes(parent.read_entry_bytes(entry, logical=True), name='dlc.rpf', crypto=crypto)
        else:
            directory = physical.get(pack.casefold())
            if directory is None:
                row['status'] = 'declared-container-absent'; ledger.append(row); continue
            outer = f'update/x64/dlcpacks/{directory}/dlc.rpf'; arc = open_outer(outer)
        try:
            setup = read(arc, 'setup2.xml')
            if setup is None: raise ValueError('Mounted setup2.xml absent')
            device, content_member = _parse_setup(setup); content = read(arc, content_member)
            if content is None: raise ValueError('Mounted content manifest absent')
            register(outer, prefix + 'setup2.xml', setup, 'dlc-setup', pack, occurrence['ordinal'])
            register(outer, prefix + content_member, content, 'dlc-content', pack, occurrence['ordinal'])
            unresolved = []
            for group, item, filename, file_type in _parse_content(content):
                family = manifest['dataFileTypes'].get(file_type)
                if family is None: continue
                member = resolve_declared_member(filename, device)
                locator = f'rpf7-member:{outer}!/{prefix}{content_member}#/CDataFileMgr__ContentsOfDataFileXml[1]/dataFiles[{group+1}]/Item[{item+1}]'
                exact = None if member is None else read(arc, member)
                if exact is None:
                    unresolved.append(dict(filename=filename, family=family, declaration=locator, reason='cross-device-or-member-absent')); continue
                register(outer, prefix + member, exact, family, pack, occurrence['ordinal'], locator)
            localize(outer, arc, prefix, pack, occurrence['ordinal'], ['x64/data/lang/americandlc.rpf'])
            row.update(status='indexed', sourceCoordinate=outer+'!/'+prefix+'setup2.xml', unresolved=unresolved)
        finally:
            arc.close()
            if parent is not None: parent.close()
        ledger.append(row)
    for container in containers.values(): container['artifacts'].sort(key=lambda x:x['sourceCoordinate'])
    index = dict(schemaId='grid.gta-v.item-corpus-index', schemaVersion=1,
                 sources=sorted(sources, key=lambda x:x['sourceCoordinate']), mountLedger=ledger)
    index_bytes = canonical(index) + b'\n'; (output/'item-corpus-index.v1.json').write_bytes(index_bytes)
    receipt = dict(schemaVersion=1, gameId=manifest['gameId'], gameVersion=platform['buildId'],
        gameVersionNamespace='valve.steam.app.3240220.build-id', observedAtUtc=observed,
        sourceFamilyManifest=dict(manifestId=manifest['manifestId'],schemaVersion=1,documentSha256=hashlib.sha256(canonical(manifest)).hexdigest()),
        acquisitionTool=dict(receiptSchemaVersion=1,methodId='grid.gta-v-enhanced.item-mounted-acquisition',methodVersion='1',
            toolId='fivefury.rpf-read',exactVersion=lock['exactVersion'],artifactSha256=lock['windowsX64WheelSha256']),
        itemCorpusIndex=dict(fileName='item-corpus-index.v1.json',documentByteLength=len(index_bytes),documentSha256=hashlib.sha256(index_bytes).hexdigest()),
        containers=sorted(containers.values(),key=lambda x:x['containerCoordinate']),platformObservation=platform)
    receipt['receiptDocumentSha256'] = hashlib.sha256(canonical(receipt)).hexdigest()
    (output/'gta-v-enhanced-item-acquisition-receipt.v1.json').write_bytes(canonical(receipt)+b'\n')
    return dict(containers=len(containers),sources=len(sources),mounts=len(ledger),unresolved=sum(len(x.get('unresolved',[])) for x in ledger))


def main():
    p=argparse.ArgumentParser();p.add_argument('--game-root',required=True);p.add_argument('--output',required=True)
    p.add_argument('--observed-at-utc',required=True);p.add_argument('--fivefury-wheel',required=True)
    a=p.parse_args()
    from datetime import datetime
    stamp=datetime.fromisoformat(a.observed_at_utc.replace('Z','+00:00'))
    if stamp.utcoffset() is None or stamp.utcoffset().total_seconds()!=0: raise ValueError('Observation must be UTC')
    print(json.dumps(acquire(Path(a.game_root),Path(a.output),a.observed_at_utc,Path(a.fivefury_wheel)),sort_keys=True))


if __name__ == '__main__': main()
