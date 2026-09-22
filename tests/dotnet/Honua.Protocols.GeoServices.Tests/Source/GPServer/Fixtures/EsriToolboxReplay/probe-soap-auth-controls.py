#!/usr/bin/env python3
"""Replay the SOAP GPServer authentication controls against a live fixture.

Every expectation is declared here before any request is sent; nothing is
copied from a server response. Transport is verified HTTPS only.

Environment:
  HONUA_PROBE_ORIGIN      https origin of the fixture proxy
  HONUA_PROBE_CA_BUNDLE   PEM root that must verify the proxy certificate
  HONUA_PROBE_API_KEY     authorized credential (never written to the receipt)
  HONUA_PROBE_SOURCE      server source SHA recorded by the fixture
  HONUA_PROBE_IMAGE       running container image digest
  HONUA_PROBE_OUTPUT      receipt path
"""

import base64
import datetime
import json
import os
import secrets
import ssl
import struct
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

SOAP = "http://schemas.xmlsoap.org/soap/envelope/"
ARCGIS = "http://www.esri.com/schemas/ArcGIS/10.8"
SERVICE = "desktop_ui_features"
AREA_TOOL = "Honua_" + "geometry.area".encode().hex().upper()
ARCPY_DEFAULT_CONTROLS = (
    "<Options><DensifyFeatures>false</DensifyFeatures><TransportType>esriGDSTransportTypeUrl</TransportType>"
    "<ReturnData>false</ReturnData><UpdateValues>true</UpdateValues></Options>"
    "<EnvironmentValues><PropertyArray>"
    "<PropertySetProperty><Key>outputZFlag</Key><Value xsi:type=\"tns:GPString\"><Value>Same As Input</Value></Value></PropertySetProperty>"
    "<PropertySetProperty><Key>outputMFlag</Key><Value xsi:type=\"tns:GPString\"><Value>Same As Input</Value></Value></PropertySetProperty>"
    "<PropertySetProperty><Key>randomGenerator</Key><Value xsi:type=\"tns:GPRandomNumberGenerator\"><Value>0</Value><GPRandomNumberGenerator>ACM599</GPRandomNumberGenerator></Value></PropertySetProperty>"
    "<PropertySetProperty><Key>autoCommit</Key><Value xsi:type=\"tns:GPLong\"><Value>1000</Value></Value></PropertySetProperty>"
    "<PropertySetProperty><Key>cellSizeProjectionMethod</Key><Value xsi:type=\"tns:GPString\"><Value>CONVERT_UNITS</Value></Value></PropertySetProperty>"
    "<PropertySetProperty><Key>nodata</Key><Value xsi:type=\"tns:GPString\"><Value>NONE</Value></Value></PropertySetProperty>"
    "</PropertyArray></EnvironmentValues>"
)


def rectangle_wkb() -> str:
    # Literal OGC WKB little-endian polygon (0,0)-(3,4); expected area is 3 * 4.
    ring = [(0.0, 0.0), (3.0, 0.0), (3.0, 4.0), (0.0, 4.0), (0.0, 0.0)]
    data = struct.pack("<BIII", 1, 3, 1, len(ring)) + b"".join(struct.pack("<dd", x, y) for x, y in ring)
    return base64.b64encode(data).decode()


def area_arguments() -> str:
    return (
        f"<ToolName>{AREA_TOOL}</ToolName><Values xsi:type=\"tns:GPValues\">"
        f"<GPValue xsi:type=\"tns:GPString\"><Value>{rectangle_wkb()}</Value></GPValue>"
        "<GPValue xsi:type=\"tns:GPLong\"><Value>3857</Value></GPValue></Values>"
        + ARCPY_DEFAULT_CONTROLS
    )


def main() -> int:
    origin = os.environ["HONUA_PROBE_ORIGIN"].rstrip("/")
    if not origin.startswith("https://"):
        raise ValueError("verified HTTPS is required")
    credential = os.environ["HONUA_PROBE_API_KEY"]
    context = ssl.create_default_context(cafile=os.environ["HONUA_PROBE_CA_BUNDLE"])
    url = f"{origin}/services/{SERVICE}/GPServer"

    def post(operation: str, arguments: str, headers: dict) -> tuple[int, str]:
        body = (
            f"<soap:Envelope xmlns:soap=\"{SOAP}\" xmlns:tns=\"{ARCGIS}\" "
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            f"<soap:Body><tns:{operation}>{arguments}</tns:{operation}></soap:Body></soap:Envelope>"
        ).encode()
        request = urllib.request.Request(url, data=body, method="POST", headers={
            "Content-Type": "text/xml; charset=utf-8", "SOAPAction": "\"\"", **headers})
        try:
            with urllib.request.urlopen(request, context=context, timeout=60) as response:
                return response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode()

    def result_of(text: str) -> ET.Element:
        return ET.fromstring(text).find(".//Result")

    def is_fault(text: str) -> bool:
        try:
            return ET.fromstring(text).find(f".//{{{SOAP}}}Fault") is not None
        except ET.ParseError:
            return False

    authorized = {"X-API-Key": credential}
    checks = []

    # Positive control: the authorized caller runs the literal job to success.
    status, text = post("SubmitJob", area_arguments(), authorized)
    assert status == 200, f"authorized SubmitJob returned {status}"
    job_id = result_of(text).text
    deadline = time.monotonic() + 60
    job_status = None
    while time.monotonic() < deadline:
        status, text = post("GetJobStatus", f"<JobID>{job_id}</JobID>", authorized)
        job_status = result_of(text).text if status == 200 else f"http-{status}"
        if job_status in ("esriJobSucceeded", "esriJobFailed", "esriJobCancelled") or status != 200:
            break
        time.sleep(0.2)
    status, text = post("GetJobResult",
                        f"<JobID>{job_id}</JobID><ParameterNames><String>outputScalar</String></ParameterNames>",
                        authorized)
    value = result_of(text).find("Values/GPValue/Value").text if status == 200 else ""
    prefix = "data:application/json;base64,"
    measure = json.loads(base64.b64decode(value[len(prefix):])) if value.startswith(prefix) else {}
    checks.append({
        "case": "authorized-submit-status-result",
        "expected": {"job_status": "esriJobSucceeded", "area": 3 * 4, "type": "MeasureResult",
                     "processId": "geometry.area", "inputSrid": 3857, "inputGeometryType": "Polygon"},
        "actual": {"job_status": job_status, "result_status": status, "area": measure.get("value"),
                   "type": measure.get("type"), "processId": measure.get("processId"),
                   "inputSrid": measure.get("inputSrid"), "inputGeometryType": measure.get("inputGeometryType")},
    })
    checks[-1]["passed"] = checks[-1]["expected"] == {k: v for k, v in checks[-1]["actual"].items() if k != "result_status"}

    # Denials: no credential, an unknown API key, an unknown bearer token. Each
    # must be refused with a 401 SOAP fault that leaks no job state or result.
    callers = {
        "anonymous": {},
        "invalid-api-key": {"X-API-Key": "honua-probe-" + secrets.token_hex(16)},
        "invalid-bearer": {"Authorization": "Bearer honua-probe-" + secrets.token_hex(16)},
    }
    operations = {
        "SubmitJob": area_arguments(),
        "Execute": area_arguments(),
        "GetJobStatus": f"<JobID>{job_id}</JobID>",
        "GetJobMessages": f"<JobID>{job_id}</JobID>",
        "GetJobToolName": f"<JobID>{job_id}</JobID>",
        "GetJobResult": f"<JobID>{job_id}</JobID><ParameterNames><String>outputScalar</String></ParameterNames>",
        "CancelJob": f"<JobID>{job_id}</JobID>",
    }
    for caller, headers in callers.items():
        for operation, arguments in operations.items():
            status, text = post(operation, arguments, headers)
            leaked = [marker for marker in ("esriJob", AREA_TOOL, "geometry.area", "data:application/json", job_id)
                      if marker in text]
            checks.append({
                "case": f"{caller}-{operation}", "expected_status": 401, "actual_status": status,
                "soap_fault": is_fault(text), "leaked_markers": leaked,
                "passed": status == 401 and is_fault(text) and not leaked,
            })

    # The owner's job is unchanged by the refused CancelJob attempts.
    status, text = post("GetJobStatus", f"<JobID>{job_id}</JobID>", authorized)
    after = result_of(text).text if status == 200 else f"http-{status}"
    checks.append({"case": "authorized-status-after-denied-cancels", "expected": "esriJobSucceeded",
                   "actual": after, "passed": after == "esriJobSucceeded"})

    # Authorized but malformed: the credential is accepted and the payload rejected.
    status, text = post("SubmitJob", f"<ToolName>{AREA_TOOL}</ToolName><Values xsi:type=\"tns:GPValues\">"
                        "<GPValue xsi:type=\"tns:GPLong\"><Value>not-a-geometry</Value></GPValue></Values>",
                        authorized)
    checks.append({"case": "authorized-invalid-parameters", "expected_status": 400, "actual_status": status,
                   "soap_fault": is_fault(text), "passed": status == 400 and is_fault(text)})

    report = {
        "at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "source": os.environ["HONUA_PROBE_SOURCE"], "image": os.environ["HONUA_PROBE_IMAGE"],
        "tls_verified": True, "service": SERVICE, "evidence_kind": "verified-https-soap-requests",
        "desktop_ui_exercised": False, "checks": checks,
        "passed": all(check["passed"] for check in checks),
    }
    serialized = json.dumps(report, indent=2) + "\n"
    if credential in serialized:
        raise RuntimeError("credential would be written to the receipt")
    with open(os.environ["HONUA_PROBE_OUTPUT"], "w", encoding="utf-8") as handle:
        handle.write(serialized)
    print(json.dumps({"passed": report["passed"],
                      "failed": [c["case"] for c in checks if not c["passed"]]}, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
