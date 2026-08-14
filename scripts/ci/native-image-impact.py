#!/usr/bin/env python3
"""Report-only native-image impact routing.

The selector derives managed inputs from the ProjectReference closure instead
of maintaining another list of source directories.  It emits evidence only;
neither native-image workflow reads this report while policy mode is
``observe``.
"""

from __future__ import annotations

import argparse
import fnmatch
import functools
import hashlib
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_POLICY = ROOT / ".github" / "native-image-impact.json"
SCHEMA = "honua.ci.native-image-impact-observation/v2"
TRUSTED_EXECUTION = "default-branch-workflow-run/v1"
SHA_PATTERN = re.compile(r"[0-9a-f]{40}")
TERMINAL_CONCLUSIONS = {
    "success",
    "failure",
    "cancelled",
    "timed_out",
    "action_required",
    "neutral",
    "skipped",
    "stale",
    "startup_failure",
}
RISK_CLASSES = (
    "server_aot_compile",
    "generic_final_rootfs",
    "lambda_final_rootfs",
    "functions_final_rootfs",
    "worker_managed_graph",
    "worker_native_rootfs",
    "worker_vulnerability",
)


class PolicyError(RuntimeError):
    """The checked-in routing policy cannot be evaluated safely."""


def normalize_path(value: str | Path) -> str:
    text = str(value).replace("\\", "/")
    while text.startswith("./"):
        text = text[2:]
    return PurePosixPath(text).as_posix().strip("/")


def path_matches(path: str, pattern: str) -> bool:
    path = normalize_path(path)
    pattern = normalize_path(pattern)
    if pattern.endswith("/**"):
        prefix = pattern[:-3].rstrip("/")
        return path == prefix or path.startswith(f"{prefix}/")
    return fnmatch.fnmatchcase(path, pattern)


def matching_paths(paths: Iterable[str], patterns: Iterable[str]) -> list[str]:
    normalized = sorted({normalize_path(path) for path in paths if normalize_path(path)})
    return [
        path
        for path in normalized
        if any(path_matches(path, pattern) for pattern in patterns)
    ]


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _resolve_repo_path(repo: Path, owner: Path, include: str) -> str | None:
    include = include.replace("\\", "/")
    if not include or any(token in include for token in ("$", "*", "?", "%")):
        return None
    candidate = (owner / include).resolve()
    try:
        return candidate.relative_to(repo.resolve()).as_posix()
    except ValueError:
        raise PolicyError(f"project input escapes repository: {include}") from None


@functools.lru_cache(maxsize=None)
def project_inputs(repo: Path, project: str) -> tuple[frozenset[str], frozenset[str]]:
    project_path = repo / project
    if not project_path.is_file():
        raise PolicyError(f"project does not exist: {project}")
    try:
        root = ET.parse(project_path).getroot()
    except ET.ParseError as error:
        raise PolicyError(f"invalid project XML {project}: {error}") from error

    references: set[str] = set()
    external_inputs: set[str] = set()
    for element in root.iter():
        name = _local_name(element.tag)
        include = element.attrib.get("Include", "")
        if not include:
            continue
        resolved = _resolve_repo_path(repo, project_path.parent, include)
        if not resolved:
            continue
        if name == "ProjectReference":
            references.add(resolved)
        # `None` items are deliberately excluded: several library projects pack
        # the repository README into NuGet packages, but dotnet publish does not
        # copy it into either serving image. Content/embedded/analyzer inputs can
        # affect compilation or the published rootfs and remain conservative.
        elif name in {"AdditionalFiles", "Content", "EmbeddedResource"}:
            project_dir = normalize_path(project_path.parent.relative_to(repo))
            if not (resolved == project_dir or resolved.startswith(f"{project_dir}/")):
                external_inputs.add(resolved)
    return frozenset(references), frozenset(external_inputs)


def project_closure(
    repo: Path, root_project: str, global_projects: Iterable[str]
) -> tuple[list[str], list[str]]:
    projects, external_inputs = _project_closure_cached(
        repo.resolve(),
        normalize_path(root_project),
        tuple(sorted(map(normalize_path, global_projects))),
    )
    return list(projects), list(external_inputs)


@functools.lru_cache(maxsize=None)
def _project_closure_cached(
    repo: Path, root_project: str, global_projects: tuple[str, ...]
) -> tuple[tuple[str, ...], tuple[str, ...]]:
    pending = [root_project, *global_projects]
    projects: set[str] = set()
    external_inputs: set[str] = set()
    while pending:
        project = pending.pop()
        if project in projects:
            continue
        references, external = project_inputs(repo, project)
        projects.add(project)
        external_inputs.update(external)
        pending.extend(sorted(references - projects))
    return tuple(sorted(projects)), tuple(sorted(external_inputs))


def graph_matching_paths(
    paths: Iterable[str], projects: Iterable[str], external_inputs: Iterable[str]
) -> list[str]:
    project_dirs = sorted({str(PurePosixPath(project).parent) for project in projects})
    patterns = [*external_inputs]
    matched: list[str] = []
    for raw_path in paths:
        path = normalize_path(raw_path)
        if any(
            path == directory or path.startswith(f"{directory}/")
            for directory in project_dirs
        ) or any(path_matches(path, pattern) for pattern in patterns):
            matched.append(path)
    return sorted(set(matched))


def _fingerprint(projects: Iterable[str], external_inputs: Iterable[str]) -> str:
    payload = json.dumps(
        {"projects": sorted(projects), "external_inputs": sorted(external_inputs)},
        sort_keys=True,
        separators=(",", ":"),
    ).encode()
    return f"sha256:{hashlib.sha256(payload).hexdigest()}"


def load_policy(path: Path = DEFAULT_POLICY) -> dict[str, Any]:
    try:
        policy = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PolicyError(f"cannot load {path}: {error}") from error
    if policy.get("schema") != "honua.ci.native-image-impact-policy/v1":
        raise PolicyError("unsupported native-image impact policy schema")
    if policy.get("mode") != "observe":
        raise PolicyError("native-image impact policy must remain observe-only")
    return policy


def _legacy_paths_from_workflow(path: Path) -> list[str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    in_pull_request = False
    in_paths = False
    paths: list[str] = []
    for line in lines:
        if line == "  pull_request:":
            in_pull_request = True
            in_paths = False
            continue
        if in_pull_request and re.match(r"^  [A-Za-z_].*:$", line):
            break
        if in_pull_request and line == "    paths:":
            in_paths = True
            continue
        if in_paths:
            match = re.match(r"^      - ['\"]([^'\"]+)['\"]\s*$", line)
            if match:
                paths.append(match.group(1))
            elif line.strip() and not line.lstrip().startswith("#"):
                break
    if not paths:
        raise PolicyError(f"could not extract pull_request paths from {path}")
    return paths


def validate_policy(repo: Path, policy: dict[str, Any]) -> None:
    required = {
        "roots",
        "global_projects",
        "common_managed_patterns",
        "serving_external_patterns",
        "serving_shared_patterns",
        "serving_variant_patterns",
        "worker_native_patterns",
        "worker_vulnerability_patterns",
        "legacy",
    }
    missing = sorted(required - policy.keys())
    if missing:
        raise PolicyError(f"policy is missing keys: {', '.join(missing)}")
    variants = policy["serving_variant_patterns"]
    if set(variants) != {"generic", "lambda", "functions"}:
        raise PolicyError("serving variants must be generic, lambda, and functions")
    for name, patterns in {
        "common_managed_patterns": policy["common_managed_patterns"],
        "serving_external_patterns": policy["serving_external_patterns"],
        "serving_shared_patterns": policy["serving_shared_patterns"],
        "worker_native_patterns": policy["worker_native_patterns"],
        "worker_vulnerability_patterns": policy["worker_vulnerability_patterns"],
        **{f"serving_variant_patterns.{key}": value for key, value in variants.items()},
    }.items():
        if not isinstance(patterns, list) or not patterns or len(patterns) != len(set(patterns)):
            raise PolicyError(f"{name} must be a non-empty duplicate-free list")

    project_closure(repo, policy["roots"]["serving"], policy["global_projects"])
    project_closure(repo, policy["roots"]["worker"], policy["global_projects"])

    legacy = policy["legacy"]
    for kind in ("serving", "worker"):
        workflow = repo / legacy[f"{kind}_workflow"]
        actual = _legacy_paths_from_workflow(workflow)
        expected = legacy[f"{kind}_patterns"]
        if actual != expected:
            raise PolicyError(
                f"{kind} legacy trigger drift: workflow paths no longer match policy"
            )

    observer = (repo / ".github/workflows/native-image-impact-observe.yml").read_text(
        encoding="utf-8"
    )
    if "  workflow_run:" not in observer or "    workflows: [PR Gate]" not in observer:
        raise PolicyError("observer must execute after the canonical PR Gate")
    if re.search(r"(?m)^  pull_request:\s*$", observer):
        raise PolicyError("candidate-controlled pull_request observer is forbidden")
    if re.search(r"(?m)^\s{4}paths(?:-ignore)?:", observer):
        raise PolicyError("observer must run on every pull request without path filters")
    required_trust_markers = (
        "trusted-pr-workflow-run",
        "context.ref !== trustedRef",
        "Checkout candidate tree as inert data",
        "persist-credentials: false",
        "default-branch-workflow-run/v1",
        "repositoryId: repository.id",
        "policy_inputs_sha256",
        "observer_workflow_blob_sha",
        "Recheck exact PR identity before retaining evidence",
    )
    if any(token not in observer for token in required_trust_markers):
        raise PolicyError("observer lost its trusted execution boundary")
    if re.search(r"(?m)^\s+[a-z-]+: write\s*$", observer):
        raise PolicyError("observe-only workflow gained write permission")
    if "  checks: read" not in observer:
        raise PolicyError("observer cannot read the canonical job check association")
    forbidden = ("docker build", "docker/build-push-action", "gh run cancel", "cancelWorkflowRun")
    if any(token in observer for token in forbidden):
        raise PolicyError("observe-only workflow gained build or cancellation authority")


def evaluate(
    repo: Path,
    policy: dict[str, Any],
    changed_paths: Iterable[str],
    *,
    base_sha: str = "",
    head_sha: str = "",
    repository: str = "",
    pull_request: int = 0,
    policy_sha: str = "",
    policy_blob_sha: str = "",
    routing_policy_blob_sha: str = "",
    resolver_blob_sha: str = "",
    observer_workflow_blob_sha: str = "",
    policy_inputs_sha256: str = "",
    trusted_execution: str = "",
    gate_workflow_path: str = "",
    gate_run_id: int = 0,
    gate_run_attempt: int = 0,
    gate_run_conclusion: str = "",
) -> dict[str, Any]:
    paths = sorted({normalize_path(path) for path in changed_paths if normalize_path(path)})
    serving_projects, serving_external = project_closure(
        repo, policy["roots"]["serving"], policy["global_projects"]
    )
    worker_projects, worker_external = project_closure(
        repo, policy["roots"]["worker"], policy["global_projects"]
    )
    serving_external = sorted(
        set(serving_external) | set(policy["serving_external_patterns"])
    )

    serving_graph_hits = graph_matching_paths(paths, serving_projects, serving_external)
    worker_graph_hits = graph_matching_paths(paths, worker_projects, worker_external)
    common_hits = matching_paths(paths, policy["common_managed_patterns"])
    serving_shared_hits = matching_paths(paths, policy["serving_shared_patterns"])
    variant_hits = {
        name: matching_paths(paths, patterns)
        for name, patterns in policy["serving_variant_patterns"].items()
    }
    worker_native_hits = matching_paths(paths, policy["worker_native_patterns"])
    worker_vulnerability_hits = matching_paths(
        paths, policy["worker_vulnerability_patterns"]
    )

    reasons: dict[str, list[str]] = {
        "server_aot_compile": sorted(set(serving_graph_hits + common_hits)),
        "worker_managed_graph": sorted(set(worker_graph_hits + common_hits)),
    }
    for variant in ("generic", "lambda", "functions"):
        reasons[f"{variant}_final_rootfs"] = sorted(
            set(
                reasons["server_aot_compile"]
                + serving_shared_hits
                + variant_hits[variant]
            )
        )
    reasons["worker_native_rootfs"] = sorted(
        set(reasons["worker_managed_graph"] + worker_native_hits)
    )
    reasons["worker_vulnerability"] = sorted(
        set(reasons["worker_native_rootfs"] + worker_vulnerability_hits)
    )
    risk_classes = {name: bool(reasons[name]) for name in RISK_CLASSES}
    serving_variants = {
        name: risk_classes[f"{name}_final_rootfs"]
        for name in ("generic", "lambda", "functions")
    }
    worker_build = any(
        risk_classes[name]
        for name in ("worker_managed_graph", "worker_native_rootfs", "worker_vulnerability")
    )
    legacy_serving = bool(matching_paths(paths, policy["legacy"]["serving_patterns"]))
    legacy_worker = bool(matching_paths(paths, policy["legacy"]["worker_patterns"]))
    candidate_serving = any(serving_variants.values())

    return {
        "schema": SCHEMA,
        "mode": "observe",
        "mutation": "none",
        "repository": repository,
        "pull_request": pull_request,
        "base_sha": base_sha,
        "head_sha": head_sha,
        "changed_paths": paths,
        "changed_paths_sha256": hashlib.sha256(
            json.dumps(paths, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        ).hexdigest(),
        "policy_sha": policy_sha,
        "policy_blob_sha": policy_blob_sha,
        "routing_policy_blob_sha": routing_policy_blob_sha,
        "resolver_blob_sha": resolver_blob_sha,
        "observer_workflow_blob_sha": observer_workflow_blob_sha,
        "policy_inputs_sha256": policy_inputs_sha256,
        "trusted_execution": trusted_execution,
        "gate_workflow_path": gate_workflow_path,
        "gate_run_id": gate_run_id,
        "gate_run_attempt": gate_run_attempt,
        "gate_run_head_sha": head_sha,
        "gate_run_conclusion": gate_run_conclusion,
        "graphs": {
            "serving": {
                "root": policy["roots"]["serving"],
                "projects": serving_projects,
                "external_inputs": serving_external,
                "fingerprint": _fingerprint(serving_projects, serving_external),
            },
            "worker": {
                "root": policy["roots"]["worker"],
                "projects": worker_projects,
                "external_inputs": worker_external,
                "fingerprint": _fingerprint(worker_projects, worker_external),
            },
        },
        "legacy": {
            "serving_trigger": legacy_serving,
            "worker_trigger": legacy_worker,
        },
        "candidate": {
            "risk_classes": risk_classes,
            "serving_variants": serving_variants,
            "worker_build": worker_build,
            "reasons": reasons,
        },
        "comparison": {
            "serving_legacy_only": legacy_serving and not candidate_serving,
            "worker_legacy_only": legacy_worker and not worker_build,
            "serving_candidate_only": candidate_serving and not legacy_serving,
            "worker_candidate_only": worker_build and not legacy_worker,
        },
    }


def parse_changed_path_output(raw: bytes) -> list[str]:
    if not raw:
        return []
    if not raw.endswith(b"\0"):
        raise PolicyError("git diff emitted a truncated path record")
    paths: list[str] = []
    seen: set[str] = set()
    for encoded in raw[:-1].split(b"\0"):
        try:
            path = encoded.decode("utf-8")
        except UnicodeDecodeError as error:
            raise PolicyError("git diff emitted a non-UTF-8 path") from error
        if (
            not path
            or "\\" in path
            or any(ord(character) < 32 or ord(character) == 127 for character in path)
            or PurePosixPath(path).is_absolute()
            or ".." in PurePosixPath(path).parts
            or PurePosixPath(path).as_posix() != path
            or path in seen
        ):
            raise PolicyError(f"git diff emitted an unsafe path: {path!r}")
        seen.add(path)
        paths.append(path)
    return paths


def changed_paths(repo: Path, base_ref: str, head_ref: str) -> list[str]:
    result = subprocess.run(
        [
            "git",
            "diff",
            "--name-only",
            "-z",
            "--diff-filter=ACDMRTUXB",
            f"{base_ref}...{head_ref}",
        ],
        cwd=repo,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        raise PolicyError(f"git diff failed: {detail}")
    return parse_changed_path_output(result.stdout)


def observation_identity(args: argparse.Namespace) -> dict[str, Any]:
    values = {
        "base_sha": args.base,
        "head_sha": args.head,
        "policy_sha": args.policy_sha,
        "policy_blob_sha": args.policy_blob_sha,
        "routing_policy_blob_sha": args.routing_policy_blob_sha,
        "resolver_blob_sha": args.resolver_blob_sha,
        "observer_workflow_blob_sha": args.observer_workflow_blob_sha,
    }
    if any(SHA_PATTERN.fullmatch(value or "") is None for value in values.values()):
        raise PolicyError("observation identity contains an invalid SHA")
    if args.repository != "honua-io/honua-server" or args.pr < 1:
        raise PolicyError("observation repository or pull request is invalid")
    if args.trusted_execution != TRUSTED_EXECUTION:
        raise PolicyError("observation did not run through trusted default-branch policy")
    if args.gate_workflow_path != ".github/workflows/pr-gate.yml":
        raise PolicyError("observation source is not the canonical PR Gate")
    if args.gate_run_id < 1 or args.gate_run_attempt < 1:
        raise PolicyError("observation source run identity is invalid")
    if args.gate_run_conclusion not in TERMINAL_CONCLUSIONS:
        raise PolicyError("observation source run conclusion is invalid")
    if re.fullmatch(r"[0-9a-f]{64}", args.policy_inputs_sha256 or "") is None:
        raise PolicyError("observation policy input digest is invalid")
    return {
        "policy_sha": args.policy_sha,
        "policy_blob_sha": args.policy_blob_sha,
        "routing_policy_blob_sha": args.routing_policy_blob_sha,
        "resolver_blob_sha": args.resolver_blob_sha,
        "observer_workflow_blob_sha": args.observer_workflow_blob_sha,
        "policy_inputs_sha256": args.policy_inputs_sha256,
        "trusted_execution": args.trusted_execution,
        "gate_workflow_path": args.gate_workflow_path,
        "gate_run_id": args.gate_run_id,
        "gate_run_attempt": args.gate_run_attempt,
        "gate_run_conclusion": args.gate_run_conclusion,
    }


def markdown(report: dict[str, Any]) -> str:
    candidate = report["candidate"]
    lines = [
        "## Native-image impact observation",
        "",
        f"- Mode: `{report['mode']}` (mutation: `{report['mutation']}`)",
        f"- PR/head: `#{report['pull_request']}` / `{report['head_sha']}`",
        f"- Legacy serving trigger: `{str(report['legacy']['serving_trigger']).lower()}`",
        f"- Candidate serving variants: `{json.dumps(candidate['serving_variants'], sort_keys=True)}`",
        f"- Legacy worker trigger: `{str(report['legacy']['worker_trigger']).lower()}`",
        f"- Candidate worker build: `{str(candidate['worker_build']).lower()}`",
        "",
        "| Risk class | Impacted | Matched inputs |",
        "|---|---:|---|",
    ]
    for name in RISK_CLASSES:
        hits = candidate["reasons"][name]
        rendered = "<br>".join(f"`{path}`" for path in hits[:8]) or "—"
        if len(hits) > 8:
            rendered += f"<br>… +{len(hits) - 8}"
        lines.append(
            f"| `{name}` | `{str(candidate['risk_classes'][name]).lower()}` | {rendered} |"
        )
    lines.extend(
        [
            "",
            "This report is observation-only. Existing image workflows remain authoritative.",
        ]
    )
    return "\n".join(lines) + "\n"


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate")
    observe = subparsers.add_parser("observe")
    observe.add_argument("--repo-root", type=Path, default=ROOT)
    observe.add_argument("--base", required=True)
    observe.add_argument("--head", required=True)
    observe.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", ""))
    observe.add_argument("--pr", type=int, default=0)
    observe.add_argument("--output", type=Path, required=True)
    observe.add_argument("--markdown", type=Path, required=True)
    observe.add_argument("--policy-sha", required=True)
    observe.add_argument("--policy-blob-sha", required=True)
    observe.add_argument("--routing-policy-blob-sha", required=True)
    observe.add_argument("--resolver-blob-sha", required=True)
    observe.add_argument("--observer-workflow-blob-sha", required=True)
    observe.add_argument("--policy-inputs-sha256", required=True)
    observe.add_argument("--trusted-execution", required=True)
    observe.add_argument("--gate-workflow-path", required=True)
    observe.add_argument("--gate-run-id", type=int, required=True)
    observe.add_argument("--gate-run-attempt", type=int, required=True)
    observe.add_argument("--gate-run-conclusion", required=True)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    policy = load_policy(args.policy)
    policy_root = args.policy.resolve().parent.parent
    validate_policy(policy_root, policy)
    if args.command == "validate":
        serving, _ = project_closure(
            policy_root, policy["roots"]["serving"], policy["global_projects"]
        )
        worker, _ = project_closure(
            policy_root, policy["roots"]["worker"], policy["global_projects"]
        )
        print(
            f"native-image-impact=ok mode=observe serving_projects={len(serving)} "
            f"worker_projects={len(worker)}"
        )
        return 0
    repo_root = args.repo_root.resolve()
    identity = observation_identity(args)
    report = evaluate(
        repo_root,
        policy,
        changed_paths(repo_root, args.base, args.head),
        base_sha=args.base,
        head_sha=args.head,
        repository=args.repository,
        pull_request=args.pr,
        **identity,
    )
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    args.markdown.write_text(markdown(report), encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except PolicyError as error:
        print(f"native-image-impact: {error}", file=sys.stderr)
        raise SystemExit(2) from error
