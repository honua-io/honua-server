#!/usr/bin/env bash
set -euo pipefail

# Performance-parity gate (issue #1249).
#
# Grades the Honua-vs-source p95/p99 latency ratios carried by a generated parity scorecard
# against a configurable perf budget and FAILS when latency regresses beyond the budget. This is
# the performance analogue of scripts/ci/check-import-fidelity-scorecard-regression.sh (the correctness
# gate). The C# GeoServicesPerfParityGate already writes a PerfParity.Verdict into each scorecard
# case; this script enforces that verdict AND independently re-checks the raw ratios against the
# budget so the gate holds even if the embedded verdict is stale or missing.
#
# Usage:
#   check-import-fidelity-perf-budget.sh <scorecard.json> [budget.json]
#
# Budget JSON (all keys optional; omitted keys disable that threshold):
#   { "warnP95": 1.5, "failP95": 2.0, "warnP99": 1.75, "failP99": 2.5, "minSamples": 5 }
#
# Budget may also be supplied via environment variables (override the budget file):
#   HONUA_PERF_PARITY_WARN_P95, HONUA_PERF_PARITY_FAIL_P95,
#   HONUA_PERF_PARITY_WARN_P99, HONUA_PERF_PARITY_FAIL_P99,
#   HONUA_PERF_PARITY_MIN_SAMPLES

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <scorecard.json> [budget.json]" >&2
  exit 1
fi

scorecard="$1"
budget_file="${2:-}"

if [[ ! -f "$scorecard" ]]; then
  echo "Scorecard not found: $scorecard" >&2
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required to grade the perf-parity scorecard." >&2
  exit 1
fi

# Defaults mirror PerfParityBudget.GeoServicesDefault in C#.
warn_p95="1.5"
fail_p95="2.0"
warn_p99="1.75"
fail_p99="2.5"
min_samples="5"

if [[ -n "$budget_file" ]]; then
  if [[ ! -f "$budget_file" ]]; then
    echo "Budget file not found: $budget_file" >&2
    exit 1
  fi
  warn_p95="$(jq -r '.warnP95 // empty' "$budget_file" 2>/dev/null || true)"; warn_p95="${warn_p95:-1.5}"
  fail_p95="$(jq -r '.failP95 // empty' "$budget_file" 2>/dev/null || true)"; fail_p95="${fail_p95:-2.0}"
  warn_p99="$(jq -r '.warnP99 // empty' "$budget_file" 2>/dev/null || true)"; warn_p99="${warn_p99:-1.75}"
  fail_p99="$(jq -r '.failP99 // empty' "$budget_file" 2>/dev/null || true)"; fail_p99="${fail_p99:-2.5}"
  min_samples="$(jq -r '.minSamples // empty' "$budget_file" 2>/dev/null || true)"; min_samples="${min_samples:-5}"
fi

# Environment variables win over the budget file.
warn_p95="${HONUA_PERF_PARITY_WARN_P95:-$warn_p95}"
fail_p95="${HONUA_PERF_PARITY_FAIL_P95:-$fail_p95}"
warn_p99="${HONUA_PERF_PARITY_WARN_P99:-$warn_p99}"
fail_p99="${HONUA_PERF_PARITY_FAIL_P99:-$fail_p99}"
min_samples="${HONUA_PERF_PARITY_MIN_SAMPLES:-$min_samples}"

echo "Perf-parity budget: p95 warn>=${warn_p95} fail>=${fail_p95}; p99 warn>=${warn_p99} fail>=${fail_p99}; minSamples=${min_samples}"

# Re-grade every case from the raw ratios; this is the authoritative gate. A case fails when a
# graded ratio reaches the fail budget. Cases with too few samples or no measured ratio are
# reported as "unknown" and never fail the gate (they cannot prove a regression).
failures="$(
  jq -r \
    --argjson failP95 "$fail_p95" \
    --argjson failP99 "$fail_p99" \
    --argjson minSamples "$min_samples" '
      .Cases[]
      | . as $case
      | ($case.LatencyMetrics // {}) as $lm
      | ($lm.SampleCount // 0) as $samples
      | ($lm.HonuaToSourceP95Ratio) as $p95
      | ($lm.HonuaToSourceP99Ratio) as $p99
      | if $samples < $minSamples then empty
        else
          ( if ($p95 != null and $p95 >= $failP95)
              then "\($case.ServiceCase): p95 ratio \($p95) >= fail budget \($failP95)"
              else empty end ),
          ( if ($p99 != null and $p99 >= $failP99)
              then "\($case.ServiceCase): p99 ratio \($p99) >= fail budget \($failP99)"
              else empty end )
        end
    ' "$scorecard"
)"

# Cross-check the embedded verdict the C# gate wrote, if present, so a Fail verdict is honored even
# if a future ratio shape changes.
verdict_failures="$(
  jq -r '
    .Cases[]
    | select(.PerfParity != null and .PerfParity.Verdict == "Fail")
    | "\(.ServiceCase): embedded PerfParity verdict is Fail — \(.PerfParity.Summary // "")"
  ' "$scorecard"
)"

all_failures="$(printf '%s\n%s\n' "$failures" "$verdict_failures" | sed '/^$/d' | sort -u)"

if [[ -n "$all_failures" ]]; then
  echo "Performance-parity gate FAILED — latency regressed beyond the budget:" >&2
  printf '%s\n' "$all_failures" | sed 's/^/- /' >&2
  exit 1
fi

# Surface warnings (non-blocking) for visibility.
warnings="$(
  jq -r \
    --argjson warnP95 "$warn_p95" \
    --argjson failP95 "$fail_p95" \
    --argjson warnP99 "$warn_p99" \
    --argjson failP99 "$fail_p99" \
    --argjson minSamples "$min_samples" '
      .Cases[]
      | . as $case
      | ($case.LatencyMetrics // {}) as $lm
      | ($lm.SampleCount // 0) as $samples
      | ($lm.HonuaToSourceP95Ratio) as $p95
      | ($lm.HonuaToSourceP99Ratio) as $p99
      | if $samples < $minSamples then empty
        else
          ( if ($p95 != null and $p95 >= $warnP95 and $p95 < $failP95)
              then "\($case.ServiceCase): p95 ratio \($p95) >= warn budget \($warnP95)"
              else empty end ),
          ( if ($p99 != null and $p99 >= $warnP99 and $p99 < $failP99)
              then "\($case.ServiceCase): p99 ratio \($p99) >= warn budget \($warnP99)"
              else empty end )
        end
    ' "$scorecard"
)"

if [[ -n "$warnings" ]]; then
  echo "Performance-parity warnings (within fail budget):"
  printf '%s\n' "$warnings" | sed 's/^/- /'
fi

echo "Performance-parity gate PASSED — all sampled operations are within the latency budget."
