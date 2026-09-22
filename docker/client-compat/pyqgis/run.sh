#!/usr/bin/env bash
# Runs the PyQGIS client compatibility suite against a running honua service
# and copies the generated .cert.json envelopes into /output.
set -euo pipefail

: "${HONUA_BASE_URL:=http://honua:5000}"
: "${HONUA_PYQGIS_SERVICE_ID:=test_service}"
: "${HONUA_PYQGIS_COLLECTION_ID:=0}"

cd /workspace

for attempt in 1 2 3 4 5 6 7 8; do
    if curl -fsS "${HONUA_BASE_URL}/healthz/live" >/dev/null 2>&1; then
        break
    fi
    sleep 5
done

mkdir -p /output

# tests/python/pyqgis/conftest.py honors HONUA_PYQGIS_OUTPUT_DIR and writes
# .cert.json envelopes there directly, so the lane never needs to write into
# the read-only tests/ bind mount.
export HONUA_PYQGIS_BASE_URL="${HONUA_BASE_URL}"
export HONUA_PYQGIS_SERVICE_ID
export HONUA_PYQGIS_COLLECTION_ID
export HONUA_PYQGIS_REQUIRE_WFS=1
export HONUA_PYQGIS_OUTPUT_DIR=/output
export QT_QPA_PLATFORM=offscreen

report=/output/pyqgis-junit.xml
rm -f "$report"

set +e
xvfb-run -a pytest tests/python/pyqgis \
    -m pyqgis \
    --tb=short \
    -v \
    --junitxml="$report"
status=$?
set -e

# QGIS segfaults inside QgsApplication.exitQgis() during interpreter teardown,
# so a fully green run still exits 139 - reproducible with any single module
# that builds a provider, and independent of this server. That is why this lane
# used to end in `|| true`, which also swallowed real failures: the WCS lane
# once reported exit=0 with 6 of 7 cases failing.
#
# Decide from the JUnit report instead. pytest writes it before teardown, so it
# survives the segfault, and it distinguishes "all green then crashed on exit"
# from "tests failed". Only 139 is forgiven, and only when the report is clean.
if [ ! -s "$report" ]; then
    echo "PyQGIS lane FAILED: pytest wrote no JUnit report (exit ${status})." >&2
    exit 1
fi

python3 - "$report" "$status" <<'PY'
import sys
import xml.etree.ElementTree as ET

report, status = sys.argv[1], int(sys.argv[2])
root = ET.parse(report).getroot()
suites = root.iter("testsuite") if root.tag == "testsuites" else [root]

tests = failures = errors = skipped = 0
for suite in suites:
    tests += int(suite.get("tests", 0))
    failures += int(suite.get("failures", 0))
    errors += int(suite.get("errors", 0))
    skipped += int(suite.get("skipped", 0))

print(f"PyQGIS lane: {tests} tests, {failures} failed, {errors} errors, "
      f"{skipped} skipped (pytest exit {status}).")

if tests == 0:
    sys.exit("PyQGIS lane FAILED: collected no tests.")
if failures or errors:
    sys.exit(f"PyQGIS lane FAILED: {failures} failed, {errors} errors.")
if status not in (0, 139):
    sys.exit(f"PyQGIS lane FAILED: every test passed but pytest exited {status}, "
             "which is neither success nor the known exitQgis teardown crash.")
PY

echo "PyQGIS lane complete; output written to /output."
