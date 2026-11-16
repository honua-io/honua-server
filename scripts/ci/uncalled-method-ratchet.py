#!/usr/bin/env python3
"""Ratchet for methods under src/ that nothing calls.

A method is uncalled when its name occurs exactly once across the repository's
code and configuration text (C#, Razor, JSON, YAML, XML, MSBuild, scripts) after
C# comments are removed: that one occurrence is its declaration. String literals
stay, so a name used through reflection or configuration still counts as used.
Overrides, framework entry points and non-private members of types that derive
from or implement a type not declared under src/ (framework interfaces, SignalR
hubs, gRPC interceptors) are skipped, and the CLI template sources
under src/Honua.Geoprocessing/Cli/Templates are content, not product code.
Prints one "path::Name" entry per line; the architecture test requires no output.

Usage:
    python3 scripts/ci/uncalled-method-ratchet.py
    python3 scripts/ci/uncalled-method-ratchet.py --self-test
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
import tempfile
from collections import Counter
from collections.abc import Callable, Iterable
from pathlib import Path

TEXT_SUFFIXES = (
    ".cs", ".razor", ".cshtml", ".json", ".xml", ".yaml", ".yml", ".props", ".targets",
    ".csproj", ".ps1", ".sh", ".py", ".ts", ".js",
)
EXCLUDED_PREFIXES = ("docs/", "src/Honua.Geoprocessing/Cli/Templates/")
FRAMEWORK_NAMES = {
    "Main", "Dispose", "DisposeAsync", "ToString", "Equals", "GetHashCode", "Configure", "ConfigureServices",
    "Deconstruct", "GetEnumerator", "GetAsyncEnumerator", "GetAwaiter", "GetPinnableReference",
}
TYPE_HEADER = re.compile(r"(?P<partial>\bpartial\s+)?\b(?:class|struct|record)\s+(?P<name>\w+)(?P<generic><[^>{]*>)?(?:\((?:[^()]|\([^()]*\))*\))?\s*(?::\s*(?P<bases>[^{;]+?)\s*)?(?:where\b[^{]*)?\{")
NAMESPACE = re.compile(r"\bnamespace\s+([\w.]+)\s*([;{])")
DECLARED_TYPE = re.compile(r"\b(?:class|struct|record|interface|enum)\s+([A-Z]\w*)")
WORD = re.compile(r"\b[A-Za-z_]\w*\b")
DECLARATION = re.compile(
    r"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*"
    r"(?P<modifiers>(?:(?:public|internal|private|protected|static|async|virtual|sealed|partial|new|unsafe|extern|override)\s+)+)"
    r"[\w<>\[\],.?() ]+?\s+(?P<name>[A-Z]\w*)\s*(?:<[^>()]*>)?\s*\(",
    re.M,
)

def tracked_files(root: Path) -> list[str]:
    result = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], capture_output=True, text=True, check=True)
    return [path for path in result.stdout.split("\0") if path.endswith(TEXT_SUFFIXES) and not path.startswith(EXCLUDED_PREFIXES)]


def quoted_literal_end(text: str, start: int, quote: str) -> int:
    index = start + 1
    while index < len(text) and text[index] != quote:
        index += 2 if text[index] == '\\' else 1
    return index + 1


def verbatim_literal_end(text: str, start: int) -> int:
    index = start + 1
    while index < len(text):
        if text[index] == '"':
            if text.startswith('""', index):
                index += 2
                continue
            break
        index += 1
    return index + 1


def interpolated_literal_end(text: str, start: int) -> int:
    index, depth = start + 1, 0
    while index < len(text):
        character = text[index]
        if character == '\\':
            index += 2
            continue
        if depth == 0 and (text.startswith('{{', index) or text.startswith('}}', index)):
            index += 2
            continue
        if depth and character in '\'\"$@':
            end = quoted_literal_end(text, index, "'") if character == "'" else string_literal_end(text, index)
            if end is not None:
                index = end
                continue
        if character == '{':
            depth += 1
        elif character == '}' and depth:
            depth -= 1
        if character == '"' and depth == 0:
            break
        index += 1
    return index + 1


def string_literal_end(text: str, start: int) -> int | None:
    quote_index = start
    while quote_index < len(text) and text[quote_index] in '$@':
        quote_index += 1
    if quote_index == len(text) or text[quote_index] != '"':
        return None
    prefix = text[start:quote_index]
    if text.startswith('"""', quote_index):
        quote_count = 3
        while text.startswith('"', quote_index + quote_count):
            quote_count += 1
        end = text.find('"' * quote_count, quote_index + quote_count)
        return len(text) if end == -1 else end + quote_count
    if '@' in prefix:
        return verbatim_literal_end(text, quote_index)
    if '$' in prefix:
        return interpolated_literal_end(text, quote_index)
    return quoted_literal_end(text, quote_index, '"')


def strip_comments(s: str) -> str:
    return transform_csharp(s, str)


def mask_literals(s: str) -> str:
    return transform_csharp(s, lambda literal: re.sub(r"[^\r\n]", " ", literal))


def transform_csharp(s: str, transform_literal: Callable[[str], str]) -> str:
    """Remove comments and transform literals before scanning C# source."""
    out, i, n = [], 0, len(s)
    while i < n:
        c = s[i]
        if s.startswith('//', i):
            j = s.find('\n', i)
            i = n if j == -1 else j
            continue
        if s.startswith('/*', i):
            j = s.find('*/', i + 2)
            i = n if j == -1 else j + 2
            out.append(' ')
            continue
        end = None
        if c == "'":
            end = quoted_literal_end(s, i, "'")
        elif c in '$@"':
            end = string_literal_end(s, i)
        if end is not None:
            literal = s[i:end]
            out.append(transform_literal(literal))
            i = end
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def closing_brace(text: str, opening: int) -> int:
    depth = 0
    for index in range(opening, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return index
    return len(text)


def type_declarations(text: str) -> list[tuple[int, int, str, bool, list[str]]]:
    text = mask_literals(text)
    namespaces = [(match.start(), len(text) if match.group(2) == ";" else closing_brace(text, match.end() - 1),
                   match.group(1)) for match in NAMESPACE.finditer(text)]
    declarations = []
    for header in TYPE_HEADER.finditer(text):
        opening = header.end() - 1
        generic = header.group("generic")
        name = header.group("name") + (f"`{generic.count(',') + 1}" if generic else "")
        namespace = [name for start, end, name in namespaces if start < opening < end]
        parents = [identity.rsplit(".", 1)[-1] for start, end, identity, _, _ in declarations
                   if start < opening < end]
        identity = ".".join(namespace + parents + [name])
        bases = [part.strip().split("<")[0].split(".")[-1].split("(")[0].strip()
                 for part in re.split(r",(?![^<]*>)", header.group("bases") or "")]
        declarations.append((opening, closing_brace(text, opening), identity, bool(header.group("partial")), bases))
    return declarations


def type_spans(text: str, partial_bases: dict[str, list[str]] | None = None) -> list[tuple[int, int, list[str]]]:
    return [(start, end, (partial_bases or {}).get(identity, bases) if partial else bases)
            for start, end, identity, partial, bases in type_declarations(text)]


def has_external_base(spans: list[tuple[int, int, list[str]]], position: int, declared: set[str]) -> bool:
    inner = None
    for start, end, bases in spans:
        if start < position < end and (inner is None or start > inner[0]):
            inner = (start, end, bases)
    return inner is not None and any(base and base not in declared for base in inner[2])


def collect_partial_bases(sources: Iterable[str]) -> dict[str, list[str]]:
    partial_bases: dict[str, list[str]] = {}
    for text in sources:
        for _, _, identity, partial, bases in type_declarations(text):
            if partial:
                partial_bases.setdefault(identity, []).extend(bases)
    return partial_bases


def scan(root: Path) -> list[str]:
    words: Counter[str] = Counter()
    sources: dict[str, str] = {}
    for relative in tracked_files(root):
        path = root / relative
        if not path.is_file():
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        if relative.endswith(".cs"):
            text = strip_comments(text)
            if relative.startswith("src/"):
                sources[relative] = text
        words.update(WORD.findall(text))

    declared = {name for text in sources.values() for name in DECLARED_TYPE.findall(text)}
    partial_bases = collect_partial_bases(sources.values())
    entries = set()
    for relative, text in sources.items():
        spans = None
        for match in DECLARATION.finditer(text):
            name = match.group("name")
            modifiers = match.group("modifiers").split()
            if name in FRAMEWORK_NAMES or "override" in modifiers or words[name] != 1:
                continue
            if "private" not in modifiers and any(m in modifiers for m in ("public", "protected", "internal")):
                spans = spans if spans is not None else type_spans(text, partial_bases)
                if has_external_base(spans, match.start(), declared):
                    continue
            entries.add(f"{relative}::{name}")
    return sorted(entries)


def literal_boundaries_pass() -> bool:
    for literal in ('"}"', "'}'", '@"}"', '$"}}"', '"""}"""', '$"{string.Join("}", Array.Empty<string>())}"'):
        source = f"class Writer : IBufferWriter<byte> {{ object value = {literal}; void Advance() {{ }} }}"
        if not has_external_base(type_spans(source), source.index("void Advance"), set()):
            print(f"self-test failed: literal {literal} truncated the containing type", file=sys.stderr)
            return False
    return True


def write_sample_fixture(root: Path) -> None:
    (root / "src" / "Sample.cs").write_text(
        "public class Sample\n{\n"
        "    public void Used() { }\n"
        "    public void Unused() { }\n"
        "    // Commented() is only mentioned in a comment\n"
        "    public void Commented() { }\n"
        "    public void Reflected() { }\n"
        "    public override string ToString() => \"\";\n"
        "    private void Caller() { Used(); var name = \"Reflected\"; }\n"
        "    private void Masked() { var media = \"text/*\"; }\n"
        "    private void MaskedCaller() { Masked(); var closing = \"*/\"; }\n"
        "    private void Escaped() { var text = $\"a {{ b\"; var media = \"text/*\"; }\n"
        "    private void EscapedCaller() { Escaped(); var closing = \"*/\"; }\n"
        "}\n"
        "public class Writer : System.Buffers.IBufferWriter<byte>\n{\n"
        "    private const string ClosingBrace = \"}\";\n"
        "    public void Advance(int count) { }\n"
        "    private void Helper() { }\n"
        "}\n",
        encoding="utf-8",
    )


def write_partial_fixture(root: Path) -> None:
    (root / "src" / "PartialBase.cs").write_text(
        "namespace Streams; public partial class PartialWriter : System.Buffers.IBufferWriter<byte> { }",
        encoding="utf-8",
    )
    (root / "src" / "PartialMethods.cs").write_text(
        "namespace Streams; public partial class PartialWriter {\n"
        "    public void CompleteBuffer() { }\n"
        "    private void PartialHelper() { }\n}\n",
        encoding="utf-8",
    )
    (root / "src" / "Unrelated.cs").write_text(
        "namespace Other { public partial class PartialWriter {\n"
        "    public void UnrelatedMethod() { }\n} }\n",
        encoding="utf-8",
    )


def self_test() -> int:
    if not literal_boundaries_pass():
        return 1
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        (root / "src").mkdir()
        write_sample_fixture(root)
        write_partial_fixture(root)
        subprocess.run(["git", "init", "-q", str(root)], check=True)
        subprocess.run(["git", "-C", str(root), "add", "."], check=True)
        actual = scan(root)
    expected = [
        "src/PartialMethods.cs::PartialHelper",
        "src/Sample.cs::Caller", "src/Sample.cs::Commented", "src/Sample.cs::EscapedCaller", "src/Sample.cs::Helper",
        "src/Sample.cs::MaskedCaller", "src/Sample.cs::Unused", "src/Unrelated.cs::UnrelatedMethod",
    ]
    if actual != expected:
        print(f"self-test failed: expected {expected}, got {actual}", file=sys.stderr)
        return 1
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--root", default=".")
    arguments = parser.parse_args()
    if arguments.self_test:
        return self_test()
    print("\n".join(scan(Path(arguments.root).resolve())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
