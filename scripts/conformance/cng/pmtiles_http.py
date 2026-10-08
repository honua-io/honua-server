"""Observe canonical PMTiles reads through Honua's real S3-backed serving route."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from urllib.request import Request, urlopen

ARTIFACT_KEY = "pmtiles/cng/honua.pmtiles"
BUCKET = "honua-cng-fixtures"
CONTENT_TYPE = "application/vnd.pmtiles"


def serving_url(base_url: str) -> str:
    return base_url.rstrip("/") + "/api/v1/tiles/pmtiles/" + ARTIFACT_KEY


def load_source(artifact: Path, base_url: str) -> tuple[bytes, dict]:
    content = artifact.read_bytes()
    source = json.loads(artifact.with_name("pmtiles-serving-source.json").read_text())
    expected = {
        "url": serving_url(base_url), "artifact_sha256": hashlib.sha256(content).hexdigest(),
        "artifact_size": len(content), "provider": "AwsS3", "provider_environment": "localstack",
        "object_key": ARTIFACT_KEY, "bucket": BUCKET,
        "publication_api_proven": False,
    }
    if any(source.get(key) != value for key, value in expected.items()):
        raise ValueError("PMTiles serving receipt does not match the artifact and supported server route")
    return content, source


class HttpRangeSource:
    """The Python PMTiles Reader's standard get-bytes callable, with real HTTP accounting."""

    def __init__(self, content: bytes, url: str, opener=urlopen):
        self.content = content
        self.url = url
        self.opener = opener
        self.transfer = dict(requests=0, range_requests=0, full_object_downloads=0,
                             transferred_bytes=0, distinct_objects=0)
        self.responses = []

    def __call__(self, offset: int, length: int) -> bytes:
        if offset < 0 or length <= 0 or offset >= len(self.content):
            raise ValueError("Invalid PMTiles reader byte window")
        requested = f"bytes={offset}-{offset + length - 1}"
        request = Request(self.url, headers={"Range": requested, "Accept-Encoding": "identity"})
        self.transfer["requests"] += 1
        self.transfer["range_requests"] += 1
        self.transfer["distinct_objects"] = 1
        with self.opener(request, timeout=30) as response:
            body = response.read()
            status = response.status
            content_range = response.headers.get("Content-Range", "")
            response_url = response.geturl() if hasattr(response, "geturl") else self.url
        self.transfer["transferred_bytes"] += len(body)
        if status == 200 or len(body) == len(self.content):
            self.transfer["full_object_downloads"] += 1
        self.responses.append(dict(request_range=requested, status=status,
                                   content_range=content_range, bytes=len(body),
                                   sha256=hashlib.sha256(body).hexdigest(), response_url=response_url))
        expected_end = min(offset + length, len(self.content)) - 1
        match = re.fullmatch(r"bytes (\d+)-(\d+)/(\d+)", content_range)
        if (response_url != self.url or status != 206 or not match
                or tuple(map(int, match.groups())) != (offset, expected_end, len(self.content))):
            raise ValueError("Honua PMTiles response did not honor the requested byte range")
        if body != self.content[offset:expected_end + 1]:
            raise ValueError("Honua PMTiles response bytes differ from the candidate-generated archive")
        return body


def record_source(artifact: Path, object_metadata: Path, base_url: str, opener=urlopen) -> dict:
    """Check real provider and route HEAD responses; these setup probes are not client reads."""
    content = artifact.read_bytes()
    digest = hashlib.sha256(content).hexdigest()
    metadata = json.loads(object_metadata.read_text())
    if (metadata.get("ContentLength") != len(content) or metadata.get("ContentType") != CONTENT_TYPE
            or metadata.get("Metadata", {}).get("operation") != "publish"
            or metadata.get("Metadata", {}).get("cng-sha256") != digest):
        raise ValueError("S3 object metadata does not identify the exact generated published PMTiles artifact")
    url = serving_url(base_url)
    with opener(Request(url, method="HEAD"), timeout=30) as response:
        response_url = response.geturl() if hasattr(response, "geturl") else url
        if (response_url != url or response.status != 200 or int(response.headers.get("Content-Length", "-1")) != len(content)
                or response.headers.get("Content-Type", "").split(";")[0] != CONTENT_TYPE
                or response.headers.get("Accept-Ranges") != "bytes"):
            raise ValueError("Honua's supported PMTiles route did not resolve the seeded S3 object")
    return dict(schema="honua-cng-pmtiles-serving/v1", url=url, artifact_sha256=digest,
                artifact_size=len(content), provider="AwsS3", provider_environment="localstack",
                object_key=ARTIFACT_KEY, bucket=BUCKET, publication_api_proven=False,
                scope="candidate serving route with an S3-compatible emulator; not real AWS certification")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact", type=Path, required=True)
    parser.add_argument("--object-metadata", type=Path, required=True)
    parser.add_argument("--base-url", required=True)
    args = parser.parse_args()
    receipt = record_source(args.artifact, args.object_metadata, args.base_url)
    args.artifact.with_name("pmtiles-serving-source.json").write_text(json.dumps(receipt, indent=2) + "\n")
