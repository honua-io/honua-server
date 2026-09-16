#!/usr/bin/env python3
"""Replay the server#4973 SOAP argument-binding contract against a running server.

Posts ArcGIS Pro 3.7.1's captured GetServiceDescriptionsEx envelope verbatim, the
issue's isolation table (qualified, unqualified, default-namespace, casing), and
the same argument forms for the GPServer GetToolInfo and SubmitJob operations.
Every case states the post-fix contract; the receipt records what the target
actually answered, so a pre-fix image reproduces the defect as contract misses.

Usage: replay-soap-argument-binding.py BASE_URL GP_SERVICE RECEIPT.json [--label TEXT]
An admin API key for SubmitJob is read from HONUA_REPLAY_API_KEY and is never
written to the receipt. Standard library only.
"""

import argparse
import datetime
import json
import os
import re
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

SOAP11 = "http://schemas.xmlsoap.org/soap/envelope/"
ARCGIS = "http://www.esri.com/schemas/ArcGIS/10.8"
LEGACY = "http://www.esri.com/schemas/ArcGIS/9.0"

# Captured verbatim from ArcGIS Pro 3.7.1 (server#4973).
PRO_371_GET_SERVICE_DESCRIPTIONS_EX = """<?xml version="1.0" encoding="utf-8" ?>
<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
               xmlns:xsd="http://www.w3.org/2001/XMLSchema"
               xmlns:tns="http://www.esri.com/schemas/ArcGIS/10.8">
  <soap:Body>
    <tns:GetServiceDescriptionsEx>
      <FolderName></FolderName>
    </tns:GetServiceDescriptionsEx>
  </soap:Body>
</soap:Envelope>
"""

AREA_TOOL = "Honua_67656F6D657472792E61726561"  # geometry.area
AREA_ARGUMENTS = (
    f"<ToolName>{AREA_TOOL}</ToolName>"
    '<Values xsi:type="tns:GPValues">'
    '<GPValue xsi:type="tns:GPString"><Value>AQEAAAAAAAAAAAAAAAAAAAAAAAAA</Value></GPValue>'
    '<GPValue xsi:type="tns:GPLong"><Value>3857</Value></GPValue>'
    "</Values>"
)


def envelope(operation_xml):
    return (
        '<?xml version="1.0" encoding="utf-8" ?>'
        f'<soap:Envelope xmlns:soap="{SOAP11}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        f'xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:tns="{ARCGIS}">'
        f"<soap:Body>{operation_xml}</soap:Body></soap:Envelope>"
    )


def form(operation, arguments, shape):
    if shape == "unqualified":
        return f"<tns:{operation}>{arguments}</tns:{operation}>"
    if shape == "qualified":
        qualified = re.sub(r"<(/?)([A-Za-z])", r"<\1tns:\2", arguments)
        return f"<tns:{operation}>{qualified}</tns:{operation}>"
    if shape == "default-namespace":
        return f'<{operation} xmlns="{ARCGIS}">{arguments}</{operation}>'
    raise ValueError(shape)


def post(url, body, api_key=None):
    request = urllib.request.Request(url, data=body.encode("utf-8"), method="POST")
    request.add_header("Content-Type", "text/xml; charset=utf-8")
    request.add_header("SOAPAction", '""')
    if api_key:
        request.add_header("X-API-Key", api_key)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8")


def local(element):
    return element.tag.rsplit("}", 1)[-1]


def summarize(text):
    try:
        root = ET.fromstring(text.encode("utf-8"))
    except ET.ParseError:
        return {"parseable": False}
    elements = list(root.iter())
    fault = next((e.text or "" for e in elements if local(e) in ("faultstring", "Text")), None)
    response = next((local(e) for e in elements if local(e).endswith("Response")), None)
    descriptions = [e for e in elements if local(e) == "ServiceDescription"]
    names = [
        (child.text or "")
        for description in descriptions
        for child in description
        if local(child) == "Name"
    ]
    result = next((e for e in elements if local(e) == "Result"), None)
    tool_name = None
    if result is not None:
        tool_name = next((c.text for c in result if local(c) == "Name"), None)
    return {
        "parseable": True,
        "response": response,
        "faultstring": fault,
        "serviceDescriptionNames": names if response and response.startswith("GetServiceDescriptions") else None,
        "resultToolName": tool_name,
        "resultHasText": bool(result is not None and (result.text or "").strip()),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("base_url")
    parser.add_argument("gp_service")
    parser.add_argument("receipt")
    parser.add_argument("--label", default="")
    args = parser.parse_args()
    base = args.base_url.rstrip("/")
    api_key = os.environ.get("HONUA_REPLAY_API_KEY")
    catalog = f"{base}/services"
    gp = f"{base}/services/{args.gp_service}/GPServer"

    status, text = post(catalog, envelope("<tns:GetServiceDescriptions />"))
    baseline = summarize(text)
    baseline_names = baseline.get("serviceDescriptionNames") or []

    cases = []

    def check(name, url, body, expected_status, expect, key=None):
        observed_status, observed_text = post(url, body, key)
        observed = summarize(observed_text)
        ok = observed_status == expected_status and expect(observed)
        cases.append({
            "case": name,
            "endpoint": url[len(base):],
            "request": body,
            "expectedStatus": expected_status,
            "observedStatus": observed_status,
            "observed": observed,
            "matchesContract": ok,
        })

    def descriptions(observed):
        return observed.get("serviceDescriptionNames") == baseline_names and observed.get("faultstring") is None

    def fault(expected):
        return lambda observed: observed.get("faultstring") == expected

    recurse_fault = "GetServiceDescriptionsEx does not accept the 'Recurse' argument; its only argument is FolderName."
    check("catalog/pro-3.7.1-captured-envelope", catalog, PRO_371_GET_SERVICE_DESCRIPTIONS_EX, 200, descriptions)
    for name, operation, expected_status, expect in [
        ("tns-operation+unqualified-FolderName", "<tns:GetServiceDescriptionsEx><FolderName></FolderName></tns:GetServiceDescriptionsEx>", 200, descriptions),
        ("tns-operation+qualified-FolderName", "<tns:GetServiceDescriptionsEx><tns:FolderName></tns:FolderName></tns:GetServiceDescriptionsEx>", 200, descriptions),
        ("default-namespace+inherited-FolderName", f'<GetServiceDescriptionsEx xmlns="{ARCGIS}"><FolderName></FolderName></GetServiceDescriptionsEx>', 200, descriptions),
        ("default-namespace+inherited-folderName", f'<GetServiceDescriptionsEx xmlns="{ARCGIS}"><folderName></folderName></GetServiceDescriptionsEx>', 200, descriptions),
        ("tns-operation+unqualified-folderName", "<tns:GetServiceDescriptionsEx><folderName /></tns:GetServiceDescriptionsEx>", 200, descriptions),
        ("legacy-9.0-operation+unqualified-FolderName", f'<legacy:GetServiceDescriptionsEx xmlns:legacy="{LEGACY}"><FolderName /></legacy:GetServiceDescriptionsEx>', 200, descriptions),
        ("default-namespace+folderName+Recurse", f'<GetServiceDescriptionsEx xmlns="{ARCGIS}"><folderName /><Recurse>true</Recurse></GetServiceDescriptionsEx>', 400, fault(recurse_fault)),
        ("tns-operation+unqualified-Recurse", "<tns:GetServiceDescriptionsEx><Recurse>true</Recurse></tns:GetServiceDescriptionsEx>", 400, fault(recurse_fault)),
        ("tns-operation+two-FolderName", "<tns:GetServiceDescriptionsEx><FolderName /><tns:FolderName /></tns:GetServiceDescriptionsEx>", 400, fault("GetServiceDescriptionsEx accepts at most one FolderName argument.")),
    ]:
        check(f"catalog/{name}", catalog, envelope(operation), expected_status, expect)

    for shape in ("unqualified", "qualified", "default-namespace"):
        check(f"gpserver/GetToolInfo/{shape}", gp, envelope(form("GetToolInfo", "<ToolName>Buffer</ToolName>", shape)), 200,
              lambda observed: observed.get("resultToolName") == "Buffer")
    check("gpserver/GetToolInfo/unqualified+Recurse", gp, envelope(form("GetToolInfo", "<Recurse>true</Recurse>", "unqualified")), 400,
          fault("GetToolInfo does not accept the 'Recurse' argument; its only argument is ToolName."))

    if api_key:
        for shape in ("unqualified", "qualified", "default-namespace"):
            check(f"gpserver/SubmitJob/{shape}", gp, envelope(form("SubmitJob", AREA_ARGUMENTS, shape)), 200,
                  lambda observed: observed.get("resultHasText") and observed.get("faultstring") is None, api_key)
        check("gpserver/SubmitJob/unqualified+Recurse", gp, envelope(form("SubmitJob", AREA_ARGUMENTS + "<Recurse>true</Recurse>", "unqualified")), 400,
              lambda observed: observed.get("faultstring") is not None, api_key)

    receipt = {
        "issue": "honua-io/honua-server#4973",
        "label": args.label,
        "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "baselineGetServiceDescriptions": {"status": status, "serviceDescriptionNames": baseline_names},
        "cases": cases,
        "summary": {
            "total": len(cases),
            "matchesContract": sum(1 for case in cases if case["matchesContract"]),
        },
    }
    with open(args.receipt, "w", encoding="utf-8") as handle:
        json.dump(receipt, handle, indent=2)
        handle.write("\n")
    print(json.dumps(receipt["summary"]))
    for case in cases:
        print(f'{"ok  " if case["matchesContract"] else "MISS"} {case["observedStatus"]} {case["case"]}')
    return 0 if receipt["summary"]["matchesContract"] == receipt["summary"]["total"] else 1


if __name__ == "__main__":
    sys.exit(main())
