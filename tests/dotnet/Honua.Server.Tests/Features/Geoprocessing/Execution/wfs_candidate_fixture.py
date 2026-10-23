"""Local TLS WFS fixture for qualify_wfs_candidate.py; run inside its isolated network."""

import argparse
import json
import ssl
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from qualify_wfs_candidate import ROWS


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--certificate", required=True)
    parser.add_argument("--private-key", required=True)
    parser.add_argument("--port", type=int, default=18450)
    args = parser.parse_args()
    observations = {}

    class Fixture(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            path = urllib.parse.urlsplit(self.path)
            parts = path.path.strip("/").split("/")
            record = observations.setdefault(parts[-1], {"starts": [], "errors": []})
            try:
                if parts[0] == "observations":
                    payload = record
                else:
                    q = {k: v[0] for k, v in urllib.parse.parse_qs(path.query).items()}
                    for key, value in {"service": "WFS", "request": "GetFeature", "version": "2.0.0",
                                       "outputFormat": "application/json", "count": "2"}.items():
                        assert q[key] == value, (key, q)
                    start = int(q["startIndex"])
                    record["starts"].append(start)
                    assert len(record["starts"]) <= 4, "paging did not terminate"
                    rows = [r for r in ROWS if r[1] == q.get("typeNames")]
                    if q.get("CQL_FILTER") == "active = true":
                        rows = [r for r in rows if r[2]]
                    if q.get("bbox") == "0,0,10,10":
                        rows = [r for r in rows if 0 <= r[3][0] <= 10 and 0 <= r[3][1] <= 10]
                    payload = {"type": "FeatureCollection", "features": [
                        {"type": "Feature", "id": key, "geometry": {"type": "Point", "coordinates": xyz},
                         "properties": {"key": key, "serial": 9007199254740993 + key, "name": name, "active": active}}
                        for key, _, active, xyz, name in rows[start:start + 1]]}
                    if parts[1] == "matched":
                        payload["numberMatched"] = len(rows)
                data = json.dumps(payload).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            except Exception as error:
                record["errors"].append(str(error))
                self.send_error(500)

    server = ThreadingHTTPServer(("0.0.0.0", args.port), Fixture)
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(args.certificate, args.private_key)
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
