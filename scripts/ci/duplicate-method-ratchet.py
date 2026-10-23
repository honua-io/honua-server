#!/usr/bin/env python3
"""Ratchet for same-name near-duplicate methods under src/.

Prints one pair id per line. A pair is two block-bodied methods that share a
name, are each at least 25 normalized lines, and whose normalized bodies have
a line-level similarity of at least 0.92. Comments and blank lines are
removed before the comparison, so a copy that differs only by commentary
still pairs. String literals stay, so a one-line message change still
compares as a near-copy instead of disappearing.

The architecture test commits the output as a baseline and refuses both new
pairs and a baseline that lists a pair the scanner no longer finds. Shrink
the baseline only by deleting the duplicated method.

Usage:
    python3 scripts/ci/duplicate-method-ratchet.py
    python3 scripts/ci/duplicate-method-ratchet.py --self-test
    python3 scripts/ci/duplicate-method-ratchet.py --write tests/dotnet/Honua.Architecture.Tests/duplicate-method-baseline.txt
"""
from __future__ import annotations

import argparse
import difflib
import sys
from pathlib import Path

MIN_LINES = 25
MIN_RATIO = 0.92
# 2 * min / (min + max) >= MIN_RATIO implies min/max >= MIN_RATIO / (2 - MIN_RATIO).
MIN_LENGTH_RATIO = MIN_RATIO / (2.0 - MIN_RATIO)

KEYWORDS = {
    "if", "for", "foreach", "while", "using", "switch", "catch", "lock", "fixed",
    "when", "else", "do", "try", "finally", "delegate", "new", "return", "throw",
    "sizeof", "typeof", "nameof", "checked", "unchecked", "default", "stackalloc",
    "get", "set", "init", "add", "remove", "operator", "where", "and", "or", "not",
    "is", "as", "await", "yield", "base", "this", "var", "void", "class", "struct",
    "record", "interface", "enum", "namespace", "event", "out", "ref", "in",
}

MODIFIERS = {
    "public", "private", "protected", "internal", "static", "async", "sealed",
    "override", "virtual", "unsafe", "new", "partial", "extern", "required",
    "file", "readonly", "volatile", "abstract",
}


def blank_range(chars: list[str], start: int, end: int) -> None:
    for index in range(start, end):
        if chars[index] != "\n":
            chars[index] = " "


def consume_char(chars: list[str], index: int) -> int:
    start = index
    index += 1
    limit = len(chars)
    if index < limit and chars[index] == "\\":
        index = min(limit, index + 2)
    elif index < limit:
        index += 1
    if index < limit and chars[index] == "'":
        index += 1
    blank_range(chars, start, index)
    return index


def consume_normal_string(chars: list[str], index: int, dollars: int, verbatim: bool) -> int:
    """index points at the opening quote. Blank string text. Keep code braces inside holes."""
    limit = len(chars)
    hole_width = dollars if dollars > 0 else 0
    blank_range(chars, index, index + 1)
    index += 1
    while index < limit:
        char = chars[index]
        if not verbatim and char == "\\":
            blank_range(chars, index, min(limit, index + 2))
            index = min(limit, index + 2)
            continue
        if char == '"':
            if verbatim and index + 1 < limit and chars[index + 1] == '"':
                blank_range(chars, index, index + 2)
                index += 2
                continue
            blank_range(chars, index, index + 1)
            return index + 1
        if hole_width and char == "{":
            run = index
            while run < limit and chars[run] == "{":
                run += 1
            brace_count = run - index
            if brace_count == hole_width:
                blank_range(chars, index, run)
                index = consume_interpolation_hole(chars, run)
                continue
            if brace_count == hole_width * 2:
                blank_range(chars, index, run)
                index = run
                continue
        if char != "\n":
            chars[index] = " "
        index += 1
    return index


def consume_interpolation_hole(chars: list[str], index: int) -> int:
    """index is just after a blanked interpolation '{'. Return the index after its blanked '}'."""
    limit = len(chars)
    depth = 1
    while index < limit and depth > 0:
        consumed = try_consume_non_code(chars, index)
        if consumed is not None:
            index = consumed
            continue
        char = chars[index]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                chars[index] = " "
                return index + 1
        index += 1
    return index


def consume_raw_string(chars: list[str], index: int, quote_count: int, dollars: int) -> int:
    """index points at the first opening quote."""
    limit = len(chars)
    blank_range(chars, index, index + quote_count)
    index += quote_count
    hole_width = dollars if dollars > 0 else 0
    while index < limit:
        if chars[index] == '"':
            run = index
            while run < limit and chars[run] == '"':
                run += 1
            if run - index >= quote_count:
                blank_range(chars, index, index + quote_count)
                return index + quote_count
        if hole_width and chars[index] == "{":
            run = index
            while run < limit and chars[run] == "{":
                run += 1
            brace_count = run - index
            if brace_count == hole_width:
                blank_range(chars, index, run)
                index = consume_interpolation_hole(chars, run)
                continue
            if brace_count == hole_width * 2:
                blank_range(chars, index, run)
                index = run
                continue
        if chars[index] != "\n":
            chars[index] = " "
        index += 1
    return index


def try_consume_non_code(chars: list[str], index: int) -> int | None:
    limit = len(chars)
    char = chars[index]
    if char == "/" and index + 1 < limit and chars[index + 1] == "/":
        end = index
        while end < limit and chars[end] != "\n":
            end += 1
        blank_range(chars, index, end)
        return end
    if char == "/" and index + 1 < limit and chars[index + 1] == "*":
        end = index + 2
        while end + 1 < limit and not (chars[end] == "*" and chars[end + 1] == "/"):
            end += 1
        end = min(limit, end + 2)
        blank_range(chars, index, end)
        return end
    if char == "'":
        return consume_char(chars, index)

    if char not in "\"$@":
        return None
    cursor = index
    dollars = 0
    verbatim = False
    while cursor < limit and chars[cursor] in "$@":
        if chars[cursor] == "$":
            dollars += 1
        else:
            verbatim = True
        cursor += 1
    if cursor >= limit or chars[cursor] != '"':
        return None
    quote_end = cursor
    while quote_end < limit and chars[quote_end] == '"':
        quote_end += 1
    quote_count = quote_end - cursor
    if quote_count >= 3 and not verbatim:
        blank_range(chars, index, cursor)
        return consume_raw_string(chars, cursor, quote_count, dollars)
    if quote_count >= 2 and dollars == 0 and not verbatim:
        blank_range(chars, index, index + 2)
        return index + 2
    if quote_count >= 1:
        blank_range(chars, index, cursor)
        return consume_normal_string(chars, cursor, dollars, verbatim)
    return None


def mask_non_code(text: str) -> str:
    chars = list(text)
    index = 0
    limit = len(chars)
    interesting = "/'\"$@"
    while index < limit:
        if chars[index] not in interesting:
            index += 1
            continue
        consumed = try_consume_non_code(chars, index)
        if consumed is None or consumed <= index:
            index += 1
        else:
            index = consumed
    return "".join(chars)


def normalize_body(body: str, masked: str | None = None) -> list[str]:
    body = body.replace("\r\n", "\n").replace("\r", "\n")
    if masked is None:
        masked = mask_non_code(body)
    else:
        masked = masked.replace("\r\n", "\n").replace("\r", "\n")
    lines: list[str] = []
    raw_lines = body.split("\n")
    masked_lines = masked.split("\n")
    if len(raw_lines) != len(masked_lines):
        raise RuntimeError("mask changed the newline count")
    for raw_line, masked_line in zip(raw_lines, masked_lines, strict=True):
        if not masked_line.strip():
            continue
        # Drop comment-only and string-only lines. Keep a line that still has code.
        code = " ".join(masked_line.split())
        if not code:
            continue
        # Prefer the original line with comments removed so string text still
        # participates in the similarity ratio.
        kept = " ".join(segment.strip() for segment in raw_line.split("/*")[0].split("//")[0].split())
        if kept:
            lines.append(kept)
    return lines


def split_params(params: str) -> list[str]:
    parts: list[str] = []
    current: list[str] = []
    angles = 0
    parens = 0
    brackets = 0
    for char in params:
        if char == "<":
            angles += 1
        elif char == ">":
            angles = max(0, angles - 1)
        elif char == "(":
            parens += 1
        elif char == ")":
            parens = max(0, parens - 1)
        elif char == "[":
            brackets += 1
        elif char == "]":
            brackets = max(0, brackets - 1)
        if char == "," and angles == 0 and parens == 0 and brackets == 0:
            parts.append("".join(current))
            current = []
            continue
        current.append(char)
    if current:
        parts.append("".join(current))
    return parts


def parameter_types(params: str) -> str:
    types: list[str] = []
    for part in split_params(params):
        cleaned = []
        index = 0
        while index < len(part):
            if part[index] == "[":
                depth = 1
                index += 1
                while index < len(part) and depth:
                    if part[index] == "[":
                        depth += 1
                    elif part[index] == "]":
                        depth -= 1
                    index += 1
                continue
            cleaned.append(part[index])
            index += 1
        text = "".join(cleaned)
        equals = text.find("=")
        if equals >= 0:
            text = text[:equals]
        tokens = [token for token in text.split() if token not in {"out", "ref", "in", "params", "this", "scoped"}]
        if len(tokens) >= 2:
            types.append(" ".join(tokens[:-1]))
        elif tokens:
            types.append(tokens[0])
    return ",".join(types)


def match_signature(header: str) -> tuple[str, str] | None:
    """Return (name, parameter-type string) when header ends in a method declaration."""
    start = max(header.rfind(";"), header.rfind("{"), header.rfind("}"))
    window = header[start + 1 :]
    where_at = window.find(" where ")
    signature = window if where_at < 0 else window[:where_at]
    close = signature.rfind(")")
    open_paren = signature.rfind("(", 0, close)
    if open_paren < 0 or close < 0:
        return None
    if "=>" in signature[close:]:
        return None
    params = signature[open_paren + 1 : close]
    left = signature[:open_paren].rstrip()
    if left.endswith(">"):
        depth = 0
        cut = None
        for index in range(len(left) - 1, -1, -1):
            if left[index] == ">":
                depth += 1
            elif left[index] == "<":
                depth -= 1
                if depth == 0:
                    cut = index
                    break
        if cut is not None:
            left = left[:cut].rstrip()
    if not left:
        return None
    parts = left.split()
    name = parts[-1]
    if not name.isidentifier() or name in KEYWORDS:
        return None
    prefix = parts[:-1]
    if not prefix:
        return None
    if prefix[0] == "new" and not any(token in MODIFIERS and token != "new" for token in prefix):
        return None
    has_modifier = any(token in MODIFIERS for token in prefix)
    has_return = any(token not in MODIFIERS for token in prefix)
    if not has_modifier and not has_return:
        return None
    if "new" in prefix and not has_return:
        return None
    return name, parameter_types(params)


def matching_brace(masked: str, open_index: int) -> int | None:
    depth = 0
    for index in range(open_index, len(masked)):
        char = masked[index]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return index
    return None


class Method:
    def __init__(self, path: str, name: str, types: str, line: int, body: list[str]) -> None:
        self.path = path
        self.name = name
        self.types = types
        self.line = line
        self.body = body

    def endpoint(self, disambiguate: bool) -> str:
        suffix = f"@{self.line}" if disambiguate else ""
        return f"{self.path}::{self.name}({self.types}){suffix}"


def extract_methods(path: str, text: str) -> tuple[list[Method], bool]:
    masked = mask_non_code(text)
    if masked.count("{") != masked.count("}"):
        return [], False
    methods: list[Method] = []
    depth = 0
    for index, char in enumerate(masked):
        if char == "{":
            signature = match_signature(masked[max(0, index - 1200) : index])
            if signature is not None and depth >= 1:
                end = matching_brace(masked, index)
                if end is None:
                    return [], False
                body = normalize_body(text[index : end + 1], masked[index : end + 1])
                if len(body) >= MIN_LINES:
                    line = text.count("\n", 0, index) + 1
                    methods.append(Method(path, signature[0], signature[1], line, body))
            depth += 1
        elif char == "}":
            depth -= 1
            if depth < 0:
                return [], False
    return methods, depth == 0


def iter_source_files(root: Path) -> list[Path]:
    files: list[Path] = []
    src = root / "src"
    for path in src.rglob("*.cs"):
        relative = path.relative_to(root).as_posix()
        if "/obj/" in f"/{relative}/" or "/bin/" in f"/{relative}/":
            continue
        if path.name.endswith(".g.cs") or path.name.endswith(".Designer.cs"):
            continue
        files.append(path)
    files.sort()
    return files


def scan(root: Path) -> tuple[list[str], list[str]]:
    methods: list[Method] = []
    unbalanced: list[str] = []
    for path in iter_source_files(root):
        text = path.read_text(encoding="utf-8", errors="replace")
        relative = path.relative_to(root).as_posix()
        found, balanced = extract_methods(relative, text)
        if not balanced:
            unbalanced.append(relative)
            continue
        methods.extend(found)

    by_endpoint: dict[str, list[Method]] = {}
    for method in methods:
        by_endpoint.setdefault(method.endpoint(False), []).append(method)
    for group in by_endpoint.values():
        if len(group) > 1:
            for method in group:
                method.line = method.line  # endpoint() reads this when disambiguating

    collisions = {key for key, group in by_endpoint.items() if len(group) > 1}
    by_name: dict[str, list[Method]] = {}
    for method in methods:
        by_name.setdefault(method.name, []).append(method)

    pairs: set[str] = set()
    for group in by_name.values():
        if len(group) < 2:
            continue
        ordered = sorted(group, key=lambda method: (len(method.body), method.path, method.line))
        for left_index, left in enumerate(ordered):
            for right in ordered[left_index + 1 :]:
                shorter = len(left.body)
                longer = len(right.body)
                # Lengths are non-decreasing, so a method that is already too long
                # rules out every method after it.
                if shorter / longer < MIN_LENGTH_RATIO:
                    break
                if left.body == right.body or difflib.SequenceMatcher(
                    None, left.body, right.body, autojunk=False
                ).ratio() >= MIN_RATIO:
                    left_id = left.endpoint(left.endpoint(False) in collisions)
                    right_id = right.endpoint(right.endpoint(False) in collisions)
                    first, second = sorted((left_id, right_id))
                    pairs.add(f"{first}|{second}")
    return sorted(pairs), unbalanced


def self_test() -> int:
    body = "\n".join(["        value += 1;"] * 30)
    sample = f'''
namespace Demo;

internal static class Sample
{{
    private static int Alpha(string whereClause)
    {{
        var label = $"brace {{ 1 }}";
        var raw = """
            {{ not code }}
            """;
        // {{ comment brace
        var note = "a /* not a comment */ brace {{";
        {body}
        return value;
    }}

    private static int Beta(int other)
    {{
        if (other > 0)
        {{
            return other;
        }}
        return 0;
    }}
}}
'''
    # Beta is short. Alpha must be found, and braces in strings must not unbalance the file.
    methods, balanced = extract_methods("src/Sample.cs", sample)
    if not balanced:
        print("self-test: sample file is unbalanced", file=sys.stderr)
        return 1
    names = {method.name for method in methods}
    if names != {"Alpha"}:
        print(f"self-test: expected only Alpha, found {sorted(names)}", file=sys.stderr)
        return 1
    twin = sample.replace("private static int Alpha", "internal static int Alpha")
    root_text = sample + "\n" + twin.replace("namespace Demo", "namespace Demo.Other")
    # scan() walks the filesystem; check the pair helper directly.
    first, _ = extract_methods("src/A.cs", sample)
    second, _ = extract_methods("src/B.cs", twin.replace("namespace Demo", "namespace Demo.Other"))
    if not first or not second:
        print("self-test: failed to extract the twin methods", file=sys.stderr)
        return 1
    ratio = difflib.SequenceMatcher(None, first[0].body, second[0].body, autojunk=False).ratio()
    if ratio < MIN_RATIO:
        print(f"self-test: twin ratio {ratio} is below {MIN_RATIO}", file=sys.stderr)
        return 1
    if "not code" in "\n".join(first[0].body):
        print("self-test: raw-string contents leaked into the normalized body", file=sys.stderr)
        return 1
    del root_text
    print("self-test ok", file=sys.stderr)
    return 0


HEADER = """# Generated by scripts/ci/duplicate-method-ratchet.py. Do not add lines.
# Each line is a same-name method pair under src/ whose normalized bodies are
# at least 25 lines and at least 0.92 similar. Delete a line only when that
# pair is gone. The architecture test rejects a new line against trunk.
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
    pairs, unbalanced = scan(root)
    if unbalanced:
        print("unbalanced C# files (scanner skipped them):", file=sys.stderr)
        for path in unbalanced:
            print(path, file=sys.stderr)
        return 2
    if args.write:
        payload = HEADER + "".join(f"{pair}\n" for pair in pairs)
        args.write.write_text(payload, encoding="utf-8")
        print(f"wrote {len(pairs)} pairs to {args.write}", file=sys.stderr)
        return 0
    sys.stdout.write("".join(f"{pair}\n" for pair in pairs))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
