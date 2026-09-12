#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Attach the attestation's own signature and identity to the receipt.

honua-release's gate verifies authenticity with `gh attestation verify --repo
honua-io/honua-server`, and `tools/check_capacity_soak.py` additionally requires the receipt
to carry a non-empty `signingIdentity` and `signature`. Self-asserted text would satisfy the
letter of that and none of its intent, so those two members are filled in from a real
Sigstore signature over the receipt payload:

  1. the payload (the receipt without its signature members) is attested with
     actions/attest-build-provenance, which signs an in-toto statement whose subject digest is
     the payload's SHA-256, using a Fulcio certificate issued to this workflow's identity;
  2. this script checks that the bundle really covers the payload bytes, then copies the DSSE
     signature into `signature` and the certificate's SAN (the workflow identity) into
     `signingIdentity`;
  3. the workflow attests the finished receipt as well, which is the attestation the consumer
     verifies over the published bytes.

A verifier can therefore re-derive the payload from the published receipt (drop the signature
members, canonicalise) and check both the payload attestation and this signature.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from soak_contract import canonical_json, payload_of  # noqa: E402

SAN_URI = re.compile(r"URI:(\S+)")


def _certificate_der(bundle: dict[str, Any]) -> bytes | None:
    material = bundle.get("verificationMaterial") or {}
    certificate = material.get("certificate") or {}
    raw = certificate.get("rawBytes")
    if not raw:
        chain = (material.get("x509CertificateChain") or {}).get("certificates") or []
        raw = chain[0].get("rawBytes") if chain else None
    return base64.b64decode(raw) if raw else None


def signing_identity_from_certificate(der: bytes) -> str | None:
    """Read the Fulcio certificate's SAN URI: the workflow identity Sigstore bound the key to."""
    pem = (
        b"-----BEGIN CERTIFICATE-----\n"
        + base64.encodebytes(der).replace(b"\n", b"\n").strip()
        + b"\n-----END CERTIFICATE-----\n"
    )
    result = subprocess.run(
        ["openssl", "x509", "-noout", "-ext", "subjectAltName"],
        input=pem,
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        return None
    match = SAN_URI.search(result.stdout.decode("utf-8", "replace"))
    return match.group(1) if match else None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", required=True, type=Path, help="receipt payload that was attested")
    parser.add_argument("--bundle", required=True, type=Path, help="attest-build-provenance bundle for it")
    parser.add_argument("--out", required=True, type=Path, help="signed receipt to publish")
    args = parser.parse_args()

    payload_bytes = args.payload.read_bytes()
    payload_sha256 = hashlib.sha256(payload_bytes).hexdigest()
    bundle = json.loads(args.bundle.read_text(encoding="utf-8"))

    envelope = bundle.get("dsseEnvelope") or {}
    signatures = envelope.get("signatures") or []
    if not signatures or not signatures[0].get("sig"):
        print("attestation bundle carries no DSSE signature", file=sys.stderr)
        return 1
    signature = signatures[0]["sig"]

    statement = json.loads(base64.b64decode(envelope["payload"]).decode("utf-8"))
    subjects = statement.get("subject") or []
    digests = {subject.get("digest", {}).get("sha256") for subject in subjects}
    if payload_sha256 not in digests:
        # The signature must cover THESE bytes. Anything else would be a signature stapled to
        # a document it does not describe.
        print(
            f"attestation subject digests {sorted(d for d in digests if d)} do not include the "
            f"payload digest {payload_sha256}",
            file=sys.stderr,
        )
        return 1

    identity = None
    identity_source = None
    der = _certificate_der(bundle)
    if der:
        identity = signing_identity_from_certificate(der)
        identity_source = "sigstore-certificate-san"
    if not identity:
        builder = ((statement.get("predicate") or {}).get("runDetails") or {}).get("builder") or {}
        identity = builder.get("id")
        identity_source = "in-toto-builder-id"
    if not identity:
        print("could not resolve a signing identity from the attestation bundle", file=sys.stderr)
        return 1

    receipt = json.loads(payload_bytes.decode("utf-8"))
    if canonical_json(payload_of(receipt)) != canonical_json(receipt):
        print("payload file already contains signature members", file=sys.stderr)
        return 1

    receipt["signingIdentity"] = identity
    receipt["signingIdentitySource"] = identity_source
    receipt["signature"] = signature
    receipt["signatureFormat"] = (
        "sigstore DSSE signature over the in-toto provenance statement whose subject is "
        f"sha256:{payload_sha256} — the canonical JSON of this receipt without its signature members"
    )
    args.out.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"signed receipt: {args.out}")
    print(f"  signingIdentity: {identity}  ({identity_source})")
    print(f"  payload sha256:  {payload_sha256}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
