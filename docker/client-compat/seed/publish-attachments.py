#!/usr/bin/env python3
"""Materialize and verify the desktop attachment fixture through server storage.

SQL owns the parents and capability metadata; addAttachment owns both the database
record and file bytes. A rerun verifies existing bytes instead of duplicating them.
This is fixture preparation, never a native-client certification receipt.
"""
from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request
import uuid

SERVICE = "/rest/services/cert_relations/FeatureServer"
FIXTURES = {
    9001: ("inspection-a.txt", b"Honua certification attachment A\n"),
    9002: ("inspection-b.txt", b"Honua certification attachment B\n"),
}


def request(base: str, path: str, *, body: bytes | None = None,
            content_type: str | None = None, api_key: str | None = None) -> bytes:
    headers = {}
    if content_type:
        headers["Content-Type"] = content_type
    if api_key:
        headers["X-API-Key"] = api_key
    req = urllib.request.Request(base + path, data=body, headers=headers)
    with urllib.request.urlopen(req, timeout=60) as response:
        return response.read()


def document(base: str, path: str, **kwargs) -> dict:
    value = json.loads(request(base, path, **kwargs))
    if not isinstance(value, dict) or "error" in value:
        raise ValueError("fixture endpoint returned an error envelope")
    return value


def publish(base: str, api_key: str | None = None) -> None:
    layer = document(base, SERVICE + "/20?f=json")
    if not layer.get("hasAttachments"):
        raise ValueError("fixture layer 20 does not advertise attachments")
    for parent, (name, expected) in FIXTURES.items():
        path = f"{SERVICE}/20/{parent}"
        listing = document(base, path + "/attachments?f=json")
        matches = [a for a in listing["attachmentInfos"] if a["name"] == name]
        if len(matches) > 1:
            raise ValueError("duplicate fixture attachments")
        if matches:
            attachment_id = matches[0]["id"]
        else:
            boundary = "honua-" + uuid.uuid4().hex
            body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"f\"\r\n\r\njson\r\n"
                    f"--{boundary}\r\nContent-Disposition: form-data; name=\"attachment\"; "
                    f"filename=\"{name}\"\r\nContent-Type: text/plain\r\n\r\n").encode()
            body += expected + f"\r\n--{boundary}--\r\n".encode()
            result = document(base, path + "/addAttachment", body=body,
                              content_type=f"multipart/form-data; boundary={boundary}",
                              api_key=api_key)["addAttachmentResult"]
            if result.get("success") is not True:
                raise ValueError("addAttachment did not succeed")
            attachment_id = result["objectId"]
        actual = request(base, f"{path}/attachments/{int(attachment_id)}")
        if actual != expected:
            raise ValueError("stored fixture attachment bytes do not match")

    # Verify the asymmetric relationship (two inspections for A, one for B),
    # including reverse lookup, so a populated but unusable seed fails bootstrap.
    for layer_id, origins, expected in (
        (21, "9101,9102", {9101: {9201, 9202}, 9102: {9203}}),
        (22, "9201,9202,9203", {9201: {9101}, 9202: {9101}, 9203: {9102}}),
    ):
        value = document(base, f"{SERVICE}/{layer_id}/queryRelatedRecords?f=json"
                         f"&objectIds={origins}&relationshipId=1&outFields=objectid&returnGeometry=false")
        actual = {g["objectId"]: {r["attributes"]["objectid"] for r in g["relatedRecords"]}
                  for g in value["relatedRecordGroups"]}
        if actual != expected:
            raise ValueError("related fixture rows do not match their parent keys")


def main() -> int:
    try:
        publish(os.environ.get("HONUA_BASE_URL", "http://honua:5000").rstrip("/"),
                os.environ.get("HONUA_ADMIN_PASSWORD"))
    except (OSError, ValueError, KeyError, TypeError) as error:
        # Do not echo response bodies, request headers, or credentials.
        print(f"Desktop fixture verification failed ({type(error).__name__}).", file=sys.stderr)
        return 1
    print("Desktop fixture verified: two downloadable attachments and bidirectional related records.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
