#!/usr/bin/env python3
"""Partition analyzer compilation roots without changing diagnostic policy."""

import argparse
import json
from pathlib import Path

from solution_projects import solution_projects

SERVER = "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"


def partition(root: Path, source: Path | None, group: str) -> list[str]:
    members = set(solution_projects(root))
    if SERVER not in members:
        raise ValueError("Server test project is missing from the solution")
    projects = members
    if source is not None:
        scope = json.loads(source.read_text())["solution"]
        if Path(scope["path"]).resolve() != (root / "Honua.sln").resolve():
            raise ValueError("Analyzer filter must reference this solution")
        values = scope["projects"]
        if not isinstance(values, list) or any(not isinstance(value, str) for value in values):
            raise ValueError("Analyzer filter must contain project paths")
        projects = {value.replace("\\", "/") for value in values}
        if not projects <= members:
            raise ValueError("Analyzer filter contains unknown solution projects")
    if group == "server":
        return sorted(projects & {SERVER})
    if group == "other":
        return sorted(projects - {SERVER})
    raise ValueError(f"Unknown analyzer root partition: {group}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--filter", type=Path)
    parser.add_argument("--group", choices=["server", "other"], required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    projects = partition(root, args.filter, args.group)
    args.output.write_text(json.dumps({"solution": {"path": str(root / "Honua.sln"),
                                                  "projects": projects}}, indent=2) + "\n")
    print(len(projects))


if __name__ == "__main__":
    main()
