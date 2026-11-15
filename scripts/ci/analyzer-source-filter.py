#!/usr/bin/env python3
"""Select evaluated source or test compilation roots, retaining forward dependencies."""

import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


from solution_projects import solution_projects


def evaluated_ownership(root: Path, projects: list[str]) -> dict[str, tuple[str, str]]:
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
                      Lines="@(Ownership->'%(Identity)|%(TestOnly)|%(TestRoot)')")
        traversal = temporary / "ownership.proj"
        ET.ElementTree(probe).write(traversal, encoding="unicode")
        subprocess.run(["dotnet", "msbuild", str(traversal), "-t:EvaluateOwnership",
                        "-maxCpuCount:5", "-verbosity:quiet", "-nologo"], check=True)
        evaluated = {}
        for line in output.read_text().splitlines():
            path, test_only, test_root = line.rsplit("|", 2)
            project = Path(path).resolve().relative_to(root).as_posix()
            if project in evaluated or test_only not in {"", "true"} or test_root not in {"", "true"}:
                raise ValueError(f"Invalid analyzer ownership: {line}")
            evaluated[project] = (test_only, test_root)
        if set(evaluated) != set(projects):
            raise ValueError("Analyzer ownership did not evaluate every selected project")
        return evaluated


def source_roots(root: Path, projects: list[str]) -> list[str]:
    return sorted(project for project, (test_only, _) in evaluated_ownership(root, projects).items()
                  if test_only != "true")


def test_roots(root: Path, projects: list[str]) -> list[str]:
    return sorted(project for project, (_, test_root) in evaluated_ownership(root, projects).items()
                  if test_root == "true")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--kind", choices=["source", "tests"], default="source")
    parser.add_argument("--filter", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    members = solution_projects(root)
    projects = sorted(members)
    if args.filter is not None:
        scope = json.loads(args.filter.read_text())["solution"]
        values = scope["projects"]
        if Path(scope["path"]).resolve() != root / "Honua.sln" or not isinstance(values, list):
            raise ValueError("Analyzer filter must reference this solution and project list")
        if any(not isinstance(value, str) or value.replace("\\", "/") not in members for value in values):
            raise ValueError("Analyzer filter contains unknown solution projects")
        projects = sorted({value.replace("\\", "/") for value in values})
    select = source_roots if args.kind == "source" else test_roots
    roots = select(root, projects) if projects else []
    args.output.write_text(json.dumps({"solution": {"path": str(root / "Honua.sln"),
                                                  "projects": [members[p] for p in roots]}}, indent=2) + "\n")
    print(f"{args.kind.title()} analyzer compilation: {len(roots)} of {len(projects)} selected roots; dependencies retained.")


if __name__ == "__main__":
    main()
