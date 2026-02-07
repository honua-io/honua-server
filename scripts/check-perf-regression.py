#!/usr/bin/env python3
"""
Performance regression detection script for Honua Server.

Compares current benchmark results against baseline and reports regressions.
"""

import argparse
import json
import sys
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Tuple


class PerformanceRegression:
    """Represents a detected performance regression"""

    def __init__(self, metric: str, baseline_value: float, current_value: float,
                 threshold: float, regression_percent: float):
        self.metric = metric
        self.baseline_value = baseline_value
        self.current_value = current_value
        self.threshold = threshold
        self.regression_percent = regression_percent


class PerformanceChecker:
    """Performance regression checker"""

    def __init__(self, baseline_file: str, current_file: str, threshold: float = 0.10,
                 skip_on_env_mismatch: bool = False):
        self.baseline_file = Path(baseline_file)
        self.current_file = Path(current_file)
        self.threshold = threshold
        self.skip_on_env_mismatch = skip_on_env_mismatch
        self.regressions: List[PerformanceRegression] = []
        self.env_mismatches: List[Tuple[str, str, str]] = []
        self.env_mismatch_skipped = False

    def load_json(self, file_path: Path) -> Optional[Dict]:
        """Load and parse JSON file"""
        try:
            with open(file_path, 'r') as f:
                return json.load(f)
        except Exception as e:
            print(f"❌ Error loading {file_path}: {e}")
            return None

    def check_latency_regression(self, baseline: Dict, current: Dict) -> None:
        """Check for latency regressions in BenchmarkDotNet results"""
        baseline_benchmarks = {b['Method']: b for b in baseline.get('Benchmarks', [])}
        current_benchmarks = {b['Method']: b for b in current.get('Benchmarks', [])}

        for method, current_bench in current_benchmarks.items():
            if method not in baseline_benchmarks:
                print(f"⚠️ New benchmark: {method} (no baseline)")
                continue

            baseline_bench = baseline_benchmarks[method]

            # Check mean latency
            self._check_metric(
                f"{method}.Mean",
                baseline_bench['Statistics']['Mean'] / 1_000_000,  # Convert to ms
                current_bench['Statistics']['Mean'] / 1_000_000,
                higher_is_worse=True,
                min_delta=1.0
            )

            # Check P95 latency
            self._check_metric(
                f"{method}.P95",
                baseline_bench['Statistics']['Percentile95'] / 1_000_000,
                current_bench['Statistics']['Percentile95'] / 1_000_000,
                higher_is_worse=True,
                min_delta=1.0
            )

            # Check P99 latency
            self._check_metric(
                f"{method}.P99",
                baseline_bench['Statistics']['Percentile99'] / 1_000_000,
                current_bench['Statistics']['Percentile99'] / 1_000_000,
                higher_is_worse=True,
                min_delta=1.0
            )

            # Check memory allocations
            if 'Memory' in baseline_bench and 'Memory' in current_bench:
                self._check_metric(
                    f"{method}.AllocatedBytes",
                    baseline_bench['Memory']['AllocatedBytes'],
                    current_bench['Memory']['AllocatedBytes'],
                    higher_is_worse=True
                )

    def check_throughput_regression(self, baseline: Dict, current: Dict) -> None:
        """Check for throughput regressions in load test results"""
        baseline_load_tests = {t['TestName']: t for t in baseline.get('LoadTests', [])}
        current_load_tests = {t['TestName']: t for t in current.get('LoadTests', [])}

        for test_name, current_test in current_load_tests.items():
            if test_name not in baseline_load_tests:
                print(f"⚠️ New load test: {test_name} (no baseline)")
                continue

            baseline_test = baseline_load_tests[test_name]

            # Check requests per second (lower is worse)
            self._check_metric(
                f"{test_name}.RPS",
                baseline_test['RequestsPerSecond'],
                current_test['RequestsPerSecond'],
                higher_is_worse=False
            )

            # Check P95 latency (higher is worse)
            self._check_metric(
                f"{test_name}.P95Latency",
                baseline_test['P95LatencyMs'],
                current_test['P95LatencyMs'],
                higher_is_worse=True
            )

            # Check error rate (higher is worse)
            self._check_metric(
                f"{test_name}.ErrorRate",
                baseline_test['ErrorRatePercent'],
                current_test['ErrorRatePercent'],
                higher_is_worse=True
            )

    def check_memory_regression(self, baseline: Dict, current: Dict) -> None:
        """Check for memory leak regressions"""
        baseline_memory_tests = {t['TestName']: t for t in baseline.get('MemoryTests', [])}
        current_memory_tests = {t['TestName']: t for t in current.get('MemoryTests', [])}

        for test_name, current_test in current_memory_tests.items():
            if test_name not in baseline_memory_tests:
                print(f"⚠️ New memory test: {test_name} (no baseline)")
                continue

            baseline_test = baseline_memory_tests[test_name]

            # Check memory delta (higher is worse)
            self._check_metric(
                f"{test_name}.MemoryDeltaMB",
                baseline_test['MemoryDeltaMB'],
                current_test['MemoryDeltaMB'],
                higher_is_worse=True
            )

            # Check if memory leak is suspected
            if current_test.get('IsMemoryLeakSuspected', False):
                self.regressions.append(PerformanceRegression(
                    f"{test_name}.MemoryLeak",
                    0.0,
                    1.0,
                    0.0,
                    100.0
                ))

    def _check_metric(self, metric_name: str, baseline_value: float,
                     current_value: float, higher_is_worse: bool, min_delta: float = 0.0) -> None:
        """Check if a metric has regressed beyond threshold"""
        if baseline_value == 0:
            return  # Skip division by zero
        if abs(current_value - baseline_value) < min_delta:
            return

        if higher_is_worse:
            # For latency, memory, etc - higher values are worse
            change_percent = (current_value - baseline_value) / baseline_value
            if change_percent > self.threshold:
                self.regressions.append(PerformanceRegression(
                    metric_name, baseline_value, current_value,
                    self.threshold, change_percent
                ))
        else:
            # For throughput, etc - lower values are worse
            change_percent = (baseline_value - current_value) / baseline_value
            if change_percent > self.threshold:
                    self.regressions.append(PerformanceRegression(
                        metric_name, baseline_value, current_value,
                        self.threshold, change_percent
                    ))

    @staticmethod
    def _normalize_env_value(value: Optional[str]) -> str:
        return " ".join(str(value or "").split()).strip()

    def _collect_environment_mismatches(self, baseline: Dict, current: Dict) -> None:
        self.env_mismatches = []
        baseline_env = baseline.get('Environment', {}) or {}
        current_env = current.get('Environment', {}) or {}

        for key in ("Runtime", "OS", "Hardware"):
            baseline_value = self._normalize_env_value(baseline_env.get(key))
            current_value = self._normalize_env_value(current_env.get(key))
            if baseline_value and current_value and baseline_value != current_value:
                self.env_mismatches.append((key, baseline_value, current_value))

    def check_regressions(self) -> bool:
        """Check for performance regressions"""
        print(f"🔍 Checking performance regressions (threshold: {self.threshold*100:.1f}%)")
        print(f"📊 Baseline: {self.baseline_file}")
        print(f"📈 Current:  {self.current_file}")
        print()

        # Load baseline and current results
        baseline_data = self.load_json(self.baseline_file)
        current_data = self.load_json(self.current_file)

        if not baseline_data or not current_data:
            return False

        self._collect_environment_mismatches(baseline_data, current_data)
        if self.env_mismatches:
            print("⚠️ Environment mismatch detected:")
            for key, baseline_value, current_value in self.env_mismatches:
                print(f"  {key}: {baseline_value} → {current_value}")
            if self.skip_on_env_mismatch:
                print("ℹ️ Skipping regression checks due to environment mismatch.")
                self.env_mismatch_skipped = True
                return True

        # Check different types of regressions
        self.check_latency_regression(baseline_data, current_data)
        self.check_throughput_regression(baseline_data, current_data)
        self.check_memory_regression(baseline_data, current_data)

        # Report results
        if self.regressions:
            print(f"❌ {len(self.regressions)} performance regression(s) detected:")
            print()

            for regression in self.regressions:
                print(f"  🔴 {regression.metric}")
                print(f"      Baseline: {regression.baseline_value:.2f}")
                print(f"      Current:  {regression.current_value:.2f}")
                print(f"      Change:   {regression.regression_percent*100:+.1f}% (threshold: {regression.threshold*100:.1f}%)")
                print()

            return False
        else:
            print("✅ No performance regressions detected")
            return True

    def generate_report(self, output_file: Optional[str] = None) -> str:
        """Generate a performance comparison report"""
        report = f"""# Performance Comparison Report

Generated: {datetime.now().isoformat()}
Threshold: {self.threshold*100:.1f}%

## Summary

"""

        if self.env_mismatches:
            report += "## Environment Mismatch\n\n"
            for key, baseline_value, current_value in self.env_mismatches:
                report += f"- **{key}**: {baseline_value} → {current_value}\n"
            if self.env_mismatch_skipped:
                report += "\nRegression checks were skipped because environments differ.\n"
            report += "\n"

        if self.regressions:
            report += f"❌ **{len(self.regressions)} regression(s) detected**\n\n"

            report += "## Regressions\n\n"
            for regression in self.regressions:
                report += f"- **{regression.metric}**: {regression.baseline_value:.2f} → {regression.current_value:.2f} ({regression.regression_percent*100:+.1f}%)\n"
        else:
            report += "✅ **No regressions detected**\n\n"

        report += f"""
## Files Compared

- Baseline: `{self.baseline_file}`
- Current: `{self.current_file}`

## Thresholds

- Latency regression: >{self.threshold*100:.1f}% increase
- Throughput regression: >{self.threshold*100:.1f}% decrease
- Memory regression: >{self.threshold*100:.1f}% increase
"""

        if output_file:
            with open(output_file, 'w') as f:
                f.write(report)
            print(f"📄 Report saved to: {output_file}")

        return report


def main():
    parser = argparse.ArgumentParser(description='Check for performance regressions')
    parser.add_argument('--baseline', required=True, help='Baseline performance results file')
    parser.add_argument('--current', required=True, help='Current performance results file')
    parser.add_argument('--threshold', type=float, default=0.10,
                       help='Regression threshold (default: 0.10 = 10%)')
    parser.add_argument('--report', help='Generate markdown report to file')
    parser.add_argument('--skip-on-env-mismatch', action='store_true',
                       help='Skip regression checks when baseline and current environments differ')
    parser.add_argument('--fail-on-regression', action='store_true',
                       help='Exit with non-zero code on regression')

    args = parser.parse_args()

    checker = PerformanceChecker(
        args.baseline,
        args.current,
        args.threshold,
        skip_on_env_mismatch=args.skip_on_env_mismatch
    )
    no_regressions = checker.check_regressions()

    if args.report:
        checker.generate_report(args.report)

    if args.fail_on_regression and not no_regressions:
        sys.exit(1)
    elif no_regressions:
        sys.exit(0)
    else:
        sys.exit(0)  # Don't fail by default, just report


if __name__ == '__main__':
    main()
