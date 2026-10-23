#!/usr/bin/env python3
"""Build ab-report.json: Honua vs the Esri reference vs the independent oracle (#3558).

    python tools/ab_report.py --raw-dir <HONUA_3558_RAW>   # first run: extracts GMI bodies
    python tools/ab_report.py                              # later runs: committed files only

A = the Esri reference ImageServer (SeaTemperature), driven by the same Pro steps.
B = Honua serving the genuine scanned NetCDF fixture.
Oracle = the values authored in docker/client-compat/multidim-fixture/seed.py,
computed here without reading any server output.
"""
import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
BUNDLE = HERE.parent
sys.path.insert(0, str(HERE))
from binary_soap_strings import fingerprint  # noqa: E402

# Independent oracle (seed.py): 4x4 cells of 0.05 deg centred on
# lon -122.50..-122.35 / lat 37.70..37.85, two daily time slices, fill -9999.
ORACLE = {
    "variables": ["sea_surface_temperature"],
    "dimension_names": ["StdTime"],
    "time_values": ["2024-01-01T00:00:00", "2024-01-02T00:00:00"],
    "extent": [-122.525, 37.675, -122.325, 37.875],
    "width": 4, "height": 4,
    "pixel_type": "F32",
    "no_data_value": -9999.0,
    "unit": "degC",
    "description": "Sea Surface Temperature",
}


def oracle_slices():
    """North-up rows (as ArcPy returns them); None marks nodata."""
    slices = []
    for t in range(2):
        rows = []
        for r in range(4):
            ordinal_row = 3 - r  # authored south-to-north
            row = []
            for c in range(4):
                ordinal = ordinal_row * 4 + c
                value = 10 + ordinal if t == 0 else 35 - ordinal
                nodata = (t == 0 and ordinal == 0) or (t == 1 and ordinal == 15)
                row.append(None if nodata else float(value))
            rows.append(row)
        slices.append(rows)
    return slices


def load_run(name):
    run = BUNDLE / "runs" / name
    receipt = json.loads((run / "pro-receipt.json").read_text(encoding="utf-8"))
    exchanges = [json.loads(line) for line in (run / "exchanges.jsonl").read_text(encoding="utf-8").splitlines()]
    return run, receipt, exchanges


def step(receipt, name):
    return next((s for s in receipt["steps"] if s["step"] == name), None)


def operations(exchanges):
    table = {}
    for e in exchanges:
        if e["method"] != "POST":
            continue
        op = (e["request"] or {}).get("operation") or e["binary_request_operation"]
        if not op:
            continue
        encoding = "xml" if e["request"] else "binary"
        response = (e["response"] or {}).get("operation") or e["binary_response_operation"] or f"HTTP {e['status']}"
        row = table.setdefault(op, {})
        key = f"{encoding}:{e['status']}:{response}"
        row[key] = row.get(key, 0) + 1
    return table


def extract_gmi(raw_dir: Path):
    """Copy the first successful GMI body of each run into the bundle (no credentials in either)."""
    for name in ("honua-trunk", "reference"):
        run, _, exchanges = load_run(name)
        for e in exchanges:
            op = (e["request"] or {}).get("operation") or e["binary_request_operation"]
            response = (e["response"] or {}).get("operation") or e["binary_response_operation"]
            if op == "GetMultidimensionalInfo" and response == "GetMultidimensionalInfoResponse":
                body = (raw_dir / name / f"{e['sequence']:05d}.response").read_bytes()
                if e["response"]:
                    (run / "GetMultidimensionalInfo.response.xml").write_bytes(body)
                else:
                    (run / "GetMultidimensionalInfo.response.strings.json").write_text(
                        json.dumps(fingerprint(body), indent=2) + "\n", encoding="utf-8")
                break


def honua_gmi_structure(path: Path):
    root = ET.fromstring(path.read_bytes())

    def local(tag):
        return tag.rsplit("}", 1)[-1]

    def keys(value):
        array = next((c for c in value if local(c.tag) == "PropertyArray"), None)
        return [] if array is None else [
            (next(p for p in prop if local(p.tag) == "Key").text, next(p for p in prop if local(p.tag) == "Value"))
            for prop in array]

    result = next(e for e in root.iter() if local(e.tag) == "Result")
    names = [s.text for s in next(c for c in result if local(c.tag) == "Names")]
    sets = list(next(c for c in result if local(c.tag) == "Values"))
    structure = {"names": names}
    for name, value in zip(names, sets):
        structure[name] = {
            var: ({k: [kk for kk, _ in keys(v)] for k, v in keys(inner)} if name == "DimensionAttributes"
                  else [k for k, _ in keys(inner)])
            for var, inner in keys(value)}
    return structure


def reference_gmi_structure(strings):
    # Order observed in the binary body: set names, then variable, then keys.
    out = {"names": [s for s in strings if s in {"Variables", "DimensionAttributes", "DimensionValues"}]}
    dims = {}
    current = None
    attribute_keys = {"Description", "Unit", "Count", "Minimum", "Maximum", "Interval", "IntervalUnit",
                      "HasRegularIntervals", "HasRanges"}
    section = None
    for s in strings:
        if s in {"Variables", "DimensionAttributes", "DimensionValues"}:
            section = s
            continue
        if section == "DimensionAttributes":
            if s in {"StdTime", "StdZ"}:
                current = dims.setdefault(s, [])
            elif current is not None and s in attribute_keys:
                current.append(s)
    out["DimensionAttributes"] = dims
    out["DimensionValues"] = [s for s in strings[strings.index("DimensionValues") + 1:] if s in {"StdTime", "StdZ"}] \
        if "DimensionValues" in strings else []
    out["variable_keys"] = sorted({s for s in strings[strings.index("Variables"):strings.index("DimensionAttributes")]
                                   if s in {"Unit", "Description"}})
    return out


def compare(observed, expected, tolerance=1e-6):
    if isinstance(expected, list) and isinstance(observed, list) and len(expected) == len(observed) \
            and all(isinstance(v, (int, float)) for v in expected + observed):
        return all(abs(a - b) <= tolerance for a, b in zip(observed, expected))
    return observed == expected


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--raw-dir", type=Path)
    args = parser.parse_args()
    if args.raw_dir:
        extract_gmi(args.raw_dir)

    _, honua, honua_exchanges = load_run("honua-trunk")
    _, reference, reference_exchanges = load_run("reference")

    props = (step(honua, "RasterProperties") or {}).get("observed") or {}
    ref_props = (step(reference, "RasterProperties") or {}).get("observed") or {}
    attributes = props.get("variable_attributes") or {}
    observed = {
        "variables": [v.split("(")[0] for v in props.get("variables", [])],
        "dimension_names": props.get("dimension_names"),
        "extent": props.get("extent"),
        "width": props.get("width"), "height": props.get("height"),
        "pixel_type": props.get("pixel_type"),
        "no_data_value": props.get("no_data_value"),
        "unit": attributes.get("Unit"),
        "description": attributes.get("Description"),
    }
    dims = (step(honua, "DimensionValues") or {})
    time_values = None
    if dims.get("status") == "pass":
        time_values = (dims["observed"].get("StdTime") or {}).get("values")
    observed["time_values"] = time_values

    metadata = {}
    for key, expected in ORACLE.items():
        value = observed.get(key)
        metadata[key] = {"honua_via_pro": value, "oracle": expected, "pass": compare(value, expected)}

    slices = (step(honua, "SliceValues") or {})
    band_values = {
        "oracle_north_up": oracle_slices(),
        "pro_slice_read": {"status": slices.get("status", "not-run (no dimension values)"),
                           "observed": slices.get("observed")},
        "pro_raster_read": {"status": (step(honua, "RasterRead") or {}).get("status")},
        "pass": False,
    }

    honua_gmi = honua_gmi_structure(BUNDLE / "runs/honua-trunk/GetMultidimensionalInfo.response.xml")
    ref_strings = json.loads((BUNDLE / "runs/reference/GetMultidimensionalInfo.response.strings.json")
                             .read_text(encoding="utf-8"))["strings"]
    ref_gmi = reference_gmi_structure(ref_strings)
    honua_var = next(iter(honua_gmi["Variables"].values()), [])
    honua_dims = next(iter(honua_gmi["DimensionAttributes"].values()), {})
    reference_dim_keys = set().union(*ref_gmi["DimensionAttributes"].values()) if ref_gmi["DimensionAttributes"] else set()
    soap_shape = {
        "reference": ref_gmi,
        "honua": honua_gmi,
        "property_set_names_equal": honua_gmi["names"] == ref_gmi["names"],
        "variable_keys_subset_of_reference": set(honua_var) <= set(ref_gmi["variable_keys"]),
        "dimension_keys_subset_of_reference": all(set(k) <= reference_dim_keys for k in honua_dims.values()),
        "dimension_names_follow_reference_convention": set(honua_dims) <= {"StdTime", "StdZ"},
        "honua_dimension_values_populated": any(honua_gmi["DimensionValues"].values()),
    }

    report = {
        "issue": "honua-io/honua-server#3558",
        "pro": {"version": honua["pro_version"], "product": honua["product"]},
        "baseline_a_nil_response": {
            "source": "issue #3558 comments 2026-09-01: certified image sha256:e6db39f6... returned <Result xsi:nil=true/>",
            "now": "non-nil typed property sets on trunk (#5775); see soap_shape"},
        "soap_operations": {"honua": operations(honua_exchanges), "reference": operations(reference_exchanges)},
        "pro_steps": {"honua": {s["step"]: s["status"] for s in honua["steps"]},
                      "reference": {s["step"]: s["status"] for s in reference["steps"]}},
        "reference_pro_view": {k: ref_props.get(k) for k in ("is_multidimensional", "dimension_names", "pixel_type",
                                                              "band_count", "variables")},
        "soap_shape": soap_shape,
        "metadata": metadata,
        "band_values": band_values,
        "defects": {
            "#6491": "scan enrichment never runs for /vsi NetCDF in containers -> no StdTime / coordinate values",
            "#6492": "horizontal axes advertised as slice dimensions",
            "#6493": "service raster info describes the layer's base raster, not the coverage",
        },
    }
    out = BUNDLE / "ab-report.json"
    out.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({k: v["pass"] for k, v in metadata.items()}))
    print(json.dumps({k: v for k, v in soap_shape.items() if isinstance(v, bool)}))


if __name__ == "__main__":
    main()
