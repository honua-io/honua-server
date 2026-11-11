#!/usr/bin/env python3
"""Compile every source root without compiling test-only roots."""

import argparse
import json
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def source_roots(root: Path, projects: list[str]) -> list[str]:
    with tempfile.TemporaryDirectory(prefix="honua-analyzer-ownership-") as directory:
        temporary = Path(directory)
        output = temporary / "ownership.txt"
        probe = ET.Element("Project")
        items = ET.SubElement(probe, "ItemGroup")
        for project in projects:
            ET.SubElement(items, "Candidate", Include=str(root / project))
        target = ET.SubElement(probe, "Target", Name="EvaluateOwnership")
        task = ET.SubElement(target, "MSBuild", Projects="@(Candidate)",
                             Targets="HonuaGetAnalyzerOwnership", BuildInParallel="true",
                             Properties="Configuration=Release")
        ET.SubElement(task, "Output", TaskParameter="TargetOutputs", ItemName="Ownership")
        ET.SubElement(target, "WriteLinesToFile", File=str(output), Overwrite="true",
                      Lines="@(Ownership->'%(Identity)|%(TestOnly)')")
        traversal = temporary / "ownership.proj"
        ET.ElementTree(probe).write(traversal, encoding="unicode")
        subprocess.run(["dotnet", "msbuild", str(traversal), "-t:EvaluateOwnership",
                        "-maxCpuCount:5", "-verbosity:quiet", "-nologo"], check=True)
        evaluated = {}
        for line in output.read_text().splitlines():
            path, test_only = line.rsplit("|", 1)
            project = Path(path).resolve().relative_to(root).as_posix()
            if project in evaluated or test_only not in {"", "true"}:
                raise ValueError(f"Invalid analyzer ownership: {line}")
            evaluated[project] = test_only
        if set(evaluated) != set(projects):
            raise ValueError("Analyzer ownership did not evaluate every selected project")
        return sorted(project for project, test_only in evaluated.items() if test_only != "true")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    members = {path.replace("\\", "/"): path for path in re.findall(
        r'^Project\([^\n]+? = "[^"]+", "([^"]+\.csproj)"',
        (root / "Honua.sln").read_text(), re.MULTILINE)}
    projects = sorted(members)
    roots = source_roots(root, projects) if projects else []
    args.output.write_text(json.dumps({"solution": {"path": str(root / "Honua.sln"),
                                                  "projects": [members[p] for p in roots]}}, indent=2) + "\n")
    print(f"Source analyzer compilation: {len(roots)} of {len(projects)} selected roots; dependencies retained.")


if __name__ == "__main__":
    main()
