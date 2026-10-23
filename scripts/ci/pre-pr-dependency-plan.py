#!/usr/bin/env python3
"""Print the evaluated NuGet/MSBuild project closure, never only .slnf roots."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--root", type=Path, required=True)
parser.add_argument("--label", default="Build")
parser.add_argument("--configuration", default="Release")
parser.add_argument("graphs", nargs="+", type=Path)
args = parser.parse_args()
projects = set()
for graph_path in args.graphs:
    graph = json.loads(graph_path.read_text())
    if not graph.get("projects"):
        raise SystemExit(f"empty evaluated dependency graph: {graph_path}")
    projects.update(Path(path).resolve() for path in graph["projects"])
root = args.root.resolve()
paths = sorted(path.relative_to(root).as_posix() for path in projects)
tests = [path for path in paths if path.startswith("tests/") and path.endswith("Tests.csproj")]
print(f"{args.label} dependency closure: {len(paths)} projects ({len(tests)} test projects; evaluated {args.configuration} graph)")
for path in paths:
    print(f"   {path}")
