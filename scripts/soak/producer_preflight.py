#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Check producer output while its job is running; never grant release admission.

The verifier modules must come from the same frozen release commit as the lock.
The workflow first cryptographically verifies the bundle with gh. The pure binder
checks that verified content here; the independent release consumer additionally
admits the completed runner/job using its own metadata and protected policy.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from check_capacity_soak import bind_attestation_content, evaluate, lock_digest


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("lock", "receipt", "artifact-root", "bundle", "attestation"):
        parser.add_argument("--" + name, type=Path, required=True)
    for name in ("expected-revision", "expected-image-digest", "expected-run-id",
                 "expected-run-attempt", "expected-workflow-ref"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args(argv)
    try:
        lock = json.loads(args.lock.read_bytes())
        receipt = json.loads(args.receipt.read_bytes())
        producer = receipt.get("producer", {})
        expected = {"runId": args.expected_run_id, "runAttempt": args.expected_run_attempt,
                    "workflowRef": args.expected_workflow_ref}
        failures = ["receipt producer differs from this running invocation"] if any(
            str(producer.get(key)) != value for key, value in expected.items()) else []
        failures.extend(evaluate(lock, receipt, lock_digest(args.lock), args.expected_revision,
                                 args.artifact_root, args.expected_image_digest))
        failures.extend(bind_attestation_content(json.loads(args.attestation.read_bytes()),
                        hashlib.sha256(args.bundle.read_bytes()).hexdigest(), receipt, args.expected_revision))
    except (OSError, ValueError, TypeError, KeyError, AttributeError) as error:
        failures = [str(error)]
    if failures:
        print("producer preflight: FAIL")
        for failure in failures:
            print("- " + failure)
        return 1
    print("producer preflight pass; completed-run release admission pending")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
