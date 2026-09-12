#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Record mandatory GeoParquet pytest cells; skips are non-passing (#4396).

Usage: check-geoparquet-interop.py REPORT PYTEST_EXIT_CODE PRODUCER_COMMIT
Run from tests/python with GITHUB_STEP_SUMMARY set.
"""

import json
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root = ET.parse(sys.argv[1]).getroot()
cells = list(root.iter("testcase"))
skipped = sum(cell.find("skipped") is not None for cell in cells)
errors = sum(cell.find("error") is not None for cell in cells)
failures = sum(cell.find("failure") is not None for cell in cells)
executed = len(cells) - skipped - errors
passed = executed - failures
receipt = dict(
    suite="geoparquet-interop", commit=sys.argv[3],
    runtime="linux-glibc", tests=len(cells), executed=executed,
    passed=passed, skipped=skipped, failures=failures, errors=errors,
    pytest_exit_code=int(sys.argv[2]),
    passing=(passed >= 5 and not (skipped or errors or failures or int(sys.argv[2]))),
)
summary = json.dumps(receipt, indent=2)
Path("../TestResults/geoparquet-interop.json").write_text(summary + "\n")
with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as output:
    output.write("GeoParquet independent-consumer evidence\n```json\n" + summary + "\n```\n")
print(summary)
if not receipt["passing"]:
    sys.exit("GeoParquet required cells did not all execute and pass (#4396)")
