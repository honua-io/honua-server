# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Execute the pre-checkout admission step with controlled GitHub metadata, without a runner pool."""
from __future__ import annotations

import copy
import io
import json
import os
from pathlib import Path
import subprocess
from unittest import mock

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github/workflows/capacity-soak-candidate.yml"
SOURCE = "a" * 40
TRUNK = "b" * 40
REPO = "honua-io/honua-server"
PATH = ".github/workflows/capacity-soak-candidate.yml"


def workflow():
    return yaml.safe_load(WORKFLOW.read_text())


def setup(tmp_path, *, pool="dedicated", ref="refs/heads/trunk"):
    env = {"CAPACITY_POOL": pool, "ACTUAL_RUNNER_ENVIRONMENT": "self-hosted", "CANDIDATE_SHA": SOURCE,
        "APPROVED_RUNNER_GROUP_ID": "17",
        "GITHUB_REPOSITORY": REPO, "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_SHA": SOURCE,
        "WORKFLOW_SHA": SOURCE, "GITHUB_REF": ref, "REF_PROTECTED": "true",
        "WORKFLOW_REF": f"{REPO}/{PATH}@{ref}", "ACTUAL_RUNNER_OS": "Linux", "ACTUAL_RUNNER_ARCH": "X64",
        "ACTUAL_RUNNER_NAME": "capacity-ephemeral-1", "GITHUB_RUN_ID": "100", "GITHUB_RUN_ATTEMPT": "2",
        "RUNNER_METADATA_TOKEN": "metadata-read-secret", "RUNNER_TEMP": str(tmp_path)}
    job = {"name": "Candidate capacity soak", "run_id": 100, "head_sha": SOURCE, "status": "in_progress",
           "runner_name": env["ACTUAL_RUNNER_NAME"], "runner_id": 3, "runner_group_id": 17,
           "runner_group_name": "honua-release-heavy", "labels": ["self-hosted", "Linux", "X64", "honua-release-heavy"]}
    responses = {
        f"repos/{REPO}/branches/trunk": {"protected": True, "commit": {"sha": TRUNK}},
        f"repos/{REPO}/compare/{SOURCE}...{TRUNK}": {"status": "ahead", "merge_base_commit": {"sha": SOURCE}},
        f"repos/{REPO}/actions/runs/100/attempts/2/jobs?per_page=100": {"total_count": 1, "jobs": [job]},
        "orgs/honua-io/actions/runner-groups/17": {"id": 17, "name": "honua-release-heavy",
            "visibility": "selected", "default": False, "restricted_to_workflows": True,
            "selected_workflows": [env["WORKFLOW_REF"]]},
        "orgs/honua-io/actions/runner-groups/17/repositories?per_page=100": {
            "total_count": 1, "repositories": [{"full_name": REPO}]},
    }
    return env, responses


def execute(env, responses, *, outage=False):
    step = workflow()["jobs"]["soak"]["steps"][0]
    code = step["run"].split("<<'PYTHON'\n", 1)[1].rsplit("\nPYTHON", 1)[0]
    calls = []

    def read(request, *, timeout):
        path = request.full_url.removeprefix("https://api.github.com/")
        calls.append(path)
        assert request.get_header("Authorization") == "Bearer metadata-read-secret"
        assert timeout == 30
        if outage:
            raise OSError("transport might contain metadata-read-secret")
        return io.BytesIO(json.dumps(responses[path]).encode())

    with mock.patch.dict(os.environ, env, clear=True), mock.patch("urllib.request.urlopen", side_effect=read):
        exec(compile(code, str(WORKFLOW) + ":admission", "exec"), {"__name__": "__main__"})
    return json.loads((Path(env["RUNNER_TEMP"]) / "capacity-runner-admission.json").read_text()), calls


@pytest.mark.parametrize("ref", ["refs/heads/trunk", "refs/tags/nightly-candidate/" + SOURCE])
def test_dedicated_admission_requires_real_job_group_and_owned_protected_source(tmp_path, ref):
    env, responses = setup(tmp_path, ref=ref)
    report, calls = execute(env, responses)
    assert report["status"] == "pass" and report["pool"] == "dedicated"
    assert report["runnerGroupId"] == 17 and report["sourceRevision"] == SOURCE
    assert report["workflowRef"] == env["WORKFLOW_REF"]
    assert len(calls) == 5
    assert "metadata-read-secret" not in json.dumps(report)


@pytest.mark.parametrize("pool", ["", "hosted"])
def test_hosted_default_does_not_require_group_metadata_or_activate_a_pool(tmp_path, pool):
    env, responses = setup(tmp_path, pool=pool)
    env["ACTUAL_RUNNER_ENVIRONMENT"] = "github-hosted"
    env.pop("RUNNER_METADATA_TOKEN")
    report, calls = execute(env, responses)
    assert report == {"status": "pass", "pool": "hosted"}
    assert calls == []


@pytest.mark.parametrize("key,value", [
    ("CAPACITY_POOL", "custom-labels"), ("ACTUAL_RUNNER_ENVIRONMENT", "github-hosted"),
    ("APPROVED_RUNNER_GROUP_ID", ""), ("APPROVED_RUNNER_GROUP_ID", "18"),
    ("GITHUB_REPOSITORY", "other/repo"), ("GITHUB_EVENT_NAME", "pull_request"),
    ("GITHUB_EVENT_NAME", "pull_request_target"), ("CANDIDATE_SHA", "abc123"),
    ("GITHUB_SHA", "c" * 40), ("WORKFLOW_SHA", "c" * 40),
    ("WORKFLOW_REF", f"{REPO}/.github/workflows/ci.yml@refs/heads/trunk"),
    ("GITHUB_REF", "refs/heads/feature/untrusted"), ("GITHUB_REF", "refs/pull/1/merge"),
    ("GITHUB_REF", "refs/tags/nightly-candidate/" + "c" * 40),
    ("REF_PROTECTED", "false"), ("ACTUAL_RUNNER_OS", "Windows"),
    ("ACTUAL_RUNNER_ARCH", "ARM64"), ("GITHUB_RUN_ID", "invalid"), ("RUNNER_METADATA_TOKEN", ""),
])
def test_unapproved_source_event_runner_or_configuration_refuses_before_checkout(tmp_path, key, value):
    env, responses = setup(tmp_path)
    env[key] = value
    with pytest.raises(SystemExit):
        execute(env, responses)
    report = json.loads((tmp_path / "capacity-runner-admission.json").read_text())
    assert report["status"] == "fail"
    assert "metadata-read-secret" not in json.dumps(report)


@pytest.mark.parametrize("change", [
    "unprotected-trunk", "foreign-ancestry", "two-jobs", "wrong-job", "wrong-runner", "wrong-group",
    "missing-label", "group-all-repos", "default-group", "unrestricted-workflows", "workflow-wildcard",
    "pr-workflow", "other-workflow", "other-repository", "metadata-outage",
])
def test_broad_or_missing_live_metadata_refuses_and_preserves_safe_failure(tmp_path, change):
    env, responses = setup(tmp_path)
    branch = responses[f"repos/{REPO}/branches/trunk"]
    comparison = responses[f"repos/{REPO}/compare/{SOURCE}...{TRUNK}"]
    jobs = responses[f"repos/{REPO}/actions/runs/100/attempts/2/jobs?per_page=100"]
    job = jobs["jobs"][0]
    group = responses["orgs/honua-io/actions/runner-groups/17"]
    repos = responses["orgs/honua-io/actions/runner-groups/17/repositories?per_page=100"]
    if change == "unprotected-trunk": branch["protected"] = False
    if change == "foreign-ancestry": comparison["merge_base_commit"]["sha"] = "f" * 40
    if change == "two-jobs": jobs["jobs"].append(copy.deepcopy(job)); jobs["total_count"] = 2
    if change == "wrong-job": job["name"] = "unrelated job"
    if change == "wrong-runner": job["runner_name"] = "unrelated-host"
    if change == "wrong-group": job["runner_group_name"] = "Default"
    if change == "missing-label": job["labels"].remove("honua-release-heavy")
    if change == "group-all-repos": group["visibility"] = "all"
    if change == "default-group": group["default"] = True
    if change == "unrestricted-workflows": group["restricted_to_workflows"] = False
    if change == "workflow-wildcard": group["selected_workflows"].append(f"{REPO}/{PATH}@refs/tags/*")
    if change == "pr-workflow": group["selected_workflows"].append(f"{REPO}/{PATH}@refs/pull/1/merge")
    if change == "other-workflow": group["selected_workflows"].append(f"{REPO}/.github/workflows/ci.yml@refs/heads/trunk")
    if change == "other-repository": repos["repositories"].append({"full_name": "other/repo"}); repos["total_count"] = 2
    with pytest.raises(SystemExit):
        execute(env, responses, outage=change == "metadata-outage")
    raw = (tmp_path / "capacity-runner-admission.json").read_text()
    assert json.loads(raw)["status"] == "fail"
    assert "metadata-read-secret" not in raw


def test_one_job_fixed_routing_first_step_and_existing_release_contracts_are_retained():
    document = workflow()
    assert set(document["jobs"]) == {"soak"}
    job = document["jobs"]["soak"]
    assert job["name"] == "Candidate capacity soak" and job["timeout-minutes"] == 300
    assert document["concurrency"] == {"group": "capacity-soak-candidate", "cancel-in-progress": False}
    routing = job["runs-on"]
    assert "vars.HONUA_CAPACITY_RUNNER_POOL == 'dedicated'" in routing
    for guard in ("github.repository == 'honua-io/honua-server'", "github.event_name == 'workflow_dispatch'",
                  "github.sha == inputs.candidate_sha", "github.workflow_sha == inputs.candidate_sha",
                  "github.ref_protected", "refs/tags/nightly-candidate/{0}"):
        assert guard in routing
    assert '"group":"honua-release-heavy"' in routing
    assert '"labels":["self-hosted","Linux","X64","honua-release-heavy"]' in routing
    assert "|| 'ubuntu-24.04'" in routing
    assert "inputs.runner" not in routing and "inputs.group" not in routing and "inputs.labels" not in routing
    steps = job["steps"]
    assert steps[0]["id"] == "admission" and "uses" not in steps[0]
    assert "RUNNER_METADATA_TOKEN" in steps[0]["env"]
    assert all("RELEASE_RUNNER_READ_TOKEN" not in json.dumps(step) for step in steps[1:])
    cleanup = next(step for step in steps if step["name"] == "Tear down the substrate")
    assert cleanup["if"] == "always()"
    assert cleanup["env"]["PRODUCER_ADMITTED"] == "${{ steps.admission.outcome == 'success' }}"
    assert cleanup["run"].index('if [ "$PRODUCER_ADMITTED" != true ]') < cleanup["run"].index("cd producer")
    assert "capacity-substrate.sh cleanup" in cleanup["run"]
    assert any("attest-build-provenance@" in step.get("uses", "") for step in steps)


@pytest.mark.parametrize("admitted", [False, True])
def test_always_cleanup_executes_checkout_code_only_after_admission(tmp_path, admitted):
    cleanup = next(step for step in workflow()["jobs"]["soak"]["steps"] if step["name"] == "Tear down the substrate")
    script = tmp_path / "producer/scripts/soak/capacity-substrate.sh"
    script.parent.mkdir(parents=True)
    script.write_text('#!/bin/sh\nprintf called > "$RUNNER_TEMP/cleanup-called"\n')
    script.chmod(0o755)
    result = subprocess.run(["bash", "-eu", "-c", cleanup["run"]], cwd=tmp_path, check=False,
        env={"PATH": os.environ["PATH"], "RUNNER_TEMP": str(tmp_path), "PRODUCER_ADMITTED": str(admitted).lower()},
        text=True, capture_output=True)
    assert result.returncode == 0, result.stderr
    assert (tmp_path / "cleanup-called").exists() is admitted


@pytest.mark.parametrize("mutation", [None, "repository", "default-branch", "unprotected", "missing-head",
                                    "untrusted-source", "ref-mismatch", "comparison-base", "diverged", "api-404"])
def test_downloaded_release_code_requires_exact_protected_trunk_history(monkeypatch, mutation):
    step = next(step for step in workflow()["jobs"]["soak"]["steps"] if step.get("id") == "lock")
    script = step["run"]
    code = script.split("<<'TRUSTED_RELEASE'\n", 1)[1].split("\nTRUSTED_RELEASE", 1)[0]
    repo = "repos/honua-io/honua-release"
    data = {repo: {"full_name": "honua-io/honua-release", "default_branch": "trunk"},
            repo + "/branches/trunk": {"protected": True, "commit": {"sha": TRUNK}},
            repo + "/commits/" + SOURCE: {"sha": SOURCE},
            repo + "/compare/" + SOURCE + "..." + TRUNK: {
                "status": "ahead", "base_commit": {"sha": SOURCE}, "merge_base_commit": {"sha": SOURCE}}}
    comparison = data[repo + "/compare/" + SOURCE + "..." + TRUNK]
    if mutation == "repository": data[repo]["full_name"] = "other/repo"
    if mutation == "default-branch": data[repo]["default_branch"] = "other"
    if mutation == "unprotected": data[repo + "/branches/trunk"]["protected"] = False
    if mutation == "missing-head": data[repo + "/branches/trunk"]["commit"] = {}
    if mutation == "untrusted-source": comparison["merge_base_commit"]["sha"] = "c" * 40
    if mutation == "ref-mismatch": data[repo + "/commits/" + SOURCE]["sha"] = "c" * 40
    if mutation == "comparison-base": comparison["base_commit"]["sha"] = "c" * 40
    if mutation == "diverged": comparison["status"] = "diverged"
    def api(args, **kwargs):
        assert args[:2] == ["gh", "api"] and kwargs["stderr"] == subprocess.DEVNULL
        if mutation == "api-404":
            raise subprocess.CalledProcessError(1, args, stderr="404 credential-bearing details")
        return json.dumps(data[args[2]])
    with monkeypatch.context() as patch:
        patch.setenv("RELEASE_SHA", SOURCE)
        patch.setattr(subprocess, "check_output", api)
        if mutation is None:
            exec(compile(code, str(WORKFLOW) + ":release-source", "exec"), {})
        else:
            with pytest.raises(SystemExit) as error:
                exec(compile(code, str(WORKFLOW) + ":release-source", "exec"), {})
            assert "credential-bearing" not in str(error.value)
    assert script.index("<<'TRUSTED_RELEASE'") < script.index('root="https://raw.githubusercontent.com/')
    assert script.index("\nTRUSTED_RELEASE") < script.index("curl --retry")


@pytest.mark.parametrize("mutation", [None, "two-parents", "parent-off-trunk", "no-prepared-ref", "prepared-ref-moved",
                                    "code-change", "rename-from-code", "two-commits", "empty-change"])
def test_release_preparation_on_trunk_is_admitted_only_for_its_data_files(monkeypatch, mutation):
    """honua-release#604 dispatches with its prepared, unadmitted inputs: one data-only commit on trunk."""
    step = next(step for step in workflow()["jobs"]["soak"]["steps"] if step.get("id") == "lock")
    code = step["run"].split("<<'TRUSTED_RELEASE'\n", 1)[1].split("\nTRUSTED_RELEASE", 1)[0]
    repo = "repos/honua-io/honua-release"
    parent = "d" * 40
    diverged = {"status": "diverged", "base_commit": {"sha": SOURCE}, "merge_base_commit": {"sha": parent}}
    files = [{"filename": "platform-manifest.yaml", "status": "modified", "previous_filename": None},
             {"filename": "compatibility-matrix.yaml", "status": "modified", "previous_filename": None},
             {"filename": "deployment-preparation.json", "status": "added", "previous_filename": None}]
    data = {repo: {"full_name": "honua-io/honua-release", "default_branch": "trunk"},
            repo + "/branches/trunk": {"protected": True, "commit": {"sha": TRUNK}},
            repo + "/commits/" + SOURCE: {"sha": SOURCE, "parents": [{"sha": parent}]},
            repo + "/compare/" + SOURCE + "..." + TRUNK: diverged,
            repo + "/compare/" + parent + "..." + TRUNK: {
                "status": "ahead", "base_commit": {"sha": parent}, "merge_base_commit": {"sha": parent}},
            repo + "/git/ref/prepared-deployments/" + SOURCE: {
                "ref": "refs/prepared-deployments/" + SOURCE, "object": {"sha": SOURCE, "type": "commit"}},
            repo + "/compare/" + parent + "..." + SOURCE: {
                "status": "ahead", "ahead_by": 1, "base_commit": {"sha": parent}, "files": files}}
    change = data[repo + "/compare/" + parent + "..." + SOURCE]
    if mutation == "two-parents": data[repo + "/commits/" + SOURCE]["parents"].append({"sha": "e" * 40})
    if mutation == "parent-off-trunk": data[repo + "/compare/" + parent + "..." + TRUNK]["status"] = "diverged"
    if mutation == "no-prepared-ref": del data[repo + "/git/ref/prepared-deployments/" + SOURCE]
    if mutation == "prepared-ref-moved": data[repo + "/git/ref/prepared-deployments/" + SOURCE]["object"]["sha"] = "c" * 40
    if mutation == "code-change": files.append({"filename": "tools/check_capacity_soak.py", "status": "modified"})
    if mutation == "rename-from-code": files[0].update(status="renamed", previous_filename="tools/release_runner_policy.py")
    if mutation == "two-commits": change["ahead_by"] = 2
    if mutation == "empty-change": change["files"] = []
    def api(args, **kwargs):
        assert args[:2] == ["gh", "api"] and kwargs["stderr"] == subprocess.DEVNULL
        if args[2] not in data:
            raise subprocess.CalledProcessError(1, args, stderr="404 credential-bearing details")
        return json.dumps(data[args[2]])
    with monkeypatch.context() as patch:
        patch.setenv("RELEASE_SHA", SOURCE)
        patch.setattr(subprocess, "check_output", api)
        if mutation is None:
            exec(compile(code, str(WORKFLOW) + ":release-source", "exec"), {})
        else:
            with pytest.raises(SystemExit) as error:
                exec(compile(code, str(WORKFLOW) + ":release-source", "exec"), {})
            assert "credential-bearing" not in str(error.value)
