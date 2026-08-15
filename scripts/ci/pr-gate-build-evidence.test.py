#!/usr/bin/env python3

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("pr-gate-build-evidence.py")
SPEC = importlib.util.spec_from_file_location("pr_gate_build_evidence", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def run(repo: Path, *args: str, input_text: str | None = None) -> str:
    completed = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        input=input_text,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env={**os.environ, "GIT_AUTHOR_NAME": "CI", "GIT_AUTHOR_EMAIL": "ci@example.test",
             "GIT_COMMITTER_NAME": "CI", "GIT_COMMITTER_EMAIL": "ci@example.test"},
    )
    return completed.stdout.strip()


class BuildEvidenceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        run(self.repo, "init", "-q")

        for path in MODULE.POLICY_PATHS:
            target = self.repo / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(f"trusted:{path}\n", encoding="utf-8")
        registry = {
            "contract_version": 1,
            "projects": [{
                "artifact_suffix": "server",
                "csproj": "tests/dotnet/Server.Tests/Server.Tests.csproj",
                "proof_filter": "FullyQualifiedName~Proof",
            }],
        }
        registry_path = self.repo / ".github/server-test-artifact-projects.json"
        registry_path.write_text(json.dumps(registry), encoding="utf-8")
        (self.repo / "base.txt").write_text("base\n", encoding="utf-8")
        run(self.repo, "add", ".")
        run(self.repo, "commit", "-qm", "base")
        self.base = run(self.repo, "rev-parse", "HEAD")
        run(self.repo, "checkout", "-qb", "candidate")
        (self.repo / "candidate.txt").write_text("candidate\n", encoding="utf-8")
        run(self.repo, "add", "candidate.txt")
        run(self.repo, "commit", "-qm", "candidate")
        self.head = run(self.repo, "rev-parse", "HEAD")
        run(self.repo, "checkout", "-qb", "merge", self.base)
        run(self.repo, "merge", "--no-ff", "-qm", "merge", self.head)
        self.merge = run(self.repo, "rev-parse", "HEAD")
        self.tree = run(self.repo, "rev-parse", "HEAD^{tree}")

        self.metadata = self.root / "metadata"
        (self.metadata / "manifests").mkdir(parents=True)
        self.payload = self.root / "payload"
        self.payload.mkdir()
        self.project = "tests/dotnet/Server.Tests/Server.Tests.csproj"
        self.plan = {
            "contract": "honua.server-test-prebuild-plan/v1",
            "consumers": [{"identity": "Core", "project": self.project, "filter": "x"}],
            "deferred_repeated_projects": [],
            "descriptor_reason": "targeted",
            "producers": [{
                "identity": "server",
                "project": self.project,
                "project_suffix": "server",
                "selected_shard_count": 2,
            }],
            "selected_shard_count": 2,
        }
        self.plan_path = self.metadata / "plan.json"
        self.plan_path.write_text(json.dumps(self.plan), encoding="utf-8")
        self.expected_plan = self.root / "expected-plan.json"
        self.expected_plan.write_text(json.dumps(self.plan), encoding="utf-8")
        (self.metadata / "descriptor.json").write_text(
            json.dumps({"run_all": False, "shards": ["a", "b"], "reason": "targeted"}),
            encoding="utf-8",
        )

        self.archive = self.payload / "server-test-binaries-server.tar.gz"
        self.archive.write_bytes(b"archive bytes")
        self.manifest = {
            "contract": MODULE.MANIFEST_SCHEMA,
            "source_sha": self.merge,
            "dotnet_sdk": "10.0.100",
            "project": self.project,
            "artifact_suffix": "server",
            "archive_file": self.archive.name,
            "archive_sha256": MODULE.sha256_file(self.archive),
            "raw_bytes": 20,
            "unpacked_bytes": 15,
            "archive_bytes": self.archive.stat().st_size,
            "file_count": 1,
            "package_milliseconds": 10,
            "created_at_epoch": 1_000,
            "expires_at_epoch": 2_000,
            "retained_runtime_ids": ["linux", "linux-x64", "unix"],
        }
        self.manifest_path = self.payload / "server-test-binaries-server.manifest.json"
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        (self.metadata / "manifests" / self.manifest_path.name).write_bytes(
            self.manifest_path.read_bytes()
        )

        self.context = self.metadata / "context.json"
        self.context.write_text(json.dumps({
            "contract": MODULE.CONTEXT_SCHEMA,
            "repository": "honua-io/honua-server",
            "pull_request": 42,
            "base_sha": self.base,
            "head_sha": self.head,
            "merge_sha": self.merge,
            "merge_tree_sha": self.tree,
            "run_id": 100,
            "run_attempt": 2,
            "workflow_path": MODULE.PRODUCER_WORKFLOW,
            "configuration": "Release",
            "dotnet_sdk": "10.0.100",
            "runner_os": "Linux",
            "runner_arch": "X64",
            "runner_image": "ubuntu24-20260801.1",
        }), encoding="utf-8")
        self.source_artifact = self.root / "source-artifact.json"
        self.source_artifact.write_text(json.dumps({
            "artifact_id": 500,
            "artifact_name": "pr-gate-server-test-binaries-100-attempt-2",
            "artifact_bytes": 1234,
            "artifact_digest": f"sha256:{'a' * 64}",
        }), encoding="utf-8")
        self.receipt = self.root / "receipt.json"

    def build_args(self) -> argparse.Namespace:
        return argparse.Namespace(
            repository="honua-io/honua-server", pull_request=42, head_sha=self.head,
            base_sha=self.base, source_run_id=100, source_run_attempt=2,
            configuration="Release", dotnet_sdk="10.0.100", runner_os="Linux",
            runner_arch="X64", runner_image="ubuntu24-20260801.1", now_epoch=1_500,
            policy_root=self.repo, policy_sha=self.base, source_root=self.repo,
            context=self.context, plan=self.plan_path, expected_plan=self.expected_plan,
            source_artifact=self.source_artifact, metadata_dir=self.metadata,
            registry=self.repo / ".github/server-test-artifact-projects.json",
            observer_run_id=200, observer_run_attempt=1,
            registry_by_project=MODULE.load_registry(
                self.repo / ".github/server-test-artifact-projects.json"
            ),
        )

    def consumer_args(self, consumer_sha: str) -> argparse.Namespace:
        return argparse.Namespace(
            receipt=self.receipt, repository="honua-io/honua-server", pull_request=42,
            head_sha=self.head, source_run_id=100, source_run_attempt=2,
            source_artifact_id=500, configuration="Release", dotnet_sdk="10.0.100",
            runner_os="Linux", runner_arch="X64", runner_image="ubuntu24-20260801.1",
            now_epoch=1_500, consumer_root=self.repo, consumer_sha=consumer_sha,
            payload_dir=self.payload, project=self.project,
        )

    def write_receipt(self) -> dict:
        receipt = MODULE.build_receipt(self.build_args())
        self.receipt.write_bytes(MODULE.canonical(receipt) + b"\n")
        return receipt

    def test_policy_roster_covers_candidate_packaging_and_proof_parser(self) -> None:
        self.assertIn("scripts/ci/package-pr-gate-build-evidence.sh", MODULE.POLICY_PATHS)
        self.assertIn("scripts/ci/summarize-dotnet-trx.py", MODULE.POLICY_PATHS)

    def test_accepts_different_commit_with_identical_tree(self) -> None:
        receipt = self.write_receipt()
        consumer = run(
            self.repo,
            "commit-tree", self.tree, "-p", self.merge,
            input_text="tree-equivalent consumer\n",
        )
        accepted = MODULE.validate_for_consumer(self.consumer_args(consumer))
        self.assertEqual(accepted["fingerprint"], receipt["fingerprint"])

    def test_rejects_different_consumer_tree(self) -> None:
        self.write_receipt()
        run(self.repo, "checkout", "-qb", "different", self.merge)
        (self.repo / "different.txt").write_text("different\n", encoding="utf-8")
        run(self.repo, "add", "different.txt")
        run(self.repo, "commit", "-qm", "different")
        different = run(self.repo, "rev-parse", "HEAD")
        with self.assertRaisesRegex(ValueError, "consumer tree differs"):
            MODULE.validate_for_consumer(self.consumer_args(different))

    def test_rejects_modified_producer_policy(self) -> None:
        run(self.repo, "checkout", "-qb", "policy-change", self.merge)
        target = self.repo / MODULE.POLICY_PATHS[0]
        target.write_text("changed\n", encoding="utf-8")
        run(self.repo, "add", str(target.relative_to(self.repo)))
        run(self.repo, "commit", "-qm", "policy change")
        changed = run(self.repo, "rev-parse", "HEAD")
        context = json.loads(self.context.read_text(encoding="utf-8"))
        context["merge_sha"] = changed
        context["merge_tree_sha"] = run(self.repo, "rev-parse", "HEAD^{tree}")
        self.context.write_text(json.dumps(context), encoding="utf-8")
        self.manifest["source_sha"] = changed
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        (self.metadata / "manifests" / self.manifest_path.name).write_bytes(
            self.manifest_path.read_bytes()
        )
        with self.assertRaisesRegex(ValueError, "producer policy differs"):
            MODULE.build_receipt(self.build_args())

    def test_rejects_plan_drift(self) -> None:
        expected = dict(self.plan)
        expected["descriptor_reason"] = "different"
        self.expected_plan.write_text(json.dumps(expected), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "plans differ"):
            MODULE.build_receipt(self.build_args())

    def test_rejects_tampered_archive(self) -> None:
        self.write_receipt()
        self.archive.write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "downloaded archive"):
            MODULE.validate_for_consumer(self.consumer_args(self.merge))

    def test_rejects_tampered_manifest(self) -> None:
        self.write_receipt()
        self.manifest_path.write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "manifest digest"):
            MODULE.validate_for_consumer(self.consumer_args(self.merge))

    def test_rejects_wrong_payload_artifact_id(self) -> None:
        self.write_receipt()
        args = self.consumer_args(self.merge)
        args.source_artifact_id = 501
        with self.assertRaisesRegex(ValueError, "requested consumer identity"):
            MODULE.validate_for_consumer(args)

    def test_rejects_expired_receipt(self) -> None:
        self.write_receipt()
        args = self.consumer_args(self.merge)
        args.now_epoch = 2_001
        with self.assertRaisesRegex(ValueError, "validity window"):
            MODULE.validate_for_consumer(args)

    def test_rejects_future_receipt(self) -> None:
        self.write_receipt()
        args = self.consumer_args(self.merge)
        args.now_epoch = 999
        with self.assertRaisesRegex(ValueError, "validity window"):
            MODULE.validate_for_consumer(args)

    def test_rejects_duplicate_receipt_key(self) -> None:
        self.receipt.write_text('{"schema":"a","schema":"b"}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "duplicate key"):
            MODULE.validate_for_consumer(self.consumer_args(self.merge))

    def test_rejects_unknown_receipt_field(self) -> None:
        receipt = self.write_receipt()
        receipt["unexpected"] = True
        receipt["fingerprint"] = MODULE.fingerprint(receipt)
        self.receipt.write_bytes(MODULE.canonical(receipt) + b"\n")
        with self.assertRaisesRegex(ValueError, "keys are invalid"):
            MODULE.validate_for_consumer(self.consumer_args(self.merge))

    def test_rejects_unsafe_project_evidence_filename(self) -> None:
        receipt = self.write_receipt()
        receipt["projects"][0]["manifest_file"] = "../receipt.json"
        receipt["fingerprint"] = MODULE.fingerprint(receipt)
        self.receipt.write_bytes(MODULE.canonical(receipt) + b"\n")
        with self.assertRaisesRegex(ValueError, "project evidence identity"):
            MODULE.validate_for_consumer(self.consumer_args(self.merge))

    def test_rejects_oversized_manifest_expansion(self) -> None:
        self.manifest["unpacked_bytes"] = MODULE.MAX_UNPACKED_BYTES + 1
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        (self.metadata / "manifests" / self.manifest_path.name).write_bytes(
            self.manifest_path.read_bytes()
        )
        with self.assertRaisesRegex(ValueError, "unpacked bytes exceeds"):
            MODULE.build_receipt(self.build_args())

    def test_rejects_project_not_repeated_across_shards(self) -> None:
        self.plan["producers"][0]["selected_shard_count"] = 1
        self.plan_path.write_text(json.dumps(self.plan), encoding="utf-8")
        self.expected_plan.write_text(json.dumps(self.plan), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "not repeated"):
            MODULE.build_receipt(self.build_args())

    def test_rejects_invalid_observer_identity(self) -> None:
        receipt = self.write_receipt()
        receipt["observer"]["run_id"] = 0
        receipt["fingerprint"] = MODULE.fingerprint(receipt)
        self.receipt.write_bytes(MODULE.canonical(receipt) + b"\n")
        with self.assertRaisesRegex(ValueError, "observer run id"):
            MODULE.validate_for_consumer(self.consumer_args(self.merge))

    def test_rejects_wrong_observer_workflow(self) -> None:
        receipt = self.write_receipt()
        receipt["observer"]["workflow_path"] = ".github/workflows/ci.yml"
        receipt["fingerprint"] = MODULE.fingerprint(receipt)
        self.receipt.write_bytes(MODULE.canonical(receipt) + b"\n")
        with self.assertRaisesRegex(ValueError, "requested consumer identity"):
            MODULE.validate_for_consumer(self.consumer_args(self.merge))

    def test_rejects_invalid_source_artifact_digest(self) -> None:
        artifact = json.loads(self.source_artifact.read_text(encoding="utf-8"))
        artifact["artifact_digest"] = "sha256:not-a-digest"
        self.source_artifact.write_text(json.dumps(artifact), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "artifact identity"):
            MODULE.build_receipt(self.build_args())


if __name__ == "__main__":
    unittest.main()
