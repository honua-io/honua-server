#!/usr/bin/env python3
"""Apply the neutral GeoServices naming map to C# sources and data files.

Re-runnable: applying it to an already neutral tree changes nothing, so a branch that
conflicts with the rename can run it again instead of resolving the conflicts by hand.

Usage: python3 scripts/migrations/geoservices-neutral-names.py [--root DIR] [--check] [PATH ...]
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

MAP_PATH = Path(__file__).with_name("geoservices-neutral-names.json")
IDENTIFIER = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
DOC_COMMENT = re.compile(r"^[ \t]*///")
DATA_SUFFIXES = {".json", ".yaml", ".yml"}
STRING_PREFIX = re.compile(r'\$*@?\$*(?=")')
QUOTE_RUN = re.compile(r'"+')
CHAR_LITERAL = re.compile(r"'(?:\\.|[^'\\])*'")
TOKEN_START = re.compile(r'//|/\*|\$*@?\$*"|\'|[{}]')


@dataclass(frozen=True)
class NamingMap:
    identifiers: dict[str, str]
    literals: dict[str, str]
    literal_fragments: dict[str, str]
    data_values: dict[str, str]
    excluded_paths: tuple[str, ...]

    @classmethod
    def load(cls, path: Path) -> NamingMap:
        raw = json.loads(path.read_text(encoding="utf-8"))
        return cls(
            identifiers=raw["identifiers"],
            literals=raw["literals"],
            literal_fragments=raw["literalFragments"],
            data_values=raw["dataValues"],
            excluded_paths=tuple(raw["excludedPaths"]),
        )

    def rename_identifiers(self, text: str) -> str:
        return IDENTIFIER.sub(lambda match: self.identifiers.get(match.group(0), match.group(0)), text)

    def rename_literal(self, content: str) -> str:
        if content in self.literals:
            return self.literals[content]
        for old, new in self.literal_fragments.items():
            content = content.replace(old, new)
        return content

    def mentions_renamed_identifier(self, text: str) -> bool:
        return any(token in self.identifiers for token in IDENTIFIER.findall(text))


@dataclass(frozen=True)
class Segment:
    kind: str
    text: str


@dataclass(frozen=True)
class StringForm:
    closer: str
    dollars: int
    verbatim: bool
    raw: bool


class CSharpSegmenter:
    """Splits C# source into code, comment and string-content segments, recursing into
    interpolation holes so the identifiers inside them are treated as code."""

    def __init__(self, source: str) -> None:
        self.source = source
        self.position = 0
        self.segments: list[Segment] = []

    def split(self) -> list[Segment]:
        self._read_code(stop_at_brace=False)
        return self.segments

    def _emit(self, kind: str, text: str) -> None:
        if text:
            self.segments.append(Segment(kind, text))

    def _read_code(self, stop_at_brace: bool) -> None:
        start, depth = self.position, 0
        while (match := TOKEN_START.search(self.source, self.position)) is not None:
            self.position, token = match.start(), match.group(0)
            if token in "{}":
                if token == "}" and stop_at_brace and depth == 0:
                    break
                depth += 1 if token == "{" else -1
                self.position += 1
                continue
            self._emit("code", self.source[start:self.position])
            self._read_token()
            start = self.position
        else:
            self.position = len(self.source)
        self._emit("code", self.source[start:self.position])

    def _read_token(self) -> None:
        if self.source.startswith("//", self.position):
            end = self.source.find("\n", self.position)
            self._read_until(end if end >= 0 else len(self.source), "comment")
        elif self.source.startswith("/*", self.position):
            end = self.source.find("*/", self.position + 2)
            self._read_until(end + 2 if end >= 0 else len(self.source), "comment")
        elif self.source[self.position] == "'":
            self._read_char_literal()
        else:
            self._read_string()

    def _read_until(self, end: int, kind: str) -> None:
        self._emit(kind, self.source[self.position:end])
        self.position = end

    def _read_char_literal(self) -> None:
        match = CHAR_LITERAL.match(self.source, self.position)
        self._read_until(self.position + (len(match.group(0)) if match else 1), "code")

    def _read_string(self) -> None:
        prefix = STRING_PREFIX.match(self.source, self.position).group(0)
        quotes = QUOTE_RUN.match(self.source, self.position + len(prefix)).group(0)
        raw = len(quotes) >= 3
        opener = prefix + (quotes if raw else '"')
        self._emit("delimiter", opener)
        self.position += len(opener)
        closer = quotes if raw else '"'
        self._read_string_body(StringForm(closer, prefix.count("$"), raw or "@" in prefix, raw))
        self._emit("delimiter", self.source[self.position:self.position + len(closer)])
        self.position += len(closer)

    def _read_string_body(self, form: StringForm) -> None:
        start = self.position
        while self.position < len(self.source):
            if self.source.startswith(form.closer, self.position) and not self._is_doubled_quote(form):
                break
            if not form.verbatim and self.source[self.position] == "\\":
                self.position += 2
                continue
            literal_braces, opens_hole = self._brace_run(form)
            self.position += literal_braces
            if opens_hole:
                self._emit("string", self.source[start:self.position])
                self._read_hole(form.dollars)
                start = self.position
            elif not literal_braces:
                self.position += 2 if self._is_doubled_quote(form) else 1
        self._emit("string", self.source[start:self.position])

    def _brace_run(self, form: StringForm) -> tuple[int, bool]:
        """Literal braces to skip at the position, and whether an interpolation hole opens after them."""
        char = self.source[self.position]
        if not form.dollars or char not in "{}":
            return 0, False
        run = len(re.match(r"\{+|\}+", self.source[self.position:self.position + 64]).group(0))
        if not form.raw:
            return (2, False) if run >= 2 else (0, char == "{")
        if char == "{" and run >= form.dollars:
            return run - form.dollars, True
        return run, False

    def _is_doubled_quote(self, form: StringForm) -> bool:
        return form.verbatim and form.closer == '"' and self.source.startswith('""', self.position)

    def _read_hole(self, braces: int) -> None:
        self._emit("delimiter", "{" * braces)
        self.position += braces
        self._read_code(stop_at_brace=True)
        self._emit("delimiter", self.source[self.position:self.position + braces])
        self.position += braces


def string_spans(segments: list[Segment]) -> list[tuple[int, int]]:
    spans, offset = [], 0
    for segment in segments:
        if segment.kind == "string":
            spans.append((offset, offset + len(segment.text)))
        offset += len(segment.text)
    return spans


def inside_string(offset: int, spans: list[tuple[int, int]]) -> bool:
    return any(start <= offset < end for start, end in spans)


def drop_doc_blocks_mentioning(source: str, naming: NamingMap) -> str:
    spans = string_spans(CSharpSegmenter(source).split())
    kept, block, offset = [], [], 0
    for line in source.splitlines(keepends=True):
        if DOC_COMMENT.match(line) and not inside_string(offset + line.index("/"), spans):
            block.append(line)
        else:
            kept.extend(_resolve_block(block, naming))
            block = []
            kept.append(line)
        offset += len(line)
    kept.extend(_resolve_block(block, naming))
    return "".join(kept)


def _resolve_block(block: list[str], naming: NamingMap) -> list[str]:
    return [] if naming.mentions_renamed_identifier("".join(block)) else block


def rename_csharp(source: str, naming: NamingMap) -> str:
    source = drop_doc_blocks_mentioning(source, naming)
    renamed = []
    for segment in CSharpSegmenter(source).split():
        if segment.kind == "string":
            renamed.append(naming.rename_literal(segment.text))
        elif segment.kind in ("code", "comment"):
            renamed.append(naming.rename_identifiers(segment.text))
        else:
            renamed.append(segment.text)
    return "".join(renamed)


def rename_data(text: str, naming: NamingMap) -> str:
    for old, new in naming.data_values.items():
        text = text.replace(old, new)
    return text


def renamed_path(path: Path, naming: NamingMap) -> Path:
    stem, _, rest = path.name.partition(".")
    return path.with_name(naming.identifiers.get(stem, stem) + ("." + rest if rest else ""))


def is_excluded(relative: str, naming: NamingMap) -> bool:
    return any(relative == prefix or relative.startswith(prefix.rstrip("/") + "/") for prefix in naming.excluded_paths)


def tracked_files(root: Path, scopes: list[str]) -> list[Path]:
    listing = subprocess.run(["git", "-C", str(root), "ls-files", "-z", "--", *scopes],
                             check=True, capture_output=True, text=True).stdout
    return [root / name for name in listing.split("\0") if name]


def read_text_or_none(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return None


def transform(path: Path, naming: NamingMap) -> str | None:
    if path.suffix != ".cs" and path.suffix not in DATA_SUFFIXES:
        return None
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return None
    if path.suffix == ".cs":
        return rename_csharp(text, naming)
    return rename_data(text, naming)


def apply(root: Path, scopes: list[str], naming: NamingMap, check_only: bool) -> list[str]:
    changed = []
    for path in tracked_files(root, scopes):
        relative = path.relative_to(root).as_posix()
        if is_excluded(relative, naming):
            continue
        updated = transform(path, naming)
        target = renamed_path(path, naming) if path.suffix == ".cs" else path
        if (updated is None or updated == read_text_or_none(path)) and target == path:
            continue
        changed.append(relative)
        if not check_only:
            write_renamed(root, path, target, updated)
    return changed


def write_renamed(root: Path, path: Path, target: Path, content: str | None) -> None:
    if target != path:
        subprocess.run(["git", "-C", str(root), "mv", str(path), str(target)], check=True)
    if content is not None:
        target.write_text(content, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--check", action="store_true", help="list files that would change; exit 1 if any")
    parser.add_argument("paths", nargs="*", default=["src", "tests"])
    args = parser.parse_args()
    changed = apply(args.root.resolve(), args.paths, NamingMap.load(MAP_PATH), args.check)
    for relative in changed:
        print(relative)
    return 1 if args.check and changed else 0


if __name__ == "__main__":
    sys.exit(main())
