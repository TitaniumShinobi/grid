from __future__ import annotations

import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "catalog"))
import gta_v_enhanced_route_acquire as routes
from fivefury import Ynd, YndNode, Vector3, build_ynd_bytes, get_ynd_area_id
from fivefury.gxt2 import Gxt2
from fivefury.resource import split_rsc7_sections


class RouteSourceTests(unittest.TestCase):
    def test_standalone_ynd_retains_unsigned_native_key(self):
        area = get_ynd_area_id(Vector3())
        model = Ynd.from_nodes([YndNode(area_id=area, node_id=0, position=Vector3(), street_name_hash=0xF1234567)], area_id=area)
        exact = build_ynd_bytes(model)
        self.assertEqual(routes._parse_ynd(exact), [{"ordinal": 0, "areaId": area, "nodeId": 0, "streetNameHash": 0xF1234567}])
        with self.assertRaises(ValueError):
            routes._parse_ynd(split_rsc7_sections(exact)[1])

    def test_join_uses_keys_not_names_and_preserves_unmatched(self):
        gxt = Gxt2([(1, "Same Road"), (2, "Same Road"), (4, "Other Road")]).to_bytes()
        ynds = [{"nodes": [{"streetNameHash": x} for x in (0, 1, 1, 2, 3)]}]
        self.assertEqual(routes._counts(ynds, gxt), {"yndArtifactCount": 1, "nodeCount": 5,
            "distinctNonzeroHashes": 3, "namedRouteCount": 2, "unmatchedHashCount": 1})

    def test_invalid_and_duplicate_localization_do_not_win(self):
        exact = bytearray(Gxt2([(1, "A"), (2, "B")]).to_bytes())
        exact[16:20] = exact[8:12]
        with self.assertRaises(ValueError):
            routes._gxt_keys(bytes(exact))
        self.assertEqual(routes._gxt_keys(Gxt2([(1, ""), (2, "Bad\nName"), (3, "Road")]).to_bytes()), {3})

    def test_duplicate_native_nodes_fail_closed(self):
        node = SimpleNamespace(area_id=1, node_id=2, street_name_hash=1)
        with patch.object(routes, "read_ynd", return_value=SimpleNamespace(version=1, nodes=[node, node])):
            with self.assertRaises(ValueError):
                routes._parse_ynd(b"fixture")

    def test_unsupported_resource_version_fails_closed(self):
        with patch.object(routes, "read_ynd", return_value=SimpleNamespace(version=2, nodes=[])):
            with self.assertRaises(ValueError):
                routes._parse_ynd(b"fixture")

    def test_frozen_path_retains_nested_coordinates_and_rejects_traversal(self):
        coordinate = routes.MANIFEST["pathsCoordinate"] + "!/nodes1.ynd"
        self.assertEqual(routes._frozen_relative(coordinate),
            "members/update/update.rpf.container/x64/levels/gta5/paths.rpf.container/nodes1.ynd")
        with self.assertRaises(ValueError):
            routes._frozen_relative("update/update.rpf!/../escape")

    def test_manifest_is_bounded_to_closed_source_family(self):
        self.assertEqual(routes.MANIFEST["locale"], "en-US")
        self.assertEqual(routes.MANIFEST["exactBuildClosure"]["namedRouteCount"], 230)
        self.assertIn("not-effective-mounted", routes.MANIFEST["scope"])


if __name__ == "__main__":
    unittest.main()
