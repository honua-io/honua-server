#!/usr/bin/env python3
"""Supplemental HTTP CRS84 echo proof; not an upstream ETS test or durable GP proof."""
from __future__ import annotations

import argparse
import hashlib
import json
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit

WPS = 'http://www.opengis.net/wps/2.0'
OWS = 'http://www.opengis.net/ows/2.0'
CRS = 'urn:ogc:def:crs:OGC:1.3:CRS84'
LOWER = '-123.5 37.25'
UPPER = '-121.75 39.5'
MAX_BYTES = 65536
FIXTURE = Path(__file__).resolve().parents[3] / 'docker/cite/wps20/crs84-echo.xml'
REQUEST = FIXTURE.read_bytes()


def validate_response(status: int, media_type: str, body: bytes) -> None:
    if status != 200 or media_type not in {'application/xml', 'text/xml'}:
        raise ValueError('CRS84 HTTP response requires status 200 and XML media type')
    if not body or len(body) > MAX_BYTES or b'<!DOCTYPE' in body.upper() or b'<!ENTITY' in body.upper():
        raise ValueError('CRS84 HTTP response is empty, oversized, or contains declarations')
    root = ET.fromstring(body)
    if root.tag != f'{{{WPS}}}Result':
        raise ValueError('CRS84 response must be a WPS Result')
    outputs = root.findall(f'{{{WPS}}}Output')
    if len(outputs) != 1:
        raise ValueError('CRS84 response requires exactly one output')
    output = outputs[0]
    if output.findtext(f'{{{OWS}}}Identifier') != 'boundingBoxOutput':
        raise ValueError('CRS84 response has the wrong output identifier')
    data = output.find(f'{{{WPS}}}Data')
    if data is None or len(data) != 1:
        raise ValueError('CRS84 response requires exactly one data element')
    box = data[0]
    if box.tag != f'{{{OWS}}}BoundingBox' or box.get('crs') != CRS or box.get('dimensions') != '2':
        raise ValueError('CRS84 response has missing or incorrect CRS/dimension identity')
    if [element.tag for element in box] != [f'{{{OWS}}}LowerCorner', f'{{{OWS}}}UpperCorner']:
        raise ValueError('CRS84 response requires ordered lower and upper corners')
    for corner, expected in zip(box, [LOWER, UPPER]):
        if len(corner) or (corner.text or '').split() != expected.split():
            raise ValueError('CRS84 response changed longitude/latitude order or coordinate values')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError('CRS84 probe refuses redirects')


def probe(base_url: str, output: Path) -> dict:
    parsed = urlsplit(base_url)
    if parsed.scheme != 'http' or parsed.hostname not in {'localhost', '127.0.0.1'} or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in {'', '/'}:
        raise ValueError('CRS84 probe requires the owned loopback HTTP server origin')
    output.mkdir(parents=True, exist_ok=True)
    started = datetime.now(timezone.utc).isoformat()
    clock = time.monotonic()
    body = b''
    status = None
    media = None
    error = None
    request = urllib.request.Request(base_url.rstrip('/') + '/wps', REQUEST, {'Content-Type': 'application/xml'}, method='POST')
    try:
        try:
            response = urllib.request.build_opener(NoRedirect()).open(request, timeout=20)
        except urllib.error.HTTPError as failure:
            response = failure
        with response:
            status = response.status
            media = response.headers.get_content_type()
            body = response.read(MAX_BYTES + 1)
        if time.monotonic() - clock > 20:
            raise ValueError('CRS84 response exceeded the 20-second deadline')
        validate_response(status, media, body)
    except (OSError, ValueError, ET.ParseError) as failure:
        error = type(failure).__name__ + ': ' + str(failure)
    receipt = {
        'schema': 'honua.wps-crs84-supplement/v1',
        'scope': 'Test-only conformance echo HTTP adapter; excludes upstream ETS and durable GP',
        'result': 'fail' if error else 'pass',
        'started_at': started, 'completed_at': datetime.now(timezone.utc).isoformat(),
        'elapsed_seconds': time.monotonic() - clock,
        'request': {'method': 'POST', 'path': '/wps', 'sha256': hashlib.sha256(REQUEST).hexdigest()},
        'response': {'status': status, 'media_type': media, 'sha256': hashlib.sha256(body).hexdigest(), 'bytes': len(body)},
        'error': error,
    }
    (output / 'crs84-request.xml').write_bytes(REQUEST)
    (output / 'crs84-response.xml').write_bytes(body)
    (output / 'crs84-supplement.json').write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    raise SystemExit(0 if probe(args.base_url, args.output)['result'] == 'pass' else 1)
