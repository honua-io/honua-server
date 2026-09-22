#!/usr/bin/env python3
"""Replay the server#4974 site-root SOAP catalog contract against a running server.

ArcGIS Pro's site-root server connection probes GET /services. The contract is
that the bare form answers 200 with the same service-catalog WSDL the ?wsdl form
serves, and that the SOAP catalog at the same address still negotiates.

Usage: replay-soap-services-site-root.py BASE_URL RECEIPT.json [--label TEXT]
Standard library only.
"""

import argparse
import datetime
import hashlib
import json
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

WSDL = "http://schemas.xmlsoap.org/wsdl/"
GET_MESSAGE_VERSION = (
    '<?xml version="1.0" encoding="utf-8" ?>'
    '<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" '
    'xmlns:tns="http://www.esri.com/schemas/ArcGIS/9.0">'
    "<soap:Body><tns:GetMessageVersion></tns:GetMessageVersion></soap:Body></soap:Envelope>"
)


def send(url, body=None):
    request = urllib.request.Request(url, data=body.encode("utf-8") if body else None,
                                     method="POST" if body else "GET")
    if body:
        request.add_header("Content-Type", "text/xml; charset=utf-8")
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.headers.get("Content-Type"), response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers.get("Content-Type"), error.read()


def describe(status, content_type, payload):
    result = {
        "status": status,
        "contentType": content_type,
        "bodySha256": hashlib.sha256(payload).hexdigest(),
        "bodyBytes": len(payload),
    }
    try:
        root = ET.fromstring(payload)
        result["root"] = root.tag
        result["wsdlOperations"] = sorted({
            element.get("name")
            for element in root.iter(f"{{{WSDL}}}operation")
            if element.get("name")
        })
    except ET.ParseError:
        result["root"] = None
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("base_url")
    parser.add_argument("receipt")
    parser.add_argument("--label", default="")
    args = parser.parse_args()
    base = args.base_url.rstrip("/")

    wsdl = describe(*send(f"{base}/services?wsdl"))
    site_root = describe(*send(f"{base}/services"))
    site_root_slash = describe(*send(f"{base}/services/"))
    status, content_type, payload = send(f"{base}/services", GET_MESSAGE_VERSION)
    negotiation = {"status": status, "contentType": content_type,
                   "hasMessageVersion": b"esriArcGISVersion108" in payload}

    wsdl_root = f"{{{WSDL}}}definitions"
    checks = {
        "wsdlForm200": wsdl["status"] == 200 and wsdl.get("root") == wsdl_root,
        "siteRoot200": site_root["status"] == 200,
        "siteRootServesCatalogWsdl": site_root.get("root") == wsdl_root
            and site_root["bodySha256"] == wsdl["bodySha256"],
        "siteRootTrailingSlashServesCatalogWsdl": site_root_slash["status"] == 200
            and site_root_slash["bodySha256"] == wsdl["bodySha256"],
        "soapCatalogStillNegotiates": status == 200 and negotiation["hasMessageVersion"],
    }
    receipt = {
        "issue": "honua-io/honua-server#4974",
        "label": args.label,
        "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "requests": {
            "GET /services?wsdl": wsdl,
            "GET /services": site_root,
            "GET /services/": site_root_slash,
            "POST /services GetMessageVersion (9.0 namespace)": negotiation,
        },
        "checks": checks,
        "summary": {"total": len(checks), "passed": sum(1 for value in checks.values() if value)},
    }
    with open(args.receipt, "w", encoding="utf-8") as handle:
        json.dump(receipt, handle, indent=2)
        handle.write("\n")
    for name, value in checks.items():
        print(f'{"ok  " if value else "MISS"} {name}')
    print(json.dumps(receipt["summary"]))
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
