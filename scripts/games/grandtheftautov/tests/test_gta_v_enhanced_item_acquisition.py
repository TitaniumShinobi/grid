"""Focused source-boundary checks; no installed-game access and no registration replay."""
import importlib.util
import json
import pathlib
import sys
import types
import unittest

CATALOG = pathlib.Path(__file__).resolve().parents[1] / 'catalog'
sys.path.insert(0, str(CATALOG))
# Pure parser tests intentionally do not require native extraction dependencies.
stubbed = importlib.util.find_spec('fivefury') is None
if stubbed:
    module = types.ModuleType('fivefury'); module.__path__ = []
    sys.modules['fivefury'] = module
    for name, classes in [('crypto', ['GameCrypto']), ('rpf', ['RpfArchive', 'RpfFileEntry'])]:
        child = types.ModuleType('fivefury.' + name)
        for value in classes: setattr(child, value, type(value, (), {}))
        sys.modules['fivefury.' + name] = child
    child = types.ModuleType('fivefury.ymt')
    child.read_ped_metadata = lambda *args: None
    sys.modules['fivefury.ymt'] = child
import gta_v_enhanced_item_acquire as item
if stubbed:
    for name in ('fivefury', 'fivefury.crypto', 'fivefury.rpf', 'fivefury.ymt', 'gta_v_enhanced_actor_acquire'):
        sys.modules.pop(name, None)


class ItemSourceBoundaryTests(unittest.TestCase):
    def test_manifest_closes_all_admitted_mounted_types(self):
        manifest = json.loads(item.MANIFEST_PATH.read_text('utf-8'))
        self.assertEqual(set(manifest['dataFileTypes'].values()),
                         {'vehicles', 'vehicle-shop', 'weapons', 'components', 'weapon-shop', 'apparel', 'pickups', 'text-manifest'})
        self.assertEqual(manifest['locale'], 'en-US')

    def test_mount_alias_is_exact_and_cross_device_is_unresolved(self):
        self.assertEqual(item.resolve_declared_member('dlc_packCRC:/%PLATFORM%/data/a.meta', 'dlc_pack'), 'x64/data/a.meta')
        self.assertIsNone(item.resolve_declared_member('dlc_other:/common/a.meta', 'dlc_pack'))
        with self.assertRaises(ValueError): item.resolve_declared_member('dlc_pack:/../a.meta', 'dlc_pack')
        with self.assertRaises(ValueError): item.resolve_declared_member('dlc_pack:/a//b', 'dlc_pack')

    def test_raw_xml_is_not_replaced_by_index_claims(self):
        exact = b'<CVehicleModelInfo__InitDataList><InitDatas><Item><modelName>a</modelName></Item></InitDatas></CVehicleModelInfo__InitDataList>'
        self.assertEqual(item.parse_xml(exact, 'CVehicleModelInfo__InitDataList').findtext('InitDatas/Item/modelName'), 'a')
        for bad in [b'<!DOCTYPE x [<!ENTITY a "bad">]><x/>', b'<wrong/>', b'']:
            with self.assertRaises(ValueError): item.parse_xml(bad, 'CVehicleModelInfo__InitDataList')

    def test_receipt_canonicalization_is_order_independent(self):
        self.assertEqual(item.canonical({'b': 2, 'a': ['en-US']}), item.canonical({'a': ['en-US'], 'b': 2}))


if __name__ == '__main__': unittest.main()
