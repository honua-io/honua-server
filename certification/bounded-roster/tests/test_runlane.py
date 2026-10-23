"""Governed selection never suppresses unknown or reinstated required cells."""
import importlib
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
runlane = importlib.import_module("runlane")


class SelectionTests(unittest.TestCase):
    def test_only_explicit_denominator_retirements_are_excluded(self):
        for name, test_id in runlane.RETIRED_CELLS.items():
            self.assertEqual(test_id, runlane.retired_cell(name, []))

    def test_reinstated_cells_execute(self):
        for name, test_id in runlane.RETIRED_CELLS.items():
            self.assertIsNone(runlane.retired_cell(name, [{"test_ids": [test_id]}]))

    def test_unknown_missing_cell_is_not_suppressed(self):
        self.assertIsNone(runlane.retired_cell("new_cells.typo", []))
