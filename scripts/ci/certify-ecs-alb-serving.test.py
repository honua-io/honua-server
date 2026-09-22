#!/usr/bin/env python3
"""Exercise the serving certification over HTTP without cloud infrastructure."""

import os
from pathlib import Path
import subprocess
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


SCRIPT = Path(__file__).resolve().parents[1] / "cloud/certify-ecs-alb-serving.sh"


class ServingCertificationTests(unittest.TestCase):
    def certify(self, readiness_status=200, readiness_body="Ready", count=1):
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                status, body = 404, "missing"
                if self.path == "/healthz/live":
                    status, body = 200, "Healthy"
                elif self.path == "/healthz/ready":
                    status, body = readiness_status, readiness_body
                elif self.path == "/rest/info":
                    status, body = 200, '{"currentVersion": 11.5}'
                elif self.path == "/rest/services/test/FeatureServer/0/query?returnCountOnly=true":
                    status, body = 200, '{"count": %d}' % count
                elif self.path == "/api/v1/admin/services":
                    status = 200 if self.headers.get("X-API-Key") == "test-admin-key" else 401
                    body = "{}"
                self.send_response(status)
                self.end_headers()
                self.wfile.write(body.encode())

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            env = os.environ.copy()
            env.pop("GITHUB_STEP_SUMMARY", None)
            env.update(
                HONUA_REALAWS_CERT_ALB_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                HONUA_REALAWS_CERT_ALB_ADMIN_API_KEY="test-admin-key",
                HONUA_REALAWS_CERT_ALB_QUERY_PATH="rest/services/test/FeatureServer/0/query?returnCountOnly=true",
                HONUA_REALAWS_CERT_ALB_EXPECTED_COUNT="1",
            )
            try:
                return subprocess.run(
                    [str(SCRIPT)], env=env, capture_output=True, text=True, timeout=15,
                )
            finally:
                server.shutdown()
                thread.join()

    def test_healthy_target_with_real_admin_route_passes(self):
        result = self.certify()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("authenticated admin request through the ALB succeeded", result.stdout)

    def test_not_ready_body_is_rejected(self):
        result = self.certify(readiness_body="NotReady")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("did not report Ready", result.stderr)

    def test_failed_http_status_with_ready_body_is_rejected(self):
        result = self.certify(readiness_status=503)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("did not report Ready", result.stderr)

    def test_wrong_row_count_is_rejected(self):
        result = self.certify(count=2)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("expected 1", result.stderr)


if __name__ == "__main__":
    unittest.main()
