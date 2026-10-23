#!/usr/bin/env python3
"""N-1 reader-compatibility gate: does the prior image still start on this change's schema?

honua-server#6477 was found by a release lane months after the migrations that
caused it landed: after `helm rollback`, the prior image refused the newer
database with `schema_reader_incompatible`, although every script it did not
ship was expand-only. This script reproduces that edge on a developer machine or
a PR runner, so the PR that introduces such a refusal sees it.

What it does, in order:

1. Resolves the PRIOR image by digest. The prior of record is the honua-server
   image pinned by the latest published honua-io/honua-release release (its
   `finalized-manifest.yaml`), i.e. what a customer runs: `priorSource:
   published-release`, pulled by the digest the release recorded. Trunk
   `nightly-aot` is used only as labelled scaffolding (`priorSource:
   nightly-scaffold`) when no release pin can be pulled; `--prior-image` gives
   `override`. Failing to resolve any prior is a failure, never a pass.
2. Starts PostGIS and applies the candidate's full migration set (the N schema)
   with the candidate build's migrate-only mode (`HONUA_MIGRATE_ONLY=true`), so
   the candidate's migration runner also writes its reader-compatibility
   declarations (#6478). It then applies the shared CI seed
   (`tests/seed/base-schema.sql`).
3. Starts the prior image against that database with the default migration
   configuration (`HONUA_SKIP_MIGRATIONS` unset, i.e. false) and waits for
   `/healthz/ready` or for the container to stop.
4. If it is ready, reads the seeded layer and writes one feature through it, then
   reads it back. If it refused, it parses the typed refusal.
5. Classifies the outcome and writes `receipt.json` plus `summary.md`.

Outcomes and exit status:

  0  compatible                    prior served the N schema (ready, read, write)
  0  expected-refusal              prior refused with the typed code, every refused
                                   script is contract / reader-incompatible, and
                                   each such script this change adds carries
                                   `-- honua:compatibility-review` and is named in
                                   the rollback-boundary docs
  3  prior-lacks-reader-declarations
                                   KNOWN-BLOCKED, not a pass: the prior image
                                   predates schema 135 (#6478), so it refuses every
                                   journal entry it does not ship, additive or not
  3  prior-unguarded               KNOWN-BLOCKED, not a pass: the prior image
                                   predates the schema-reader guard (#5333), so it
                                   started on journal entries it cannot qualify,
                                   contract-phase ones included. A seeded read and
                                   write succeeding there proves nothing about
                                   rollback safety
  1  anything else: reader-incompatible-regression, expected-refusal-missing,
     expected-refusal-undocumented, refusal-untyped, serve-check-failed,
     prior-changed-journal, candidate-migration-failed, prior-unresolved,
     infrastructure-error

Usage:
    scripts/ci/n1-reader-compat.py --output <dir> [--candidate-dll <path>] \
        [--prior-image <ref>] [--base-ref <rev>]
    scripts/ci/n1-reader-compat.py --applies [--base-ref <rev>]
    scripts/ci/n1-reader-compat.test.py      # offline self-test of the classifier

Requires Docker and git; `--candidate-dll` defaults to the Release build of
src/Honua.Server, which is built first when missing (needs the .NET SDK).
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field
from pathlib import Path

RECEIPT_SCHEMA = "honua.n1-reader-compat/v1"
REFUSAL_CODE = "schema_reader_incompatible"
READER_DECLARATIONS_MIGRATION = "135_RecordMigrationReaderCompatibility.sql"
# The typed schema_reader_incompatible startup guard (#5333); images built before it never refuse.
READER_GUARD_SOURCE = "src/Honua.Core/Features/Infrastructure/Domain/DatabaseSchemaCompatibilityException.cs"
# The N-1 prior of record is the honua-server image the latest published platform release
# pins: the image a customer actually runs. Trunk `nightly-aot` is scaffolding, used (and
# labelled `nightly-scaffold`) only when no release pin can be resolved and pulled.
RELEASE_REPO = "honua-io/honua-release"
RELEASE_MANIFEST_ASSET = "finalized-manifest.yaml"
NIGHTLY_SCAFFOLD = "ghcr.io/honua-io/honua-server:nightly-aot"
ROLLBACK_DOC = "docs/guides/deploy/upgrade-and-rollback.md"
SEED_SQL = "tests/seed/base-schema.sql"
SEED_SERVICE = "test_service"
SEED_LAYER = 0
ADMIN_KEY = "n1-reader-compat-admin-1a"
COMPATIBILITY_REVIEW = re.compile(r"^[ \t]*--[ \t]*honua:compatibility-review\b", re.IGNORECASE | re.MULTILINE)
READER_INCOMPATIBLE = re.compile(r"^[ \t]*--[ \t]*honua:reader-incompatible\b", re.IGNORECASE | re.MULTILINE)
# The paths whose change can move what an older reader sees or how it checks it.
APPLIES_PATTERNS = (
    "src/*/Migrations/*.sql",
    "src/*/*/Migrations/*.sql",
    "src/Honua.Core/Features/Infrastructure/Migrations/*",
    "src/Honua.Core/Features/Infrastructure/Abstractions/IDatabaseSchemaGuard.cs",
    "src/Honua.Core/Features/Infrastructure/Abstractions/IDatabaseMigrationRunner.cs",
    "src/Honua.Core/Features/Infrastructure/Domain/DatabaseSchemaCompatibilityException.cs",
    "src/Honua.Db/Postgres/Features/Infrastructure/Migrations/*",
    "src/Honua.Server/Hosting/MigrateOnly/*",
    "src/Honua.Server/Startup/ServerCoreSchemaMigrations.cs",
    "certification/schema-reader-baseline.json",
    "certification/schema-migration-hashes.json",
    SEED_SQL,
    "scripts/ci/n1-reader-compat.py",
    "scripts/ci/n1-reader-compat.test.py",
)
PASSING = {"compatible", "expected-refusal"}
KNOWN_BLOCKED = {"prior-lacks-reader-declarations", "prior-unguarded"}
REPO_ROOT = Path(__file__).resolve().parents[2]


class GateError(RuntimeError):
    """A failure with a named outcome."""

    def __init__(self, outcome: str, message: str) -> None:
        super().__init__(message)
        self.outcome = outcome


# --------------------------------------------------------------------------- pure logic


def path_applies(path: str) -> bool:
    path = path.replace("\\", "/")
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in APPLIES_PATTERNS)


def parse_refusal(logs: str) -> dict | None:
    """Return the typed refusal from server logs, or None if there is none."""
    marker = logs.find(f"{REFUSAL_CODE}: this server cannot verify compatibility with applied migration(s): ")
    if marker < 0:
        return None
    text = logs[marker:]
    head = text.split(": ", 2)[2]
    names_part = head.split(". Start a compatible server", 1)[0]
    scripts = [name.strip() for name in names_part.split(",") if name.strip()]
    refusals: dict[str, str] = {}
    match = re.search(r"Forward-compatibility declaration\(s\) refused: (.*?)\.(?:\s|\"|$)", text)
    if match:
        for item in match.group(1).split("; "):
            item_match = re.fullmatch(r"(\S+) \((.*)\)", item.strip())
            if item_match:
                refusals[item_match.group(1)] = item_match.group(2)
    return {"code": REFUSAL_CODE, "scripts": scripts, "declaration_refusals": refusals}


def basename_of(journal_name: str) -> str:
    """`Honua.Server.Migrations.126_X.sql` -> `126_X.sql`."""
    match = re.search(r"(\d{3,}_[^.]+\.sql)$", journal_name)
    return match.group(1) if match else journal_name


@dataclass
class Evidence:
    journal_delta: list[str]
    classifications: dict[str, str]
    pr_added: set[str]
    prior_has_reader_declarations: bool
    refusal: dict | None
    ready: bool
    serve_ok: bool
    script_text: dict[str, str] = field(default_factory=dict)
    rollback_doc: str = ""
    prior_has_reader_guard: bool = True
    journal_changed: bool = False


def documented(script: str, evidence: Evidence) -> list[str]:
    """Problems with an added reader-incompatible script's review trail (empty = fine)."""
    problems = []
    text = evidence.script_text.get(script, "")
    if not COMPATIBILITY_REVIEW.search(text):
        problems.append(f"{script} lacks a `-- honua:compatibility-review` annotation")
    stem = script[:-4] if script.endswith(".sql") else script
    if stem not in evidence.rollback_doc:
        problems.append(f"{script} is not named in the rollback-boundary note in {ROLLBACK_DOC}")
    return problems


def classify(evidence: Evidence) -> tuple[str, str]:
    """Map the observed evidence to (outcome, explanation)."""
    if evidence.journal_changed:
        # The prior applied migrations of its own. A serve check against that
        # mutated journal is not N-1 evidence, including when the prior is unguarded.
        return ("prior-changed-journal",
                "the prior image changed the migration journal (journal_after != journal), "
                "so this receipt does not prove N-1 compatibility")
    breaking = {
        script for script in evidence.journal_delta
        if evidence.classifications.get(script) in ("contract", "reader-incompatible")
    }
    if evidence.refusal is None:
        if not evidence.ready:
            return ("refusal-untyped",
                    "the prior image neither reached /healthz/ready nor refused with "
                    f"`{REFUSAL_CODE}`; see prior.log")
        if not evidence.prior_has_reader_guard and evidence.journal_delta:
            return ("prior-unguarded",
                    "KNOWN-BLOCKED: the prior image predates the schema-reader guard (#5333) and started "
                    f"on {len(evidence.journal_delta)} journal entr(y/ies) it does not ship"
                    + (f", including contract-phase {', '.join(sorted(breaking))}" if breaking else "")
                    + f" (seeded read/write {'passed' if evidence.serve_ok else 'failed'}). Nothing verified "
                    "that it can serve this schema, so this is not a pass.")
        if breaking:
            return ("expected-refusal-missing",
                    "the prior image served although these scripts are declared contract or "
                    f"reader-incompatible: {', '.join(sorted(breaking))}")
        if not evidence.serve_ok:
            return ("serve-check-failed",
                    "the prior image reported Ready but the seeded layer read/write failed")
        return ("compatible",
                f"the prior image served the N schema across {len(evidence.journal_delta)} "
                "unshipped journal entr(y/ies)")

    refused = {basename_of(name) for name in evidence.refusal["scripts"]}
    if refused and refused <= breaking:
        problems = [p for script in sorted(refused & evidence.pr_added) for p in documented(script, evidence)]
        if problems:
            return ("expected-refusal-undocumented", "; ".join(problems))
        return ("expected-refusal",
                "the prior image refused with the typed code, only on declared contract / "
                f"reader-incompatible scripts: {', '.join(sorted(refused))}")
    if not evidence.prior_has_reader_declarations:
        return ("prior-lacks-reader-declarations",
                "KNOWN-BLOCKED: the prior image predates schema 135 (#6478) and refuses every "
                f"journal entry it does not ship: {', '.join(sorted(refused))}. This is not a pass; "
                "it clears once the prior channel carries the reader-declaration check.")
    unexpected = sorted(refused - breaking)
    return ("reader-incompatible-regression",
            "the prior image refused expand-classified scripts it should verify: "
            f"{', '.join(unexpected)}. Either the scripts are not rollback-safe (mark them "
            "`-- honua:reader-incompatible reason=...`, add `-- honua:compatibility-review` and a "
            f"rollback-boundary note in {ROLLBACK_DOC}) or the declaration/verification is broken.")


def exit_code_for(outcome: str) -> int:
    if outcome in PASSING:
        return 0
    if outcome in KNOWN_BLOCKED:
        return 3
    return 1


def describe_release(prior: dict) -> str:
    if not prior.get("release_tag"):
        return ""
    text = f" (release `{prior['release_tag']}` pins `{prior.get('release_image')}` at `{prior.get('release_digest')}`"
    if prior.get("tag_moved_since_release"):
        text += f"; that tag now resolves to `{prior.get('tag_digest_now')}`, so the release digest is used"
    return text + ")"


def render_summary(receipt: dict) -> str:
    prior = receipt.get("prior") or {}
    lines = [
        "### N-1 reader compatibility",
        "",
        f"- Outcome: `{receipt.get('outcome')}`: {receipt.get('explanation', '')}",
        f"- Prior source: `{receipt.get('priorSource', 'unresolved')}`{describe_release(prior)}",
        f"- Prior image: `{prior.get('reference', 'unresolved')}`, "
        f"revision `{prior.get('revision', 'n/a')}`, schema {prior.get('schema', 'n/a')}, reader guard: "
        f"`{prior.get('has_reader_guard', 'n/a')}`, reader declarations: `{prior.get('has_reader_declarations', 'n/a')}`",
        f"- Candidate: `{receipt.get('candidate_sha', 'n/a')}` (schema {receipt.get('candidate_schema', 'n/a')})",
        f"- Prior ready: `{receipt.get('prior_ready')}`; seeded read/write: `{receipt.get('serve_ok')}`",
        "",
    ]
    delta = receipt.get("journal_delta") or []
    if delta:
        lines += ["| Journal entry the prior image does not ship | Classification | Added by this change | Refused |",
                  "|---|---|---|---|"]
        refused = {basename_of(n) for n in ((receipt.get("refusal") or {}).get("scripts") or [])}
        for script in delta:
            lines.append(
                f"| `{script}` | `{(receipt.get('classifications') or {}).get(script, 'undeclared')}` | "
                f"{'yes' if script in (receipt.get('pr_added') or []) else 'no'} | "
                f"{'yes' if script in refused else 'no'} |")
    else:
        lines.append("The prior image ships every journaled migration; the journal delta is empty.")
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------- side effects


def run(cmd: list[str], *, timeout: int = 120, check: bool = True, input: str | None = None,
        env: dict | None = None, cwd: Path | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=timeout, check=check, input=input, env=env, cwd=cwd)


def docker(*args: str, timeout: int = 120, check: bool = True, input: str | None = None) -> str:
    return run(["docker", *args], timeout=timeout, check=check, input=input).stdout.strip()


def git(*args: str, check: bool = True) -> str:
    return run(["git", *args], cwd=REPO_ROOT, check=check, timeout=300).stdout.strip()


def changed_paths(base_ref: str) -> list[str]:
    merge_base = git("merge-base", base_ref, "HEAD", check=False) or base_ref
    return [p for p in git("diff", "--name-only", merge_base, "HEAD").splitlines() if p]


def added_migrations(base_ref: str) -> set[str]:
    merge_base = git("merge-base", base_ref, "HEAD", check=False) or base_ref
    out = git("diff", "--name-only", "--diff-filter=A", merge_base, "HEAD")
    return {Path(p).name for p in out.splitlines() if p.endswith(".sql") and "/Migrations/" in p}


def migration_paths(revision: str) -> dict[str, str]:
    out = git("ls-tree", "-r", "--name-only", revision, "--", "src")
    result: dict[str, str] = {}
    for path in out.splitlines():
        if "/Migrations/" in path and path.endswith(".sql"):
            result.setdefault(Path(path).name, path)
    return result


def parse_release_server_pin(manifest: str) -> tuple[str, str]:
    """(image, digest) of components.honua-server in a honua-release finalized manifest."""
    block = re.search(r"^  honua-server:[ \t]*\r?\n((?:    .*(?:\r?\n|$))+)", manifest, re.MULTILINE)
    if not block:
        raise ValueError("finalized manifest has no components.honua-server entry")
    image = re.search(r"^    image:\s*(\S+)\s*$", block.group(1), re.MULTILINE)
    digest = re.search(r"^    digest:\s*(sha256:[0-9a-f]{64})\s*$", block.group(1), re.MULTILINE)
    if not image or not digest:
        raise ValueError("components.honua-server has no image and sha256 digest")
    return image.group(1), digest.group(1)


def github_get(url: str, *, accept: str = "application/vnd.github+json") -> bytes:
    request = urllib.request.Request(url, headers={"Accept": accept, "User-Agent": "honua-n1-reader-compat"})
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def latest_release_pin() -> dict:
    """The honua-server image the latest published platform release pins (what a customer runs)."""
    releases = json.loads(github_get(f"https://api.github.com/repos/{RELEASE_REPO}/releases?per_page=20"))
    published = [r for r in releases if not r.get("draft")]
    if not published:
        raise ValueError(f"{RELEASE_REPO} has no published release")
    release = max(published, key=lambda r: r.get("published_at") or "")
    asset = next((a for a in release.get("assets", []) if a.get("name") == RELEASE_MANIFEST_ASSET), None)
    if asset is None:
        raise ValueError(f"release {release.get('tag_name')} has no {RELEASE_MANIFEST_ASSET} asset")
    manifest = github_get(asset["url"], accept="application/octet-stream").decode("utf-8")
    image, digest = parse_release_server_pin(manifest)
    return {"release_tag": release.get("tag_name"), "release_url": release.get("html_url"),
            "release_prerelease": release.get("prerelease"), "release_published_at": release.get("published_at"),
            "release_image": image, "release_digest": digest}


def image_repo(reference: str) -> str:
    name = reference.split("@", 1)[0]
    return name.rsplit(":", 1)[0] if ":" in name.rsplit("/", 1)[-1] else name


def tag_digest(reference: str) -> str:
    raw = docker("buildx", "imagetools", "inspect", reference, "--format", "{{json .Manifest}}", timeout=120)
    return json.loads(raw)["digest"]


def resolve_prior(override: str | None) -> dict:
    """Resolve the N-1 prior by digest: the published release pin first, nightly-aot only as scaffolding."""
    attempts: list[dict] = []
    candidates: list[tuple[str, dict]] = []
    if override:
        candidates.append(("override", {"requested": override}))
    else:
        try:
            pin = latest_release_pin()
            candidates.append(("published-release", {"requested": pin["release_image"], **pin}))
        except (urllib.error.URLError, OSError, ValueError, KeyError) as error:
            attempts.append({"source": "published-release", "reference": RELEASE_REPO, "error": str(error)[:400]})
        candidates.append(("nightly-scaffold", {"requested": NIGHTLY_SCAFFOLD}))
    for source, info in candidates:
        reference = info["requested"]
        try:
            if "release_digest" in info:
                # The release records an immutable digest; the tag it names may have moved since.
                digest = info["release_digest"]
                try:
                    info["tag_digest_now"] = tag_digest(reference)
                except (subprocess.CalledProcessError, subprocess.TimeoutExpired, KeyError, ValueError) as error:
                    info["tag_digest_now"] = None
                    info["tag_error"] = str(getattr(error, "stderr", None) or error).strip()[:300]
                info["tag_moved_since_release"] = info["tag_digest_now"] not in (None, digest)
            elif "@sha256:" in reference:
                digest = reference.split("@", 1)[1]
            else:
                digest = tag_digest(reference)
            pinned = f"{image_repo(reference)}@{digest}"
            docker("pull", "--quiet", "--platform", "linux/amd64", pinned, timeout=900)
            image = json.loads(docker("image", "inspect", pinned))[0]
            labels = image["Config"].get("Labels") or {}
            return {"source": source, **info, "reference": pinned, "digest": digest,
                    "image_id": image["Id"], "revision": labels.get("org.opencontainers.image.revision"),
                    "created": labels.get("org.opencontainers.image.created"),
                    "compilation": labels.get("honua.runtime.compilation"), "attempts": attempts}
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, KeyError, ValueError, IndexError) as error:
            detail = getattr(error, "stderr", None) or str(error)
            attempts.append({"source": source, "reference": reference, "error": str(detail).strip()[:400]})
    raise GateError("prior-unresolved", "could not resolve any prior image: " + json.dumps(attempts))


def ensure_revision(revision: str) -> None:
    if run(["git", "cat-file", "-e", f"{revision}^{{commit}}"], cwd=REPO_ROOT, check=False).returncode == 0:
        return
    fetch = run(["git", "fetch", "--no-tags", "--depth=1", "origin", revision], cwd=REPO_ROOT, check=False, timeout=600)
    if fetch.returncode != 0:
        raise GateError("infrastructure-error", f"cannot fetch prior revision {revision}: {fetch.stderr.strip()[:400]}")


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def postgis_image() -> str:
    fixture = (REPO_ROOT / "tests/dotnet/Honua.TestKit.Postgres/PostgresFixture.cs").read_text(encoding="utf-8")
    match = re.search(r"postgis/postgis:[0-9]+(?:\.[0-9]+)*-[0-9]+(?:\.[0-9]+)*", fixture)
    if not match:
        raise GateError("infrastructure-error", "cannot resolve the PostGIS image from PostgresFixture.cs")
    return match.group(0)


def psql(pg: str, sql: str, *, timeout: int = 300) -> str:
    return docker("exec", "-i", pg, "psql", "-v", "ON_ERROR_STOP=1", "-At", "-U", "honua", "-d", "honua",
                  input=sql, timeout=timeout)


def wait_postgis(pg: str) -> None:
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        # The entrypoint's temporary init server also answers pg_isready, so wait for
        # the final server and then require two consecutive successful probes.
        result = run(["docker", "logs", pg], check=False)
        logs = result.stdout + result.stderr
        if "PostgreSQL init process complete; ready for start up." in logs:
            ok = 0
            while ok < 2 and time.monotonic() < deadline:
                ready = run(["docker", "exec", pg, "pg_isready", "-U", "honua", "-d", "honua"], check=False)
                ok = ok + 1 if ready.returncode == 0 else 0
                time.sleep(1)
            if ok >= 2:
                return
        time.sleep(1)
    raise GateError("infrastructure-error", "PostGIS did not become ready within 180s")


def build_candidate() -> Path:
    dll = REPO_ROOT / "src/Honua.Server/bin/Release/net10.0/Honua.Server.dll"
    if not dll.exists():
        print("Building src/Honua.Server (Release) for the candidate migration run.", file=sys.stderr)
        run(["dotnet", "build", "src/Honua.Server/Honua.Server.csproj", "-c", "Release", "-nologo"],
            cwd=REPO_ROOT, timeout=3600)
    return dll


def migrate_candidate(dll: Path, db_port: int, output: Path) -> dict:
    env = dict(os.environ)
    for key in list(env):
        if key.upper().startswith(("HONUA_SKIP_MIGRATIONS", "HONUA_APPROVE_CONTRACT", "DATABASE__MIGRATIONSAFETY__")):
            env.pop(key)
    env.update({
        "HONUA_MIGRATE_ONLY": "true",
        "ASPNETCORE_ENVIRONMENT": "Production",
        "DOTNET_ENVIRONMENT": "Production",
        "ConnectionStrings__DefaultConnection":
            f"Host=127.0.0.1;Port={db_port};Database=honua;Username=honua;Password=honua;GSS Encryption Mode=Disable",
    })
    result = run(["dotnet", str(dll)], env=env, cwd=dll.parent, check=False, timeout=1200)
    (output / "candidate-migrate.log").write_text(result.stderr + "\n--- stdout ---\n" + result.stdout, encoding="utf-8")
    summary = {}
    for line in reversed(result.stdout.splitlines()):
        if line.strip().startswith("{"):
            try:
                summary = json.loads(line)
                break
            except json.JSONDecodeError:
                continue
    if result.returncode != 0:
        raise GateError("candidate-migration-failed",
                        f"candidate migrate-only exited {result.returncode}; see candidate-migrate.log")
    return summary


def http(method: str, url: str, *, data: dict | None = None, timeout: int = 15) -> tuple[int, str]:
    body = urllib.parse.urlencode(data).encode() if data is not None else None
    request = urllib.request.Request(url, data=body, method=method, headers={"X-API-Key": ADMIN_KEY})
    if body is not None:
        request.add_header("Content-Type", "application/x-www-form-urlencoded")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")
    except (urllib.error.URLError, OSError) as error:
        return 0, str(error)


def feature_count(base: str) -> int | None:
    status, body = http("GET", f"{base}/rest/services/{SEED_SERVICE}/FeatureServer/{SEED_LAYER}/query"
                               "?where=1%3D1&returnCountOnly=true&f=json")
    if status != 200:
        return None
    try:
        return int(json.loads(body)["count"])
    except (ValueError, KeyError, TypeError):
        return None


def serve_checks(base: str, receipt: dict) -> bool:
    checks: dict = {}
    before = feature_count(base)
    checks["read_count_before"] = before
    marker = f"n1-reader-compat-{uuid.uuid4().hex[:8]}"
    adds = [{"geometry": {"x": -122.41, "y": 37.77, "spatialReference": {"wkid": 4326}},
             "attributes": {"name": marker, "description": "N-1 reader compatibility write"}}]
    status, body = http("POST", f"{base}/rest/services/{SEED_SERVICE}/FeatureServer/{SEED_LAYER}/applyEdits",
                        data={"f": "json", "adds": json.dumps(adds)})
    checks["write_status"] = status
    checks["write_body"] = body[:600]
    write_ok = False
    try:
        results = json.loads(body).get("addResults") or []
        write_ok = status == 200 and len(results) == 1 and results[0].get("success") is True
    except (ValueError, AttributeError):
        write_ok = False
    after = feature_count(base)
    checks["read_count_after"] = after
    status, body = http("GET", f"{base}/rest/services/{SEED_SERVICE}/FeatureServer/{SEED_LAYER}/query?"
                        + urllib.parse.urlencode({"where": f"name = '{marker}'", "returnCountOnly": "true", "f": "json"}))
    try:
        checks["read_back_count"] = int(json.loads(body)["count"]) if status == 200 else None
    except (ValueError, KeyError, TypeError):
        checks["read_back_count"] = None
    receipt["serve_checks"] = checks
    return (before is not None and before > 0 and write_ok
            and after == before + 1 and checks["read_back_count"] == 1)


def start_prior(prior: dict, network: str, pg: str, name: str, port: int) -> None:
    docker("run", "-d", "--name", name, "--network", network, "-p", f"127.0.0.1:{port}:8080",
           "--memory", "2g", "--log-opt", "max-size=20m",
           "-e", "ASPNETCORE_URLS=http://+:8080",
           "-e", "Kestrel__Endpoints__Http__Url=http://+:8080",
           # Same serving configuration the shared setup-honua-server image mode uses for
           # out-of-process integration lanes; migrations stay at their default (enabled).
           "-e", "ASPNETCORE_ENVIRONMENT=Development",
           "-e", f"ConnectionStrings__DefaultConnection=Host={pg};Database=honua;Username=honua;Password=honua",
           "-e", f"HONUA_ADMIN_PASSWORD={ADMIN_KEY}",
           "-e", "Licensing__DevGrantEdition=Pro",
           "-e", "Authentication__PortalToken__RequireHttps=false",
           "-e", "Security__ConnectionEncryption__MasterKey=test-master-key-that-is-at-least-32-characters-long-for-security",
           "-e", "Security__ConnectionEncryption__Salt=dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=",
           prior["reference"])


def wait_prior(name: str, base: str, timeout: int) -> tuple[bool, str]:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status, body = http("GET", f"{base}/healthz/ready", timeout=5)
        if status == 200 and body.strip() == "Ready":
            return True, "ready"
        state = docker("inspect", "-f", "{{.State.Running}} {{.State.ExitCode}}", name, check=False)
        if not state.startswith("true"):
            return False, f"exited ({state})"
        time.sleep(2)
    return False, "timeout"


def container_logs(name: str) -> str:
    result = run(["docker", "logs", name], check=False, timeout=60)
    return result.stdout + result.stderr


def execute(args: argparse.Namespace, receipt: dict, output: Path) -> Evidence:
    suffix = uuid.uuid4().hex[:10]
    network, pg, server = (f"honua-n1-{kind}-{suffix}" for kind in ("net", "pg", "prior"))
    receipt["candidate_sha"] = args.candidate_sha or git("rev-parse", "HEAD")
    receipt["candidate_head_sha"] = args.candidate_head_sha
    receipt["base_ref"] = args.base_ref
    prior = resolve_prior(args.prior_image)
    receipt["prior"] = prior
    receipt["priorSource"] = prior["source"]
    if not prior.get("revision") or not re.fullmatch(r"[0-9a-f]{40}", prior["revision"]):
        raise GateError("prior-unresolved", "prior image carries no org.opencontainers.image.revision label")
    ensure_revision(prior["revision"])
    prior_scripts = migration_paths(prior["revision"])
    candidate_scripts = migration_paths("HEAD")
    prior["schema"] = max((int(n[:3]) for n in prior_scripts if n[:3].isdigit() and "/Honua.Server/" in prior_scripts[n]), default=None)
    prior["has_reader_declarations"] = READER_DECLARATIONS_MIGRATION in prior_scripts
    prior["has_reader_guard"] = bool(git("ls-tree", "--name-only", prior["revision"], "--", READER_GUARD_SOURCE))
    receipt["candidate_schema"] = max((int(n[:3]) for n in candidate_scripts if n[:3].isdigit() and "/Honua.Server/" in candidate_scripts[n]), default=None)
    pr_added = added_migrations(args.base_ref)
    receipt["pr_added"] = sorted(pr_added)

    dll = Path(args.candidate_dll).resolve() if args.candidate_dll else build_candidate()
    if not dll.exists():
        raise GateError("infrastructure-error", f"candidate build {dll} does not exist")
    db_port = free_port()
    http_port = free_port()
    try:
        docker("network", "create", network)
        docker("run", "-d", "--name", pg, "--network", network, "-p", f"127.0.0.1:{db_port}:5432",
               "-e", "POSTGRES_USER=honua", "-e", "POSTGRES_PASSWORD=honua", "-e", "POSTGRES_DB=honua",
               "-e", "POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL", postgis_image(), "-c", "max_connections=200",
               timeout=900)
        wait_postgis(pg)
        psql(pg, "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; "
                 "CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;")
        receipt["candidate_migrate_summary"] = migrate_candidate(dll, db_port, output)
        journal = [line for line in psql(pg, "SELECT scriptname FROM public.schema_versions ORDER BY schemaversionsid;").splitlines() if line]
        (output / "journal.json").write_text(json.dumps(journal, indent=2) + "\n", encoding="utf-8")
        delta = [basename_of(name) for name in journal if basename_of(name) not in prior_scripts]
        receipt["journal_rows"] = len(journal)
        receipt["journal_delta"] = delta
        classifications: dict[str, str] = {}
        has_table = psql(pg, "SELECT to_regclass('public.schema_version_reader_compatibility') IS NOT NULL;") == "t"
        receipt["candidate_writes_declarations"] = has_table
        if has_table:
            rows = psql(pg, "SELECT script_name || '|' || classification || '|' || min_reader_schema "
                            "FROM public.schema_version_reader_compatibility ORDER BY script_name;")
            declared = {}
            for row in rows.splitlines():
                name, classification, floor = row.split("|")
                declared[basename_of(name)] = {"classification": classification, "min_reader_schema": int(floor)}
            receipt["declarations"] = {name: declared[name] for name in delta if name in declared}
            classifications = {name: declared[name]["classification"] for name in delta if name in declared}
        script_text = {}
        for name in delta:
            path = candidate_scripts.get(name)
            text = (REPO_ROOT / path).read_text(encoding="utf-8") if path else ""
            script_text[name] = text
            if name not in classifications:
                classifications[name] = "reader-incompatible" if READER_INCOMPATIBLE.search(text) else "undeclared"
        receipt["classifications"] = classifications
        seed = (REPO_ROOT / SEED_SQL).read_text(encoding="utf-8")
        psql(pg, seed, timeout=600)

        start_prior(prior, network, pg, server, http_port)
        receipt["prior_started"] = True
        ready, why = wait_prior(server, f"http://127.0.0.1:{http_port}", args.timeout)
        receipt["prior_ready"] = ready
        receipt["prior_wait"] = why
        serve_ok = serve_checks(f"http://127.0.0.1:{http_port}", receipt) if ready else False
        receipt["serve_ok"] = serve_ok
        logs = container_logs(server)
        (output / "prior.log").write_text(logs, encoding="utf-8")
        refusal = parse_refusal(logs)
        receipt["refusal"] = refusal
        journal_after = [line for line in psql(pg, "SELECT scriptname FROM public.schema_versions ORDER BY schemaversionsid;").splitlines() if line]
        journal_changed = journal_after != journal
        receipt["journal_unchanged_by_prior"] = not journal_changed
        doc_path = REPO_ROOT / ROLLBACK_DOC
        return Evidence(journal_delta=delta, classifications=classifications, pr_added=pr_added,
                        prior_has_reader_declarations=prior["has_reader_declarations"],
                        prior_has_reader_guard=prior["has_reader_guard"], refusal=refusal,
                        ready=ready, serve_ok=serve_ok, script_text=script_text,
                        rollback_doc=doc_path.read_text(encoding="utf-8") if doc_path.exists() else "",
                        journal_changed=journal_changed)
    finally:
        if receipt.get("prior_started"):
            (output / "prior.log").write_text(container_logs(server), encoding="utf-8")
        pg_logs = run(["docker", "logs", pg], check=False, timeout=60)
        (output / "postgis.log").write_text(pg_logs.stdout + pg_logs.stderr, encoding="utf-8")
        if not args.keep:
            for command in (("rm", "-f", server), ("rm", "-f", pg), ("network", "rm", network)):
                run(["docker", *command], check=False, timeout=120)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--output", type=Path, default=REPO_ROOT / "tests/TestResults/n1-reader-compat")
    parser.add_argument("--candidate-dll", help="built Honua.Server.dll; default builds src/Honua.Server Release")
    parser.add_argument("--candidate-sha", help="recorded candidate SHA (default: git HEAD)")
    parser.add_argument("--candidate-head-sha", help="pull-request head SHA when HEAD is a merge ref")
    parser.add_argument("--prior-image", help="explicit prior image reference (tag or @sha256 digest)")
    parser.add_argument("--base-ref", default="origin/trunk", help="diff base for 'added by this change'")
    parser.add_argument("--timeout", type=int, default=240, help="seconds to wait for the prior image")
    parser.add_argument("--keep", action="store_true", help="leave containers running for inspection")
    parser.add_argument("--applies", action="store_true",
                        help="only print applies=true|false for the diff against --base-ref")
    parser.add_argument("--github-output", action="store_true", help="with --applies, also append to $GITHUB_OUTPUT")
    args = parser.parse_args(argv)

    if args.applies:
        try:
            paths = changed_paths(args.base_ref)
        except subprocess.CalledProcessError as error:
            # Any doubt answers true: a wrong skip is the failure this gate exists to prevent.
            print(f"applies=true\nreason=cannot diff against {args.base_ref}: {error.stderr.strip()[:200]}")
            paths, verdict = [], "true"
        else:
            hits = [p for p in paths if path_applies(p)]
            verdict = "true" if hits else "false"
            reason = ("touches " + ", ".join(hits[:8])) if hits else "no migration, runner, guard or gate path changed"
            print(f"applies={verdict}\nreason={reason}")
        if args.github_output and os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as handle:
                handle.write(f"applies={verdict}\n")
        return 0

    if shutil.which("docker") is None:
        print("::error::n1-reader-compat requires Docker.", file=sys.stderr)
        return 1
    output: Path = args.output
    output.mkdir(parents=True, exist_ok=True)
    receipt: dict = {"schema": RECEIPT_SCHEMA, "started_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                     "executed": False, "prior_started": False}
    try:
        evidence = execute(args, receipt, output)
        receipt["executed"] = True
        outcome, explanation = classify(evidence)
    except GateError as error:
        outcome, explanation = error.outcome, str(error)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, OSError) as error:
        detail = getattr(error, "stderr", None) or ""
        outcome, explanation = "infrastructure-error", f"{error} {str(detail).strip()[:600]}"
    receipt["outcome"] = outcome
    receipt["explanation"] = explanation
    receipt["exit_code"] = exit_code_for(outcome)
    receipt["finished_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    summary = render_summary(receipt)
    (output / "summary.md").write_text(summary, encoding="utf-8")
    print(summary)
    level = "notice" if outcome in PASSING else ("warning" if outcome in KNOWN_BLOCKED else "error")
    print(f"::{level}::HONUA_N1_READER_COMPAT outcome={outcome}: {explanation}")
    return receipt["exit_code"]


if __name__ == "__main__":
    sys.exit(main())
