from __future__ import annotations

import pytest

from honua_customcode_harness.context import Artifact
from honua_customcode_harness.upload import ArtifactUploader


def _artifact(tmp_path, name="out.txt", body="x"):
    p = tmp_path / name
    p.write_text(body, encoding="utf-8")
    return Artifact(name=name, path=p, size_bytes=len(body))


def test_uploader_builds_keys_under_prefix(tmp_path) -> None:
    puts = []
    up = ArtifactUploader(
        "s3://my-bucket/jobs/42", s3_put=lambda b, k, p: puts.append((b, k, p))
    )
    results = up.upload([_artifact(tmp_path)])
    assert puts[0][0] == "my-bucket"
    assert puts[0][1] == "jobs/42/out.txt"
    assert results[0].uri == "s3://my-bucket/jobs/42/out.txt"


def test_uploader_handles_bucket_root_prefix(tmp_path) -> None:
    puts = []
    up = ArtifactUploader("s3://my-bucket", s3_put=lambda b, k, p: puts.append((b, k, p)))
    up.upload([_artifact(tmp_path)])
    assert puts[0][1] == "out.txt"


def test_uploader_rejects_non_s3_prefix() -> None:
    with pytest.raises(ValueError, match="s3://"):
        ArtifactUploader("/local/path")
