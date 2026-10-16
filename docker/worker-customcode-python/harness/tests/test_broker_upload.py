"""Real loopback transport checks; no injected upload callback or cloud credentials."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread

import pytest

from honua_customcode_harness.context import Artifact
from honua_customcode_harness.harness import EXIT_HARNESS_ERROR, main
from honua_customcode_harness.upload import BrokerArtifactUploader


def test_broker_upload_sends_exact_bytes_and_job_capability(tmp_path):
    captured = []

    class Handler(BaseHTTPRequestHandler):
        def do_PUT(self):
            captured.append((self.path, self.headers["X-Api-Key"], self.rfile.read(int(self.headers["Content-Length"]))))
            self.send_response(201)
            self.end_headers()
        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        path = tmp_path / "result.json"
        path.write_bytes(b'{"count":3}')
        uploader = BrokerArtifactUploader(f"http://127.0.0.1:{server.server_port}", "job-capability", "job-1", "2")
        result = uploader.upload([Artifact("result.json", path, path.stat().st_size)])
        assert captured == [("/api/geoprocessing/jobs/job-1/custom-artifacts/result.json?attempt=2", "job-capability", b'{"count":3}')]
        assert result[0].size_bytes == len(b'{"count":3}')
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def test_remote_launch_refuses_ambient_cloud_credentials(monkeypatch):
    monkeypatch.setenv("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "/fake-public-sentinel")
    assert main(["--credential-free-child"]) == EXIT_HARNESS_ERROR


@pytest.mark.parametrize("base", ["http://remote.invalid", "file:///tmp/test"])
def test_broker_refuses_insecure_remote_origin(base):
    with pytest.raises(ValueError):
        BrokerArtifactUploader(base, "job-capability", "job-1", "1")
