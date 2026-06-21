#!/usr/bin/env python3
"""CQL2 conformance gate for Honua Server.

honua advertises the CQL2 conformance classes (cql2-text, cql2-json, basic-cql2,
spatial/temporal/array functions) on OGC API Features Part 3 and STAC, but there
is no TeamEngine CITE suite for CQL2. This gate uses the canonical alternative
validator `cql2-rs` (developmentseed/cql2-rs, `pip install cql2`) plus the
official OGC example corpus:

  1. Corpus check (offline) - parse + schema-validate every vendored text
     fixture; where a JSON twin exists, assert the two parse to the same
     canonical JSON (text<->json round-trip equivalence).
  2. Live endpoint check - drive honua's OGC API Features Part 3 filter endpoint
     (`/items?filter=...&filter-lang=cql2-text|cql2-json`) with known filters,
     assert the result deltas, that text and json forms agree, that invalid
     syntax is rejected (400), and that `/queryables` is a valid JSON-Schema doc.
     honua-emitted/accepted filters are also piped back through cql2-rs
     `Expr(...).validate()`.

Exit code 0 = all gates pass, non-zero = at least one failure. Designed to run
hermetically: the corpus check needs no network; the live check needs only the
honua base URL.
"""
from __future__ import annotations

import argparse
import json
import sys
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

try:
    from cql2 import Expr
except ImportError:  # pragma: no cover
    print("ERROR: cql2 not installed. Run: pip install cql2", file=sys.stderr)
    sys.exit(2)

try:
    import jsonschema
except ImportError:  # pragma: no cover
    print("ERROR: jsonschema not installed. Run: pip install jsonschema", file=sys.stderr)
    sys.exit(2)

HERE = Path(__file__).resolve().parent
VENDOR = HERE / "vendor"
CORPUS_TEXT = VENDOR / "cql2-corpus" / "text"
CORPUS_JSON = VENDOR / "cql2-corpus" / "json"

# Fixtures whose vendored text and JSON twins are deliberately DIFFERENT
# expressions in the upstream corpus (not a text<->json encoding of the same
# filter), so a round-trip equality assertion is meaningless. These are corpus
# authoring choices, not honua failures:
#   example06a - text is the BETWEEN sub-clause; json is the full AND expression.
#   example16  - text/json nest the OR clauses differently (3-arg vs 2-arg or).
#   example49  - text is a 2D polygon; json twin carries 3D coordinates.
#   example85  - cql2-rs expands a unary minus on a property in CQL2-text as
#                ((-1 * 1) * foo) but encodes the JSON twin as (-1 * foo); the two
#                arithmetic trees are equal-valued but structurally different, a
#                cql2-rs encoding quirk rather than a corpus or honua issue.
CORPUS_TEXT_XFAIL: set[str] = set()
CORPUS_PAIR_XFAIL: set[str] = {"example06a", "example16", "example49", "example85"}


@dataclass
class Results:
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    failures: list[str] = field(default_factory=list)

    def ok(self) -> None:
        self.passed += 1

    def skip(self) -> None:
        self.skipped += 1

    def fail(self, msg: str) -> None:
        self.failed += 1
        self.failures.append(msg)

    def merge(self, other: "Results") -> None:
        self.passed += other.passed
        self.failed += other.failed
        self.skipped += other.skipped
        self.failures.extend(other.failures)


def _normalize_timestamp(value: str) -> str:
    """Collapse fractional-second padding so '...00.000000Z' == '...00Z'.

    cql2-rs renders timestamps from CQL2-text with 6-digit microseconds but keeps
    the JSON twin's literal precision; both denote the same instant.
    """
    if value.endswith("Z") and "T" in value and "." in value:
        head, frac = value[:-1].split(".", 1)
        frac = frac.rstrip("0")
        return head + "Z" if not frac else f"{head}.{frac}Z"
    return value


def _normalize(obj):
    """Fold cql2-rs's cosmetic text<->json normalization differences so the
    round-trip comparison tests *semantic* equivalence, not byte-equality:

      * function/operator names are case-insensitive in CQL2 (the text parser
        lowercases them, e.g. 't_finishedBy' -> 't_finishedby', 'Buffer' ->
        'buffer'); lowercase every ``op`` so the cases agree.
      * a BBOX literal in text decodes to an arithmetic ``bbox`` op (negative
        numbers as ``-1 * n``) while JSON keeps ``{"bbox":[...]}``; canonicalize
        both to a ``{"bbox":[floats]}`` form.
      * timestamps differ only in trailing-zero microseconds.
    """
    if isinstance(obj, dict):
        # {"bbox": [...]} JSON-literal form -> canonical bbox node
        if set(obj.keys()) == {"bbox"} and isinstance(obj["bbox"], list):
            return {"bbox": [float(c) for c in obj["bbox"]]}
        op = obj.get("op")
        # arithmetic-decoded BBOX op from text -> canonical bbox node
        if isinstance(op, str) and op.lower() == "bbox" and isinstance(obj.get("args"), list):
            coords = []
            ok = True
            for a in obj["args"]:
                if isinstance(a, (int, float)):
                    coords.append(float(a))
                elif (
                    isinstance(a, dict)
                    and str(a.get("op", "")).lower() == "*"
                    and isinstance(a.get("args"), list)
                    and len(a["args"]) == 2
                    and a["args"][0] == -1.0
                    and isinstance(a["args"][1], (int, float))
                ):
                    # text encodes a negative bbox bound as (-1 * n)
                    coords.append(-float(a["args"][1]))
                else:
                    ok = False
                    break
            if ok:
                return {"bbox": coords}
        result = {}
        for k, v in obj.items():
            if k == "op" and isinstance(v, str):
                result[k] = v.lower()
            elif k == "timestamp" and isinstance(v, str):
                result[k] = _normalize_timestamp(v)
            elif k == "interval" and isinstance(v, list):
                result[k] = [
                    _normalize_timestamp(x) if isinstance(x, str) else _normalize(x) for x in v
                ]
            else:
                result[k] = _normalize(v)
        return result
    if isinstance(obj, list):
        return [_normalize(x) for x in obj]
    if isinstance(obj, str):
        return _normalize_timestamp(obj)
    return obj


def _canonical(obj) -> str:
    """Stable, normalization-tolerant serialization so two CQL2 JSON trees that
    denote the same filter compare equal regardless of cql2-rs encoding quirks."""
    return json.dumps(_normalize(obj), sort_keys=True, separators=(",", ":"))


# --- 1. Offline corpus -------------------------------------------------------
def run_corpus(res: Results) -> None:
    print("== CQL2 corpus (cql2-rs + official examples) ==")
    if not CORPUS_TEXT.is_dir():
        res.fail(f"corpus dir missing: {CORPUS_TEXT}")
        return

    text_files = sorted(CORPUS_TEXT.glob("*.txt"))
    if not text_files:
        res.fail("no text corpus fixtures found")
        return

    for tf in text_files:
        stem = tf.stem
        text = tf.read_text(encoding="utf-8").strip()
        if stem in CORPUS_TEXT_XFAIL:
            res.skip()
            continue
        # 1a. parse + schema-validate the text fixture
        try:
            expr = Expr(text)
            expr.validate()
            parsed_text_json = expr.to_json()
        except Exception as exc:  # noqa: BLE001
            res.fail(f"text fixture {stem}: parse/validate failed: {exc!r} :: {text!r}")
            continue
        res.ok()

        # 1b. round-trip equivalence against the JSON twin, when present
        jf = CORPUS_JSON / f"{stem}.json"
        if not jf.is_file() or stem in CORPUS_PAIR_XFAIL:
            continue
        try:
            json_src = json.loads(jf.read_text(encoding="utf-8"))
            parsed_json_json = Expr(json.dumps(json_src)).to_json()
        except Exception as exc:  # noqa: BLE001
            res.fail(f"json twin {stem}: parse failed: {exc!r}")
            continue
        if _canonical(parsed_text_json) != _canonical(parsed_json_json):
            res.fail(
                f"round-trip mismatch {stem}: text->{_canonical(parsed_text_json)} "
                f"!= json->{_canonical(parsed_json_json)}"
            )
        else:
            res.ok()

    print(f"   corpus: {res.passed} passed, {res.failed} failed, {res.skipped} skipped")


# --- 2. Live honua endpoints -------------------------------------------------
def _http_json(url: str, timeout: int = 30):
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310
        return resp.status, json.loads(resp.read().decode("utf-8"))


def _http_status(url: str, timeout: int = 30) -> int:
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310
            resp.read()
            return resp.status
    except urllib.error.HTTPError as exc:  # noqa: PERF203
        return exc.code


def _items_url(base: str, coll: str, filter_lang: str, filt: str, limit: int = 1000) -> str:
    qs = urllib.parse.urlencode(
        {"filter-lang": filter_lang, "filter": filt, "limit": str(limit), "f": "json"}
    )
    return f"{base}/ogc/features/collections/{coll}/items?{qs}"


def _count(base: str, coll: str, lang: str, filt: str, res: Results) -> int | None:
    url = _items_url(base, coll, lang, filt)
    try:
        status, body = _http_json(url)
    except Exception as exc:  # noqa: BLE001
        res.fail(f"live filter [{lang}] {filt!r}: request failed: {exc!r}")
        return None
    if status != 200 or "features" not in body:
        res.fail(f"live filter [{lang}] {filt!r}: status={status} body={str(body)[:160]}")
        return None
    # pipe the accepted filter back through cql2-rs as an extra conformance check
    if lang == "cql2-text":
        try:
            Expr(filt).validate()
        except Exception as exc:  # noqa: BLE001
            res.fail(f"honua-accepted filter rejected by cql2-rs: {filt!r}: {exc!r}")
            return None
    return len(body["features"])


def run_live(base: str, coll: str, geom_field: str, cat_field: str, res: Results) -> None:
    print(f"== CQL2 live endpoints ({base}, collection {coll}) ==")

    # 2a. queryables must be a valid JSON-Schema document
    try:
        status, queryables = _http_json(f"{base}/ogc/features/collections/{coll}/queryables")
        if status != 200:
            res.fail(f"queryables: status {status}")
        else:
            # The document must itself be a valid JSON Schema (validate against its meta-schema).
            jsonschema.Draft202012Validator.check_schema(queryables)
            if queryables.get("type") != "object" or "properties" not in queryables:
                res.fail("queryables: missing type=object / properties")
            else:
                res.ok()
                print(f"   queryables valid JSON-Schema with {len(queryables['properties'])} properties")
    except jsonschema.exceptions.SchemaError as exc:
        res.fail(f"queryables: not a valid JSON Schema: {exc.message}")
    except Exception as exc:  # noqa: BLE001
        res.fail(f"queryables: {exc!r}")

    # 2b. baseline count
    baseline = _count(base, coll, "cql2-text", "1=1", res)
    if baseline is None:
        print("   baseline query failed; skipping delta checks")
        return
    print(f"   baseline (1=1) -> {baseline} features")

    # 2c. discover a real category value to make data-driven assertions robust
    try:
        _, sample = _http_json(_items_url(base, coll, "cql2-text", "1=1", limit=200))
        cats = [
            f.get("properties", {}).get(cat_field)
            for f in sample.get("features", [])
            if f.get("properties", {}).get(cat_field) not in (None, "")
        ]
    except Exception:  # noqa: BLE001
        cats = []
    cat_value = cats[0] if cats else None

    # 2d. equality delta + text/json agreement
    if cat_value is not None:
        eq_count = sum(1 for c in cats if c == cat_value)  # within the 200-row sample
        text_n = _count(base, coll, "cql2-text", f"{cat_field} = '{cat_value}'", res)
        json_filter = json.dumps({"op": "=", "args": [{"property": cat_field}, cat_value]})
        json_n = _count(base, coll, "cql2-json", json_filter, res)
        if text_n is not None and json_n is not None:
            if text_n != json_n:
                res.fail(f"text/json disagree for {cat_field}={cat_value}: {text_n} != {json_n}")
            elif text_n == 0:
                res.fail(f"equality filter {cat_field}={cat_value} returned 0 (expected >0)")
            elif text_n >= baseline and baseline > eq_count:
                res.fail(f"equality filter did not reduce result set ({text_n} vs baseline {baseline})")
            else:
                res.ok()
                print(f"   {cat_field}='{cat_value}': text={text_n} json={json_n} (agree, filtered)")

        # 2e. negation complement: (= v) + (<> v) should equal non-null total
        neq_n = _count(base, coll, "cql2-text", f"{cat_field} <> '{cat_value}'", res)
        if text_n is not None and neq_n is not None:
            if text_n + neq_n > baseline:
                res.fail(f"= plus <> exceeds baseline: {text_n}+{neq_n} > {baseline}")
            else:
                res.ok()
    else:
        print(f"   no '{cat_field}' values found; skipping equality/negation deltas")

    # 2f. spatial predicate: world bbox should match all features carrying geometry
    spatial = _count(
        base, coll, "cql2-text", f"S_INTERSECTS({geom_field},BBOX(-180,-90,180,90))", res
    )
    if spatial is not None:
        if spatial == 0:
            res.fail("S_INTERSECTS world bbox returned 0 features")
        elif spatial > baseline:
            res.fail(f"S_INTERSECTS world bbox returned more than baseline ({spatial} > {baseline})")
        else:
            res.ok()
            print(f"   S_INTERSECTS(world bbox) -> {spatial} (<= baseline {baseline})")

    # 2g. invalid CQL2 syntax must be rejected with 400 (negative conformance)
    bad_url = _items_url(base, coll, "cql2-text", "category ==")
    bad_status = _http_status(bad_url)
    if bad_status == 400:
        res.ok()
        print("   invalid filter correctly rejected (400)")
    else:
        res.fail(f"invalid filter not rejected: got status {bad_status} (expected 400)")


def main() -> int:
    parser = argparse.ArgumentParser(description="CQL2 conformance gate for Honua")
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--collection", default="0")
    parser.add_argument("--geom-field", default="shape")
    parser.add_argument("--category-field", default="category")
    parser.add_argument("--skip-live", action="store_true", help="run only the offline corpus check")
    parser.add_argument("--skip-corpus", action="store_true", help="run only the live endpoint check")
    args = parser.parse_args()

    res = Results()
    if not args.skip_corpus:
        run_corpus(res)
    if not args.skip_live:
        run_live(args.base_url, args.collection, args.geom_field, args.category_field, res)

    print("\n== CQL2 gate summary ==")
    print(f"   passed:  {res.passed}")
    print(f"   failed:  {res.failed}")
    print(f"   skipped: {res.skipped}")
    if res.failures:
        print("\nFailures:")
        for f in res.failures:
            print(f"  - {f}")
        return 1
    print("CQL2 conformance gate PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
