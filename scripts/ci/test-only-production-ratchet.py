#!/usr/bin/env python3
"""Ratchet for production files under src/ that only tests use.

Prints one src/ path per line. A C# file is listed when every type it declares
is unused in production code and at least one test names one of them. A type is
used in production when its name, or the name of an extension method declared in
one of its files, appears in a tracked non-test file outside the type's own
declaration files. Comments are stripped and string literals kept, so a
nameof, a route string or reflection by name still counts as a use. Partial types
are grouped across files.

The architecture test commits the output as a baseline and refuses both a new
path and a baseline line the scanner does not report. Shrink the baseline by
deleting the code (and its tests) or by giving it a production caller.

Usage:
    python3 scripts/ci/test-only-production-ratchet.py
    python3 scripts/ci/test-only-production-ratchet.py --self-test
    python3 scripts/ci/test-only-production-ratchet.py --write tests/dotnet/Honua.Architecture.Tests/test-only-production-baseline.txt
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
import tempfile
from collections import defaultdict
from pathlib import Path

PRODUCTION_SUFFIXES = (
    ".cs", ".razor", ".cshtml", ".csproj", ".props", ".targets", ".json", ".xml", ".yaml", ".yml",
    ".sh", ".py", ".ps1", ".config", ".resx", ".sql",
)
TEST_SUFFIXES = (".cs", ".razor", ".json", ".csproj")
CODE_SUFFIXES = (".cs", ".razor", ".cshtml")
COMMENT_OR_STRING = re.compile(r'"(?:\\.|[^"\\\n])*"|//[^\n]*|/\*.*?\*/', re.S)
TYPE = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|file|unsafe|ref|new)\s+)*"
    r"(?:class|record(?:\s+(?:struct|class))?|struct|interface|enum)\s+([A-Z]\w*)",
    re.M,
)
EXTENSION = re.compile(r"\b([A-Z]\w*)\s*(?:<[^>]*>)?\s*\(\s*this\s")
WORD = re.compile(r"[A-Za-z_]\w*")


def is_test(path: str) -> bool:
    return (
        path.startswith("tests/")
        or path.startswith("benchmarks/")
        or "/Tests/" in path
        or ".Tests/" in path
        or ".Benchmarks" in path
    )


def read(root: Path, path: str) -> str:
    try:
        text = (root / path).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""
    if not path.endswith(CODE_SUFFIXES):
        return text
    return COMMENT_OR_STRING.sub(lambda match: match.group(0) if match.group(0).startswith('"') else " ", text)


def declarations(root: Path, sources: list[str]) -> tuple[dict[str, set[str]], dict[str, set[str]]]:
    types_by_file: dict[str, set[str]] = {}
    extensions_by_file: dict[str, set[str]] = {}
    for path in sources:
        text = read(root, path)
        names = set(TYPE.findall(text))
        if names:
            types_by_file[path] = names
            extensions_by_file[path] = set(EXTENSION.findall(text))
    return types_by_file, extensions_by_file


def word_index(root: Path, paths: list[str]) -> dict[str, set[str]]:
    index: dict[str, set[str]] = defaultdict(set)
    for path in paths:
        for word in set(WORD.findall(read(root, path))):
            index[word].add(path)
    return index


def scan(root: Path, files: list[str]) -> list[str]:
    production = [f for f in files if f.endswith(PRODUCTION_SUFFIXES) and not is_test(f) and not f.startswith("docs/")]
    tests = [f for f in files if f.endswith(TEST_SUFFIXES) and is_test(f)]
    sources = [f for f in files if f.startswith("src/") and f.endswith(".cs") and not is_test(f)]

    types_by_file, extensions_by_file = declarations(root, sources)
    production_index = word_index(root, production)
    test_words = word_index(root, tests)
    declared_in: dict[str, set[str]] = defaultdict(set)
    for path, names in types_by_file.items():
        for name in names:
            declared_in[name].add(path)

    def used(name: str) -> bool:
        own = declared_in[name]
        if production_index.get(name, set()) - own:
            return True
        return any(
            production_index.get(extension, set()) - own
            for path in own
            for extension in extensions_by_file.get(path, ())
        )

    usage = {name: used(name) for name in declared_in}
    return sorted(
        path
        for path, names in types_by_file.items()
        if not any(usage[name] for name in names) and any(name in test_words for name in names)
    )


def tracked_files(root: Path) -> list[str]:
    result = subprocess.run(["git", "ls-files"], cwd=root, capture_output=True, text=True, check=True)
    return [line for line in result.stdout.splitlines() if line]


def self_test() -> int:
    sample = {
        "src/A/OnlyTests.cs": "namespace A;\npublic sealed class OnlyTests { }\n",
        "src/A/Used.cs": "namespace A;\npublic sealed class Used { }\n",
        "src/A/Caller.cs": "namespace A;\n// OnlyTests is mentioned in a comment only.\ninternal static class Caller { static object M() => new Used(); }\n",
        "src/A/Endpoints.cs": "namespace A;\ninternal static class Endpoints { public static object MapThings(this object e) => e; }\n",
        "src/A/Program.cs": "namespace A;\ninternal static class Program { static void Main() => new object().MapThings(); }\n",
        "src/A/Split.Part1.cs": "namespace A;\npublic partial class Split { }\n",
        "src/A/Split.Part2.cs": "namespace A;\npublic partial class Split { }\n",
        "src/A/Host.cs": "namespace A;\ninternal static class Host { static object M() => new Split(); }\n",
        "src/A/AfterUrl.cs": "namespace A;\npublic sealed class AfterUrl { }\n",
        "src/A/Link.cs": "namespace A;\ninternal static class Link { static object M() => new[] { (object)\"https://example.test\", new AfterUrl() }; }\n",
        "tests/A.Tests/T.cs": "class T { object a = new OnlyTests(); object b = new Used(); object c = new Split(); object d = typeof(Endpoints); object e = new AfterUrl(); }\n",
    }
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        for path, text in sample.items():
            (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / path).write_text(text, encoding="utf-8")
        found = scan(root, sorted(sample))
    expected = ["src/A/OnlyTests.cs"]
    if found != expected:
        print(f"self-test: expected {expected}, found {found}", file=sys.stderr)
        return 1
    print("self-test ok", file=sys.stderr)
    return 0


HEADER = """# Generated by scripts/ci/test-only-production-ratchet.py. Do not add lines.
# Each line is a src/ file whose declared types only tests use. Delete a line
# only when the file is gone or production calls it. The architecture test
# rejects a new line against trunk.
"""


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--write", type=Path, help="Write the baseline file here.")
    parser.add_argument("--root", type=Path, default=None)
    args = parser.parse_args(argv)
    if args.self_test:
        return self_test()
    root = args.root or Path(__file__).resolve().parents[2]
    paths = scan(root, tracked_files(root))
    if args.write:
        args.write.write_text(HEADER + "".join(f"{path}\n" for path in paths), encoding="utf-8")
        print(f"wrote {len(paths)} paths to {args.write}", file=sys.stderr)
        return 0
    sys.stdout.write("".join(f"{path}\n" for path in paths))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
