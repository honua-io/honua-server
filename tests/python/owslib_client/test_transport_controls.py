# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Controlled transport refusals, not deployment certification evidence."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from types import SimpleNamespace
from unittest.mock import patch

import pytest
from requests.exceptions import SSLError

from . import test_wfs, test_wms, test_wmts, transport

PROBES = [test_wfs.test_conn02_transport, test_wms.test_conn02_transport, test_wmts.test_conn02_transport]


@pytest.fixture(scope="session", autouse=True)
def _write_cert_evidence():
    """Keep controlled observations out of the live lane envelope writer."""
    yield


class Observations:
    def __init__(self):
        self.records = []

    def record(self, *args, **kwargs):
        self.records.append((args, kwargs))


def service_for(probe):
    return probe.__module__.rsplit("_", 1)[-1].upper()


def client(url, endpoint=None, service="WFS"):
    version = {"WFS": "2.0.0", "WMS": "1.3.0", "WMTS": "1.0.0"}[service]
    return SimpleNamespace(url=url, identification=SimpleNamespace(version=version), operations=[
        SimpleNamespace(methods=[{"url": endpoint or url}])])


def capabilities(url, service="WFS"):
    if service == "WMS":
        operations = "".join(f'<{name}><DCPType><HTTP><Get><OnlineResource xlink:href="{url}"/></Get></HTTP></DCPType></{name}>'
                             for name in ("GetCapabilities", "GetMap"))
        return (f'<WMS_Capabilities xmlns="http://www.opengis.net/wms" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.3.0">'
                f'<Capability><Request>{operations}</Request></Capability></WMS_Capabilities>').encode()
    namespace, tag, version, read = (("http://www.opengis.net/wfs/2.0", "WFS_Capabilities", "2.0.0", "GetFeature")
                                     if service == "WFS" else ("http://www.opengis.net/wmts/1.0", "Capabilities", "1.0.0", "GetTile"))
    operations = "".join(f'<ows:Operation name="{name}"><ows:DCP><ows:HTTP><ows:Get xlink:href="{url}"/></ows:HTTP></ows:DCP></ows:Operation>'
                         for name in ("GetCapabilities", read))
    return (f'<{tag} xmlns="{namespace}" xmlns:ows="http://www.opengis.net/ows/1.1" xmlns:xlink="http://www.w3.org/1999/xlink" version="{version}">'
            f'<ows:OperationsMetadata>{operations}</ows:OperationsMetadata></{tag}>').encode()


@contextmanager
def response(url, status=200, history=(), service="WFS", payload=None):
    from xml.sax.saxutils import escape
    yield SimpleNamespace(url=url, status_code=status, history=history,
                          content=capabilities(escape(url), service) if payload is None else payload)


@pytest.mark.parametrize("probe", PROBES)
@pytest.mark.parametrize("base,endpoint,status", [
    ("http://honua:5000", "http://honua:5000/wfs", "not-applicable"),
    ("https://candidate.example/mount", "https://candidate.example/mount/wms?service=WMS", "pass"),
    ("https://candidate.example", "https://CANDIDATE.example:443/wmts", "pass"),
    ("http://[::1]:5000/mount", "http://[::1]:5000/mount/wfs", "not-applicable"),
])
def test_configured_scheme_origin_and_mount_bind_the_actual_probe(probe, base, endpoint, status):
    observed = Observations()
    with patch.object(transport, "http_get", return_value=response(endpoint, service=service_for(probe))) as fetch:
        probe(base, client(endpoint, service=service_for(probe)), observed)
    assert observed.records[0][0] == ("CERT-CONN-02", status)
    assert fetch.call_args.kwargs["allow_redirects"] is False
    assert fetch.call_args.kwargs["verify"] is True
    assert fetch.call_args.kwargs["params"]["REQUEST"] == "GetCapabilities"
    assert observed.records[0][1]["evidence_ref"] == endpoint


BAD_ENDPOINTS = [
    "http://candidate.example/mount/wfs",  # HTTPS downgrade
    "https://other.example/mount/wfs", "https://candidate.example.evil/mount/wfs",
    "https://candidate.example:444/mount/wfs", "https://candidate.example/elsewhere/wfs",
    "https://candidate.example/mount-other/wfs", "https://candidate.example/mount/../wfs",
    "https://candidate.example/mount/%2e%2e/wfs", "https://candidate.example/mount/%zz",
    "https://user:fixture@candidate.example/mount/wfs", "https://candidate.example/mount/wfs#fragment",
    "ftp://candidate.example/mount/wfs", "//candidate.example/mount/wfs", "/mount/wfs", "garbage",
    "https:///mount/wfs", "https://candidate.example:/mount/wfs", "https://candidate.example:0/mount/wfs",
    "https://candidate.example:99999/mount/wfs", "https://candidate.example:wrong/mount/wfs",
    "https://candidate..example/mount/wfs", "https://candidate.example\\evil/mount/wfs",
    "https://candidate.example/mount/\nwfs", "https://candidate.example/mount/<invalid>", None,
]


@pytest.mark.parametrize("probe", PROBES)
@pytest.mark.parametrize("bad", BAD_ENDPOINTS)
@pytest.mark.parametrize("location", ["client", "advertised"])
def test_downgrade_unrelated_and_invalid_urls_never_request_or_record(probe, bad, location):
    observed = Observations()
    good = "https://candidate.example/mount/wfs"
    service = client(good, service=service_for(probe))
    if location == "client":
        service.url = bad
    else:
        service.operations[0].methods[0]["url"] = bad
    with patch.object(transport, "http_get") as fetch, pytest.raises(AssertionError):
        probe("https://candidate.example/mount", service, observed)
    fetch.assert_not_called()
    assert observed.records == []


@pytest.mark.parametrize("base", ["relative", "https://candidate.example?query=1", "https://user@candidate.example", "https://candidate.example#fragment", "https://candidate.example:wrong", "https://bad_host.example", "https://[bad]/", " https://candidate.example"])
def test_invalid_configuration_is_not_an_http_fallback(base):
    observed = Observations()
    with patch.object(transport, "http_get") as fetch, pytest.raises(AssertionError):
        transport.record_transport(base, client("https://candidate.example/wfs"), observed, "WFS")
    fetch.assert_not_called()
    assert observed.records == []


@pytest.mark.parametrize("probe", PROBES)
@pytest.mark.parametrize("reply", [
    ("https://candidate.example/wfs", 302, ()),
    ("https://candidate.example/wfs", 200, ("redirect",)),
    ("http://candidate.example/wfs", 200, ()),
    ("https://other.example/wfs", 200, ()),
    ("https://candidate.example/wfs", 403, ()),
])
def test_redirect_or_wrong_final_origin_cannot_emit_pass(probe, reply):
    observed = Observations()
    with patch.object(transport, "http_get", return_value=response(*reply)), pytest.raises(AssertionError):
        probe("https://candidate.example", client("https://candidate.example/wfs", service=service_for(probe)), observed)
    assert observed.records == []


@pytest.mark.parametrize("probe", PROBES)
def test_certificate_validation_failure_cannot_emit_pass(probe):
    observed = Observations()
    with patch.object(transport, "http_get", side_effect=SSLError("controlled invalid certificate")), pytest.raises(SSLError):
        probe("https://candidate.example", client("https://candidate.example/wfs", service=service_for(probe)), observed)
    assert observed.records == []


@pytest.mark.parametrize("probe", PROBES)
@pytest.mark.parametrize("kind", ["not-xml", "wrong-protocol", "wrong-version", "missing-operations", "dtd", "fresh-downgrade"])
def test_non_capabilities_and_malformed_capabilities_never_record_pass(probe, kind):
    observed = Observations()
    url = "https://candidate.example/wfs"
    payload = capabilities(url, service_for(probe))
    if kind == "not-xml":
        payload = b"not XML"
    elif kind == "wrong-protocol":
        payload = b'<FeatureCollection version="2.0.0"/>'
    elif kind == "wrong-version":
        payload = payload.replace(b'version="', b'version="wrong-')
    elif kind == "missing-operations":
        payload = payload.replace(b'GetCapabilities', b'UnrelatedOperation')
    elif kind == "dtd":
        payload = b'<!DOCTYPE unsafe []>' + payload
    else:
        payload = payload.replace(b'https://', b'http://')
    with patch.object(transport, "http_get", return_value=response(url, payload=payload)), pytest.raises(AssertionError):
        probe("https://candidate.example", client(url, service=service_for(probe)), observed)
    assert observed.records == []


def test_real_owslib_transport_does_not_follow_a_redirect():
    hits = []

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            hits.append(self.path)
            self.send_response(302)
            self.send_header("Location", "/redirect-target")
            self.end_headers()

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    base = f"http://127.0.0.1:{server.server_port}"
    observed = Observations()
    try:
        with pytest.raises(AssertionError, match="without redirects"):
            transport.record_transport(base, client(base + "/wfs"), observed, "WFS")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
    assert len(hits) == 1 and hits[0].startswith("/wfs?")
    assert observed.records == []
