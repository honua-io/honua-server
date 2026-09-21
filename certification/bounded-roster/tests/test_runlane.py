"""Governed selection never suppresses unknown or reinstated required cells."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from runlane import RETIRED_CELLS, retired_cell


class SelectionTests(unittest.TestCase):
    def test_only_explicit_denominator_retirements_are_excluded(self):
        for name, test_id in RETIRED_CELLS.items():
            self.assertEqual(test_id, retired_cell(name, []))

    def test_reinstated_cells_execute(self):
        for name, test_id in RETIRED_CELLS.items():
            self.assertIsNone(retired_cell(name, [{"test_ids": [test_id]}]))

    def test_unknown_missing_cell_is_not_suppressed(self):
        self.assertIsNone(retired_cell("new_cells.typo", []))
