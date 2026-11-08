import http.server
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import unittest


class HealthProbeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory()
        cls.binary = str(Path(cls.directory.name) / 'probe')
        source = Path(__file__).resolve().parents[2] / 'docker/runtime/health-probe.c'
        subprocess.run(['cc', '-Wall', '-Wextra', '-Werror', '-o', cls.binary, str(source)], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.directory.cleanup()

    def probe_status(self, status):
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(status if self.path == '/healthz/ready' else 404)
                self.end_headers()

            def log_message(self, *args):
                pass

        with http.server.HTTPServer(('127.0.0.1', 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            try:
                return subprocess.run([self.binary, '--ready'], env={**os.environ, 'HONUA_HEALTH_PORT': str(server.server_port)},
                                      capture_output=True, timeout=8).returncode
            finally:
                server.shutdown()
                thread.join()

    def test_ready_succeeds(self):
        self.assertEqual(self.probe_status(200), 0)

    def test_degraded_fails(self):
        self.assertNotEqual(self.probe_status(503), 0)

    def test_refused_connection_fails(self):
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        self.assertNotEqual(subprocess.run([self.binary], env={**os.environ, 'HONUA_HEALTH_PORT': str(port)},
                                          capture_output=True, timeout=8).returncode, 0)

    def test_malformed_status_fails(self):
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            listener.listen()
            def reply():
                with listener.accept()[0] as connection:
                    connection.recv(1024)
                    connection.sendall(b'HTTP/1.1 200invalid\r\n\r\n')
            thread = threading.Thread(target=reply)
            thread.start()
            try:
                result = subprocess.run([self.binary],
                                        env={**os.environ, 'HONUA_HEALTH_PORT': str(listener.getsockname()[1])},
                                        capture_output=True, timeout=8)
                self.assertNotEqual(result.returncode, 0)
            finally:
                thread.join()

    def test_invalid_port_fails(self):
        self.assertNotEqual(subprocess.run([self.binary], env={**os.environ, 'HONUA_HEALTH_PORT': '65536'},
                                          capture_output=True, timeout=8).returncode, 0)


if __name__ == '__main__':
    unittest.main()
