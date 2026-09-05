#!/usr/bin/env python3
"""Evaluate the published platform-fault selector with promtool, including overlap."""

import argparse
import json
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--promtool", default="promtool", help="Path to promtool")
    args = parser.parse_args()
    contract = json.loads(
        Path(__file__).with_name("slo-metric-contract.json").read_text()
    )
    numerator = contract["slo_ratios"]["platform_fault_rate"]["numerator"]
    cases = [
        ("In-band logical 500 is counted once", [(500, True, 10)], 10),
        ("In-band logical 400 remains a platform fault", [(400, True, 10)], 10),
        ("Transport 503 alone remains visible", [(503, False, 10)], 10),
        ("Ordinary transport 400 is excluded", [(400, False, 100)], None),
        (
            "Mixed errors count once and exclude ordinary client errors",
            [(500, True, 2), (400, True, 3), (503, False, 5), (400, False, 100)],
            10,
        ),
        ("Absent traffic does not manufacture faults", [], None),
    ]
    tests = []
    for name, samples, expected in cases:
        series = []
        for code, in_band, count in samples:
            labels = (
                f'service_type="FeatureServer",operation="query",error_code="{code}",'
                f'in_band="{str(in_band).lower()}"'
            )
            series.append(
                {"series": f"honua_request_error_total{{{labels}}}", "values": str(count)}
            )
        tests.append(
            {
                "name": name,
                "interval": "1m",
                "input_series": series,
                "promql_expr_test": [
                    {
                        "expr": f"sum({numerator})",
                        "eval_time": "0m",
                        "exp_samples": []
                        if expected is None
                        else [{"labels": "{}", "value": expected}],
                    }
                ],
            }
        )
    # JSON is valid YAML; use the expression from the contract itself so the
    # fixture cannot stay green while a separately copied expression drifts.
    with tempfile.TemporaryDirectory(prefix="honua-slo-contract-") as directory:
        fixture = Path(directory) / "contract.test.yml"
        fixture.write_text(json.dumps({"evaluation_interval": "1m", "tests": tests}))
        return subprocess.run(
            [args.promtool, "test", "rules", str(fixture)], check=False
        ).returncode


if __name__ == "__main__":
    raise SystemExit(main())
