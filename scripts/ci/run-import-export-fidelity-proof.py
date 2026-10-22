#!/usr/bin/env python3
"""Execute imports and decode Honua exports; compare fresh evidence to a reviewed denominator."""
import argparse
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
GDAL_IMAGE = "ghcr.io/osgeo/gdal:ubuntu-full-3.13.1@sha256:aff1d5515aa0e9b50be34ab11d6c0c2cfabc23cdcb7a2e0bc5748101eedb3e4a"
IMPORTS = [
    "ImportFileAsync_ExistingTarget_WithOverwriteTrue_ReplacesLiveRows",
    "ImportFileAsync_WithoutOverwrite_CreatesStagingTableAndSucceeds",
    "ImportFileAsync_GeoPackageWithLocalSrsId_ResolvesEpsgAndTransformsGeometry",
]
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def run(*args, env=None):
    subprocess.run(args, cwd=ROOT, env=env, check=True, timeout=1500)


def read_results(path, expected_count):
    results = ET.parse(path).findall(".//t:UnitTestResult", NS)
    if len(results) != expected_count or any(r.attrib.get("outcome") != "Passed" for r in results):
        raise ValueError(f"Expected {expected_count} executed passing proofs, got "
                         f"{[(r.attrib.get('testName'), r.attrib.get('outcome')) for r in results]}")
    return results


def compare(current, baseline):
    if not current or current != baseline:
        raise ValueError(f"Missing, skipped, or regressed fidelity evidence: {current!r} != {baseline!r}")


def reader_command(directory):
    return ["docker", "run", "--rm", "--network", "none", "--mount",
            f"type=bind,source={directory},target=/proof", "--mount",
            f"type=bind,source={ROOT / 'scripts/ci/decode-vector-export-proof.py'},target=/decode.py,readonly",
            GDAL_IMAGE, "python3", "/decode.py", "/proof"]


def challenge_reader(output):
    # Retiring a tautological policy check requires challenging its replacement:
    # a valid file with a deliberately wrong value must fail for that value.
    with tempfile.TemporaryDirectory(prefix="reader-challenge-", dir=output.parent) as scratch:
        if not Path(scratch).resolve().is_relative_to(output.parent.resolve()):
            raise ValueError("Challenge scratch path escaped the proof directory")
        mutant = Path(scratch) / "artifacts"
        shutil.copytree(output, mutant)
        with closing(sqlite3.connect(mutant / "point/export.gpkg")) as connection:
            updated = connection.execute("UPDATE features SET amount=8 WHERE fid=1")
            if updated.rowcount != 1:
                raise ValueError("Challenge did not modify exactly one feature")
            connection.commit()
        result = subprocess.run(reader_command(mutant), cwd=ROOT, text=True,
                                capture_output=True, timeout=60)
        (output / "rejection-challenge.txt").write_text(result.stdout + result.stderr)
        if result.returncode == 0 or "AssertionError: amount mismatch" not in result.stderr:
            raise ValueError("Independent reader failed to reject deliberate value corruption for the expected reason")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-build", action="store_true", help="Reuse verified current-checkout managed outputs")
    args = parser.parse_args()
    run_id = str(uuid.uuid4())
    output = ROOT / "tests/TestResults/import-export-fidelity" / run_id
    output.mkdir(parents=True, exist_ok=False)
    environment = dict(os.environ, HONUA_VECTOR_PROOF_DIR=str(output))
    common = ["-c", "Release", "-maxcpucount:4", "--results-directory", str(output)]
    if args.no_build:
        common.append("--no-build")
    run("dotnet", "test", "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj", *common,
        "--filter", "FullyQualifiedName~IndependentVectorExportFixtureTests",
        "--logger", "trx;LogFileName=exports.trx", env=environment)
    read_results(output / "exports.trx", 3)
    run("dotnet", "test", "tests/dotnet/Honua.Db.Postgres.Tests/Honua.Postgres.Tests.csproj", *common,
        "--filter", "|".join("FullyQualifiedName~StreamingFileImportStagingTableTests." + name for name in IMPORTS),
        "--logger", "trx;LogFileName=imports.trx", env=environment)
    imported = read_results(output / "imports.trx", len(IMPORTS))
    actual_names = {r.attrib["testName"].rsplit(".", 1)[-1] for r in imported}
    if actual_names != set(IMPORTS):
        raise ValueError(f"Wrong import denominator: {actual_names}")
    run(*reader_command(output))
    decoded = json.loads((output / "independent-reader.json").read_text())
    current = {"imports": sorted(actual_names), "exports": decoded["checks"]}
    baseline = json.loads((ROOT / "scripts/ci/fixtures/import-export-fidelity-baseline.json").read_text())
    compare(current, baseline)
    challenge_reader(output)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    receipt = {"commit": commit, "run_id": run_id, "completed_at": datetime.now(timezone.utc).isoformat(),
               "reader": decoded["reader"], "reader_image": GDAL_IMAGE, "scorecard": current,
               "artifact_sha256": {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
                                   for p in output.rglob("*") if p.is_file()}}
    (output / "scorecard.json").write_text(json.dumps(receipt, indent=2))
    print(f"Fidelity proofs passed; fresh receipt: {output / 'scorecard.json'}")


if __name__ == "__main__":
    main()
