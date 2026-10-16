"""Credential-free production artifact transport through the job-scoped broker."""

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
    callback is required; production uses BrokerArtifactUploader.
    """

    def __init__(self, output_prefix: str, *, s3_put=None) -> None:
        parsed = urlparse(output_prefix)
        if parsed.scheme != "s3" or not parsed.netloc:
            raise ValueError(f"output_prefix must be s3://bucket/prefix, got {output_prefix!r}.")
        self._bucket = parsed.netloc
        self._prefix = parsed.path.lstrip("/")
        if s3_put is None:
            raise ValueError("Legacy S3 transport requires an explicit callback; production uses the job artifact broker.")
        self._s3_put = s3_put

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


class BrokerArtifactUploader:
    """Uploads only through the server's job/attempt-bound artifact capability."""

    def __init__(self, base_url: str, token: str, job_id: str, attempt: str) -> None:
        import re
        parsed = urlparse(base_url)
        if parsed.scheme != "https" and not (parsed.scheme == "http" and parsed.hostname in {"localhost", "127.0.0.1", "::1"}):
            raise ValueError("Artifact broker requires HTTPS (HTTP is permitted only for loopback tests).")
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", job_id or "") or not attempt.isdecimal() or int(attempt) <= 0:
            raise ValueError("Artifact broker requires server-assigned HONUA_JOB_ID and HONUA_JOB_ATTEMPT.")
        self._base = base_url.rstrip("/") + "/api/geoprocessing/jobs/" + job_id + "/custom-artifacts/"
        self._token = token
        self._attempt = attempt

    def upload(self, artifacts) -> list[UploadResult]:
        import re
        import time
        from urllib.error import HTTPError, URLError
        from urllib.request import HTTPRedirectHandler, Request, build_opener

        class NoRedirect(HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                return None

        opener = build_opener(NoRedirect())
        results = []
        for artifact in artifacts:
            if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", artifact.name):
                raise ValueError("Artifact name must be a single safe filename.")
            actual_size = artifact.path.stat().st_size
            if actual_size > 64 * 1024 * 1024 or actual_size != artifact.size_bytes:
                raise ValueError("Artifact size exceeds the broker limit or changed after registration.")
            uri = self._base + artifact.name + "?attempt=" + self._attempt
            deadline = time.monotonic() + 120
            while True:
                try:
                    with artifact.path.open("rb") as data:
                        request = Request(uri, data=data, method="PUT", headers={
                            "X-Api-Key": self._token,
                            "Content-Type": "application/octet-stream",
                            "Content-Length": str(actual_size),
                        })
                        with opener.open(request, timeout=30) as response:
                            if response.status not in (200, 201, 204):
                                raise RuntimeError("Artifact broker refused upload.")
                    break
                except HTTPError as exc:
                    if exc.code not in {409, 429, 500, 502, 503, 504} or time.monotonic() >= deadline:
                        raise RuntimeError(f"Artifact broker refused upload (HTTP {exc.code}).") from None
                    time.sleep(2)
                except (URLError, TimeoutError, ConnectionError):
                    if time.monotonic() >= deadline:
                        raise RuntimeError("Artifact broker upload did not complete before its retry deadline.") from None
                    time.sleep(2)
            results.append(UploadResult(artifact.name, uri, actual_size))
        return results
