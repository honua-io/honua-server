"""Upload staged artifacts to the job's S3 ``output_prefix``.

This runs AFTER user code returns and AFTER the credential scrub, so it uses an
S3 client built from credentials the harness captured at startup (a dedicated
*upload* role/credentials the operator injects under a name the strip step does
NOT remove, or an explicit ``HONUA_OUTPUT_*`` credential). For Phase 1 the
uploader is fully injectable so the orchestrator stays testable offline and the
exact credential wiring can be finalized with the server half.
"""

from __future__ import annotations

from dataclasses import dataclass
from urllib.parse import urlparse

from .context import Artifact


@dataclass(frozen=True)
class UploadResult:
    name: str
    uri: str
    size_bytes: int


class ArtifactUploader:
    """Uploads artifacts to ``s3://bucket/prefix/<name>``.

    ``s3_put`` is an injectable callable ``(bucket, key, path) -> None``. The
    default lazily builds a boto3 S3 client; tests pass a fake.
    """

    def __init__(self, output_prefix: str, *, s3_put=None) -> None:
        parsed = urlparse(output_prefix)
        if parsed.scheme != "s3" or not parsed.netloc:
            raise ValueError(f"output_prefix must be s3://bucket/prefix, got {output_prefix!r}.")
        self._bucket = parsed.netloc
        self._prefix = parsed.path.lstrip("/")
        self._s3_put = s3_put or _default_s3_put

    def _key_for(self, name: str) -> str:
        if self._prefix:
            return f"{self._prefix.rstrip('/')}/{name}"
        return name

    def upload(self, artifacts) -> list[UploadResult]:
        results: list[UploadResult] = []
        for artifact in artifacts:
            assert isinstance(artifact, Artifact)
            key = self._key_for(artifact.name)
            self._s3_put(self._bucket, key, str(artifact.path))
            results.append(
                UploadResult(
                    name=artifact.name,
                    uri=f"s3://{self._bucket}/{key}",
                    size_bytes=artifact.size_bytes,
                )
            )
        return results


def _default_s3_put(bucket: str, key: str, path: str) -> None:  # pragma: no cover - needs boto3 + net
    import boto3

    boto3.client("s3").upload_file(path, bucket, key)
