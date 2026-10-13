import importlib.util
import json
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

DIRECTORY = Path(__file__).parents[1]
sys.path.insert(0, str(DIRECTORY))
from wps20_axis_probe import CRS, LOWER, UPPER, REQUEST, probe, validate_response

RESPONSE = f'''<wps:Result xmlns:wps="http://www.opengis.net/wps/2.0" xmlns:ows="http://www.opengis.net/ows/2.0"><wps:Output><ows:Identifier>boundingBoxOutput</ows:Identifier><wps:Data><ows:BoundingBox crs="{CRS}" dimensions="2"><ows:LowerCorner>{LOWER}</ows:LowerCorner><ows:UpperCorner>{UPPER}</ows:UpperCorner></ows:BoundingBox></wps:Data></wps:Output></wps:Result>'''.encode()


class AxisProbeTests(unittest.TestCase):
    def test_actual_http_request_and_retained_response(self):
        seen = []
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                seen.append((self.path, self.rfile.read(int(self.headers['Content-Length']))))
                self.send_response(200)
                self.send_header('Content-Type', 'application/xml')
                self.end_headers()
                self.wfile.write(RESPONSE)
            def log_message(self, *_):
                pass
        with HTTPServer(('127.0.0.1', 0), Handler) as server, tempfile.TemporaryDirectory() as temp:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                report = probe(f'http://127.0.0.1:{server.server_port}', Path(temp))
            finally:
                server.shutdown()
                thread.join()
            self.assertEqual(report['result'], 'pass')
            self.assertEqual(seen, [('/wps', REQUEST)])
            self.assertEqual((Path(temp) / 'crs84-response.xml').read_bytes(), RESPONSE)
            self.assertEqual(json.loads((Path(temp) / 'crs84-supplement.json').read_text()), report)

    def test_swapped_missing_and_wrong_identity_refuse(self):
        variants = [RESPONSE.replace(LOWER.encode(), b'37.25 -123.5'),
                    RESPONSE.replace(UPPER.encode(), b''),
                    RESPONSE.replace(CRS.encode(), b'urn:wrong'),
                    RESPONSE.replace(b'boundingBoxOutput', b'complexOutput'),
                    RESPONSE.replace(b'dimensions="2"', b''),
                    RESPONSE.replace(b'LowerCorner', b'Corner')]
        for body in variants:
            with self.subTest(body=body), self.assertRaises(ValueError):
                validate_response(200, 'application/xml', body)

    def test_http_status_and_media_are_required(self):
        for status, media in [(400, 'application/xml'), (200, 'text/plain')]:
            with self.subTest(status=status, media=media), self.assertRaises(ValueError):
                validate_response(status, media, RESPONSE)

    def test_external_or_credentialed_origin_refused_before_request(self):
        for origin in ['https://example.com', 'http://user:secret@localhost', 'http://localhost/path']:
            with self.subTest(origin=origin), self.assertRaises(ValueError):
                probe(origin, Path('/unused'))


if __name__ == '__main__':
    unittest.main()
