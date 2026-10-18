#!/usr/bin/env python3
"""Qualify the real installed devkit inside a previously prepared workspace image.

Run this script under with-build-lock. Acquisition/image preparation are separate;
selected editable source never runs outside the network-denied sandbox.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import threading
import time

from workspace import read_regular, run


def qualify(image: str, devkit: Path, dotnet: str, root: Path) -> dict:
    root.mkdir(parents=True, exist_ok=False)
    evidence = []
    for language, relative in (("python", "process/summary_tool.py"), ("dotnet", "process/SummaryTool.cs")):
        subprocess.run([dotnet, str(devkit.resolve()), "init", "--language", language,
                        "--name", f"summary-{language}", "--output", str(root.resolve())], check=True, timeout=120)
        project = root / f"summary-{language}"
        source = project / relative
        original = read_regular(source, 262144)
        fixture = read_regular(project / "fixtures/input.geojson", 262144)
        source.write_bytes(original + (b"\nif invalid syntax\n" if language == "python" else b"\nthis is invalid C#;\n"))
        failed = run(project, image, root / "runs", 300)
        assert failed["status"] == "failed" and not failed["artifacts"], failed
        assert (("SyntaxError" in failed["diagnostics"] or "invalid syntax" in failed["diagnostics"]) if language == "python" else "error CS" in failed["diagnostics"]), failed
        retained = root / "runs" / failed["runId"] / "snapshot"
        failed_source = read_regular(retained / relative, 262144)
        if language == "python":
            marker = b'    context.output.add_artifact("summary.json", path)'
            replacement = marker + b'\n    exported = context.workdir / "features.geojson"\n    exported.write_bytes(context.inputs["features"].read_bytes())\n    context.output.add_artifact("features.geojson", exported)'
        else:
            marker = b'        context.Output.AddArtifact("summary.json", path);'
            replacement = marker + b'\n        var exported = Path.Join(context.WorkDirectory, "features.geojson");\n        File.Copy(context.Inputs["features"], exported);\n        context.Output.AddArtifact("features.geojson", exported);'
        corrected_source = original.replace(marker, replacement)
        assert corrected_source != original, "Canonical template registration marker was not found."
        source.write_bytes(corrected_source)
        corrected = run(project, image, root / "runs", 300)
        assert corrected["status"] == "succeeded" and corrected["artifacts"], corrected
        assert failed["snapshotDigest"] != corrected["snapshotDigest"]
        assert read_regular(retained / relative, 262144) == failed_source
        assert read_regular(retained / "fixtures/input.geojson", 262144) == fixture
        expected = json.loads((project / "expected/summary.json").read_text())
        matches = [item for item in corrected["artifacts"] if item["path"].endswith("/artifacts/summary.json")]
        assert len(matches) == 1, corrected
        artifact = root / "runs" / corrected["runId"] / "output" / matches[0]["path"]
        data = read_regular(artifact, 262144)
        assert json.loads(data) == expected and hashlib.sha256(data).hexdigest() == matches[0]["sha256"]
        geospatial = [item for item in corrected["artifacts"] if item["path"].endswith("/artifacts/features.geojson")]
        assert len(geospatial) == 1, corrected
        geospatial_path = root / "runs" / corrected["runId"] / "output" / geospatial[0]["path"]
        assert read_regular(geospatial_path, 262144) == fixture
        # Cancellation also interrupts dependency restoration/build; it does not
        # wait for the author's process to cooperate with a language signal.
        if language == "python":
            source.write_bytes(original.replace(b"def execute(context: GpContext) -> GpResult:\n", b"def execute(context: GpContext) -> GpResult:\n    import time\n    time.sleep(90)\n"))
        else:
            source.write_bytes(original.replace(b"public sealed class SummaryTool : IGeoprocessingTool\n{",
                b"public sealed class SummaryTool : IGeoprocessingTool\n{\n    static SummaryTool() { System.Threading.Thread.Sleep(90000); }"))
        cancelled = threading.Event()
        timer = threading.Timer(2, cancelled.set)
        timer.start()
        started = time.monotonic()
        try:
            cancellation = run(project, image, root / "runs", 300, cancelled)
        finally:
            timer.cancel()
            source.write_bytes(original)
        assert cancellation["status"] == "cancelled" and not cancellation["artifacts"], cancellation
        assert time.monotonic() - started < 30, cancellation
        evidence.append({"language": language, "failed": failed, "corrected": corrected, "cancelled": cancellation,
                         "geoJsonArtifact": {**geospatial[0], "absolutePath": str(geospatial_path.resolve()), "sourceSnapshotDigest": corrected["snapshotDigest"]}})
    summary = {"image": image, "devkitSha256": hashlib.sha256(read_regular(devkit, 128 * 1024 * 1024)).hexdigest(), "runs": evidence}
    (root / "qualification.json").write_text(json.dumps(summary, indent=2))
    return summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--devkit", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--root", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(qualify(args.image, args.devkit, args.dotnet, args.root)))
