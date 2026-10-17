# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Bind the canonical transport observation to the configured deployment."""
import re
import xml.etree.ElementTree as ET
from ipaddress import ip_address
from urllib.parse import unquote, urlsplit

from owslib.util import http_get


def _url_identity(value):
    assert isinstance(value, str) and value, "service URL must be nonempty"
    assert not any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in value), "invalid URL whitespace"
    assert not set('"<>`{}|^').intersection(value), "invalid URL characters"
    assert "\\" not in value and not re.search(r"%(?![0-9a-fA-F]{2})", value), "invalid URL encoding"
    try:
        parsed = urlsplit(value)
        port = parsed.port
        host = parsed.hostname
    except ValueError as error:
        raise AssertionError("malformed service URL") from error
    assert parsed.scheme in {"http", "https"} and host, "service URL must be absolute HTTP(S)"
    assert parsed.username is None and parsed.password is None, "URL credentials are forbidden"
    assert not parsed.fragment, "URL fragments are forbidden"
    assert not parsed.netloc.endswith(":"), "empty URL port"
    assert port is None or 1 <= port <= 65535, "invalid URL port"
    if ":" in host:
        try:
            ip_address(host)
        except ValueError as error:
            raise AssertionError("invalid URL host") from error
    else:
        assert len(host) <= 253 and all(
            re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label)
            for label in host.split(".")
        ), "invalid URL host"
    path = unquote(parsed.path)
    assert "\\" not in path and not any(c.isspace() or ord(c) < 32 for c in path), "invalid URL path"
    assert not {".", ".."}.intersection(path.split("/")), "URL path traversal"
    return parsed, (parsed.scheme, host, port or (443 if parsed.scheme == "https" else 80)), path


def _capabilities_endpoints(payload, service, version):
    """Require a bounded, well-formed capabilities document for this protocol."""
    assert isinstance(payload, bytes) and 0 < len(payload) <= 4 * 1024 * 1024, "unusable capabilities body"
    assert b"<!DOCTYPE" not in payload.upper() and b"<!ENTITY" not in payload.upper(), "capabilities DTD is forbidden"
    tags = {
        "WFS": ("{http://www.opengis.net/wfs/2.0}WFS_Capabilities", "GetFeature"),
        "WMS": ("{http://www.opengis.net/wms}WMS_Capabilities", "GetMap"),
        "WMTS": ("{http://www.opengis.net/wmts/1.0}Capabilities", "GetTile"),
    }
    try:
        root = ET.fromstring(payload)
    except ET.ParseError as error:
        raise AssertionError("malformed capabilities XML") from error
    expected_tag, read_operation = tags[service]
    assert root.tag == expected_tag and root.get("version") == version, "wrong capabilities protocol/version"
    ns = {"ows": "http://www.opengis.net/ows/1.1", "wms": "http://www.opengis.net/wms"}
    operations = (root.findall("wms:Capability/wms:Request/*", ns) if service == "WMS"
                  else root.findall("ows:OperationsMetadata/ows:Operation", ns))
    named = {}
    for operation in operations:
        name = operation.tag.rsplit("}", 1)[-1] if service == "WMS" else operation.get("name")
        methods = (operation.findall("wms:DCPType/wms:HTTP/*/wms:OnlineResource", ns) if service == "WMS"
                   else operation.findall("ows:DCP/ows:HTTP/*", ns))
        named[name] = [method.get("{http://www.w3.org/1999/xlink}href") for method in methods]
    assert named.get("GetCapabilities") and named.get(read_operation), "capabilities lack required operation endpoints"
    return [url for urls in named.values() for url in urls]


def record_transport(base_url, client, collector, service):
    """Verify configured/advertised origin and a redirect-free OWSLib request.

    HTTP compose remains explicitly non-TLS. HTTPS requires the actual
    OWSLib request to succeed with normal certificate verification enabled.
    """
    versions = {"WFS": "2.0.0", "WMS": "1.3.0", "WMTS": "1.0.0"}
    assert service in versions, "unsupported transport probe protocol"
    configured, expected, prefix = _url_identity(base_url)
    assert not configured.query, "configured base URL must not contain a query"
    prefix = prefix.rstrip("/")

    def check(value):
        _, origin, path = _url_identity(value)
        assert origin == expected, "service URL differs from configured transport/origin"
        assert not prefix or path == prefix or path.startswith(prefix + "/"), "service URL escapes configured mount"

    check(client.url)
    operations = client.operations
    assert operations, "client advertised no operations"
    endpoints = [method.get("url") for operation in operations for method in operation.methods]
    assert endpoints, "client advertised no operation endpoints"
    for endpoint in endpoints:
        check(endpoint)

    version = versions[service]
    assert client.identification.version == version, "unexpected canonical-client protocol version"
    # OWSLib's http_get preserves requests' allow_redirects/verify arguments.
    # Do not follow an advertised downgrade or redirect before recording TLS.
    with http_get(
        client.url,
        params={"SERVICE": service, "REQUEST": "GetCapabilities", "VERSION": version},
        timeout=30, verify=True, allow_redirects=False,
    ) as response:
        assert response.status_code == 200, "transport probe must return HTTP 200 without redirects"
        assert not response.history, "transport probe followed a redirect"
        check(response.url)
        for endpoint in _capabilities_endpoints(response.content, service, version):
            check(endpoint)

    scheme = expected[0]
    collector.record(
        "CERT-CONN-02", "pass" if scheme == "https" else "not-applicable",
        notes=(
            f"OWSLib well-formed capabilities request returned HTTP 200 with redirects disabled; client and "
            f"{len(endpoints)} advertised endpoints match the configured {scheme} origin. "
            + ("HTTPS certificate verification was enabled."
               if scheme == "https" else "Configured HTTP has no TLS handshake; this is not TLS proof.")
        ),
        evidence_ref=client.url,
    )
