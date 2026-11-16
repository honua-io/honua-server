#!/usr/bin/env python3
"""Build one GP scoped-key test project once, run each contract case, and prove each case executed."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import NamedTuple
import xml.etree.ElementTree as ET

NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
SOURCE_CASE = re.compile(r"\[((?:Unit|Integration)(?:Test|Theory))\](.*?)public async Task (\w+)\(", re.S)


class ContractError(AssertionError):
    """A configured GP scoped-key regression did not execute and pass."""


class Project(NamedTuple):
    """One built test project, its PostgreSQL connection and its TRX directory."""
    path: str
    database_url: str
    results: Path


def require(condition, detail) -> None:
    # An explicit raise holds under python -O, which strips assert statements.
    if not condition:
        raise ContractError(detail)


def passed(results: list) -> bool:
    return all(result.attrib["outcome"] == "Passed" for result in results)


def by_method(results: list, methods: dict, name: str) -> list:
    return [result for result in results if methods[result.attrib["testId"]]["name"] == name]


def check(trx: Path, case: dict) -> dict:
    """Every source-derived method and theory row, required class and named method ran and passed."""
    source = case.get("source", "")
    cases = SOURCE_CASE.findall(Path(source).read_text()) if source else []
    required = [name for name in case.get("required_classes", "").split(",") if name]
    require(cases or required, "No source cases or required classes configured")
    root = ET.parse(trx).getroot()
    counters = root.find("t:ResultSummary/t:Counters", NAMESPACE).attrib
    require(int(counters["executed"]) > 0 and counters["failed"] == "0", counters)
    methods = {test.attrib["id"]: test.find("t:TestMethod", NAMESPACE).attrib
               for test in root.findall("t:TestDefinitions/t:UnitTest", NAMESPACE)}
    results = root.findall("t:Results/t:UnitTestResult", NAMESPACE)
    for class_name in required:
        actual = [result for result in results
                  if methods[result.attrib["testId"]]["className"].split(".")[-1] == class_name]
        require(actual and passed(actual), (class_name, len(actual)))
    for kind, attributes, method in cases:
        require(method.startswith(case.get("prefix", "")), method)
        expected = len(re.findall(r"\[InlineData\(", attributes)) if kind.endswith("Theory") else 1
        actual = by_method(results, methods, method)
        require(len(actual) == expected and passed(actual), (method, expected, len(actual)))
    additional = case.get("additional_method", "")
    if additional:
        actual = by_method(results, methods, additional)
        require(len(actual) == 1 and passed(actual), (additional, len(actual)))
    return counters


def run_case(project: Project, case: dict) -> subprocess.CompletedProcess:
    # Cases that never used PostgreSQL keep an empty connection string, as their own job did.
    database_url = project.database_url if case.get("needs_postgres", True) else ""
    return subprocess.run(["dotnet", "test", project.path, "--configuration", "Release", "--no-build",
                           "--filter", case["filter"], "--logger", f"trx;LogFileName={case['name']}.trx",
                           "--results-directory", str(project.results)],
                          env=dict(os.environ, HONUA_TEST_DB_URL=database_url), check=False)


def verify(project: Project, case: dict) -> str | None:
    """Return the failure for one case, or None when it executed and passed."""
    execution = run_case(project, case)
    try:
        require(execution.returncode == 0, f"dotnet test exited {execution.returncode}")
        print("Actual GP key contract counters:", check(project.results / f"{case['name']}.trx", case))
    except (ContractError, OSError, KeyError, AttributeError, TypeError, ValueError, ET.ParseError) as error:
        return str(error)
    return None


def validate_cases(cases) -> None:
    """A job must name at least one case, and each case its own TRX file."""
    require(isinstance(cases, list) and cases, "No GP scoped-key cases configured")
    names = [case.get("name") for case in cases]
    require(all(names) and len(set(names)) == len(names), f"Case names must be present and unique: {names}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    parser.add_argument("--cases", required=True, help="JSON list of case objects")
    parser.add_argument("--results", type=Path, required=True)
    args = parser.parse_args()
    cases = json.loads(args.cases)
    try:
        validate_cases(cases)
    except ContractError as error:
        print(f"::error::{error}", flush=True)
        return 1
    project = Project(args.project, os.environ.get("HONUA_GP_DB_URL", ""), args.results)
    subprocess.run(["dotnet", "build", project.path, "--configuration", "Release", "-m:1"], check=True)
    failures = []
    for case in cases:
        print(f"::group::{case['name']}", flush=True)
        failure = verify(project, case)
        print("::endgroup::", flush=True)
        if failure:
            failures.append(case["name"])
            print(f"::error::{case['name']}: {failure}", flush=True)
    if failures:
        print("GP scoped-key cases that did not execute and pass: " + ", ".join(failures), file=sys.stderr)
        return 1
    print("Every new canonical method and all source-derived theory cases executed and passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
