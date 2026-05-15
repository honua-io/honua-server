#!/usr/bin/env bash

# Builds a static, website-linkable evidence bundle from local CITE result
# directories. The bundle includes a human-readable index, machine-readable
# summary JSON, markdown summary, and the full TeamEngine result trees.

set -euo pipefail

OUTPUT_DIR="artifacts/cite-evidence"
STRICT=false
PUBLIC_BASE_URL="${CITE_EVIDENCE_PUBLIC_BASE_URL:-}"

usage() {
    cat <<'EOF_USAGE'
Usage: scripts/conformance/cite/build-cite-evidence.sh [OPTIONS]

Options:
  --output DIR   Output directory for the static evidence bundle
  --public-url   Public base URL where this bundle will be hosted
  --strict       Fail if any expected suite is missing, failed, or produced no tests
  --help, -h     Show this help
EOF_USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --public-url)
            PUBLIC_BASE_URL="${2%/}"
            shift 2
            ;;
        --strict)
            STRICT=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

if ! command -v jq >/dev/null 2>&1; then
    echo "jq is required to build conformance-summary.json" >&2
    exit 1
fi

GENERATED_AT=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
GIT_SHA=$(git rev-parse HEAD 2>/dev/null || echo "unknown")
GIT_REF=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
REPOSITORY=${GITHUB_REPOSITORY:-$(basename "$(git rev-parse --show-toplevel 2>/dev/null || pwd)")}
RUN_ID=${GITHUB_RUN_ID:-local}
RUN_NUMBER=${GITHUB_RUN_NUMBER:-local}
SERVER_VERSION=$(git describe --tags --always 2>/dev/null || echo "$GIT_SHA")
GITHUB_RUN_URL=""
if [[ -n "${GITHUB_SERVER_URL:-}" && -n "${GITHUB_REPOSITORY:-}" && -n "${GITHUB_RUN_ID:-}" ]]; then
    GITHUB_RUN_URL="${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}"
elif [[ "$REPOSITORY" == */* && "$RUN_ID" != "local" ]]; then
    GITHUB_RUN_URL="https://github.com/${REPOSITORY}/actions/runs/${RUN_ID}"
fi

# Keep this list limited to suites that are eligible for the public 100% passed
# evidence claim.
SUITES=(
    "ogcapi-features|OGC API Features|cite-results|cite-summary.md"
    "ogcapi-tiles|OGC API Tiles|cite-tiles-results|cite-tiles-summary.md"
    "wfs10|WFS 1.0|cite-wfs10-results|cite-wfs10-summary.md"
    "wfs11|WFS 1.1|cite-wfs11-results|cite-wfs11-summary.md"
    "wfs20|WFS 2.0|cite-wfs20-results|cite-summary.md"
    "wms13|WMS 1.3|cite-wms-results|cite-wms-summary.md"
    "wmts10|WMTS 1.0|cite-wmts-results|cite-wmts-summary.md"
    "wcs20|WCS 2.0|cite-wcs20-results|cite-wcs20-summary.md"
    "gml32|GML 3.2|cite-gml32-results|cite-gml32-summary.md"
    "gpkg12|GeoPackage 1.2|cite-gpkg12-results|cite-gpkg12-summary.md"
    "kml22|KML 2.2|cite-kml22-results|cite-kml22-summary.md"
)

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR/reports" "$OUTPUT_DIR/badges"

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT
SUITE_JSONL="$TMP_DIR/suites.jsonl"
HTML_ROWS="$TMP_DIR/table-rows.html"
MD_ROWS="$TMP_DIR/table-rows.md"
: > "$SUITE_JSONL"
: > "$HTML_ROWS"
: > "$MD_ROWS"

html_escape() {
    local value="$1"
    value=${value//&/&amp;}
    value=${value//</&lt;}
    value=${value//>/&gt;}
    value=${value//\"/&quot;}
    printf '%s' "$value"
}

extract_summary_count() {
    local file="$1"
    local label="$2"

    if [[ ! -f "$file" ]]; then
        echo 0
        return
    fi

    awk -F: -v label="$label" '
        index($0, "**" label "**") {
            value = $2
            gsub(/[^0-9]/, "", value)
            if (value == "") {
                value = 0
            }
            print value
            found = 1
            exit
        }
        END {
            if (!found) {
                print 0
            }
        }
    ' "$file"
}

find_summary_file() {
    local results_dir="$1"
    local expected="$2"

    if [[ -f "$results_dir/$expected" ]]; then
        printf '%s\n' "$results_dir/$expected"
        return
    fi

    find "$results_dir" -maxdepth 1 -type f -name '*summary.md' | sort | head -n 1
}

find_teamengine_index() {
    local suite_output_dir="$1"
    find "$suite_output_dir" -type f -path '*/html/index.html' | sort | tail -n 1
}

write_badge() {
    local status="$1"
    local path="$2"
    local label_color="#555"
    local value_color="#16a34a"
    local value="passing"

    case "$status" in
        passed)
            value_color="#16a34a"
            value="passing"
            ;;
        failed)
            value_color="#dc2626"
            value="failing"
            ;;
        missing|no-results)
            value_color="#ca8a04"
            value="missing"
            ;;
        *)
            value_color="#6b7280"
            value="$status"
            ;;
    esac

    cat > "$path" <<EOF_BADGE
<svg xmlns="http://www.w3.org/2000/svg" width="166" height="20" role="img" aria-label="OGC CITE: $value">
  <title>OGC CITE: $value</title>
  <linearGradient id="s" x2="0" y2="100%">
    <stop offset="0" stop-color="#fff" stop-opacity=".7"/>
    <stop offset=".1" stop-color="#aaa" stop-opacity=".1"/>
    <stop offset=".9" stop-color="#000" stop-opacity=".3"/>
    <stop offset="1" stop-color="#000" stop-opacity=".5"/>
  </linearGradient>
  <clipPath id="r"><rect width="166" height="20" rx="3" fill="#fff"/></clipPath>
  <g clip-path="url(#r)">
    <rect width="74" height="20" fill="$label_color"/>
    <rect x="74" width="92" height="20" fill="$value_color"/>
    <rect width="166" height="20" fill="url(#s)"/>
  </g>
  <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,sans-serif" font-size="11">
    <text x="37" y="15" fill="#010101" fill-opacity=".3">OGC CITE</text>
    <text x="37" y="14">OGC CITE</text>
    <text x="120" y="15" fill="#010101" fill-opacity=".3">$value</text>
    <text x="120" y="14">$value</text>
  </g>
</svg>
EOF_BADGE
}

overall_status="passed"
suite_count=0

for suite_entry in "${SUITES[@]}"; do
    IFS='|' read -r suite_id suite_name results_dir summary_name <<< "$suite_entry"
    suite_count=$((suite_count + 1))
    suite_output_dir="$OUTPUT_DIR/reports/$suite_id"
    status="missing"
    total=0
    passed=0
    failed=0
    skipped=0
    canttell=0
    success_rate=0
    summary_rel=""
    report_rel=""

    if [[ -d "$results_dir" ]]; then
        mkdir -p "$suite_output_dir"
        cp -a "$results_dir"/. "$suite_output_dir"/

        summary_file=$(find_summary_file "$results_dir" "$summary_name")
        if [[ -n "$summary_file" && -f "$summary_file" ]]; then
            total=$(extract_summary_count "$summary_file" "Total Tests")
            passed=$(extract_summary_count "$summary_file" "Passed")
            failed=$(extract_summary_count "$summary_file" "Failed")
            skipped=$(extract_summary_count "$summary_file" "Skipped")
            canttell=$(extract_summary_count "$summary_file" "CantTell")
            success_rate=$(extract_summary_count "$summary_file" "Success Rate")
            summary_rel="reports/$suite_id/$(basename "$summary_file")"
        fi

        teamengine_index=$(find_teamengine_index "$suite_output_dir")
        if [[ -n "$teamengine_index" ]]; then
            report_rel=${teamengine_index#"$OUTPUT_DIR"/}
        fi

        if [[ "$total" -eq 0 ]]; then
            status="no-results"
        elif [[ "$failed" -gt 0 || "$skipped" -gt 0 || "$canttell" -gt 0 ]]; then
            status="failed"
        else
            status="passed"
        fi
    fi

    if [[ "$status" != "passed" ]]; then
        overall_status="failed"
    fi

    jq -n \
        --arg id "$suite_id" \
        --arg name "$suite_name" \
        --arg status "$status" \
        --arg resultsDirectory "$results_dir" \
        --arg summaryPath "$summary_rel" \
        --arg reportPath "$report_rel" \
        --argjson totalTests "$total" \
        --argjson passed "$passed" \
        --argjson failed "$failed" \
        --argjson skipped "$skipped" \
        --argjson cantTell "$canttell" \
        --argjson successRate "$success_rate" \
        '{
          id: $id,
          name: $name,
          status: $status,
          totalTests: $totalTests,
          passed: $passed,
          failed: $failed,
          skipped: $skipped,
          cantTell: $cantTell,
          successRate: $successRate,
          resultsDirectory: $resultsDirectory,
          summaryPath: $summaryPath,
          reportPath: $reportPath
        }' >> "$SUITE_JSONL"

    suite_name_html=$(html_escape "$suite_name")
    status_html=$(html_escape "$status")
    summary_link="Not available"
    report_link="Not available"
    if [[ -n "$summary_rel" ]]; then
        summary_link="<a href=\"$(html_escape "$summary_rel")\">summary</a>"
    fi
    if [[ -n "$report_rel" ]]; then
        report_link="<a href=\"$(html_escape "$report_rel")\">full TeamEngine report</a>"
    fi

    cat >> "$HTML_ROWS" <<EOF_ROW
      <tr class="$status_html">
        <th scope="row">$suite_name_html</th>
        <td><span class="status">$status_html</span></td>
        <td>$total</td>
        <td>$passed</td>
        <td>$failed</td>
        <td>$skipped</td>
        <td>$canttell</td>
        <td>$success_rate%</td>
        <td>$summary_link</td>
        <td>$report_link</td>
      </tr>
EOF_ROW

    printf '| %s | %s | %s | %s | %s | %s | %s | %s%% | %s | %s |\n' \
        "$suite_name" "$status" "$total" "$passed" "$failed" "$skipped" "$canttell" "$success_rate" \
        "${summary_rel:-n/a}" "${report_rel:-n/a}" >> "$MD_ROWS"
done

jq -s \
    --arg schemaVersion "1.0" \
    --arg generatedAt "$GENERATED_AT" \
    --arg repository "$REPOSITORY" \
    --arg gitSha "$GIT_SHA" \
    --arg gitRef "$GIT_REF" \
    --arg serverVersion "$SERVER_VERSION" \
    --arg runId "$RUN_ID" \
    --arg runNumber "$RUN_NUMBER" \
    --arg githubRunUrl "$GITHUB_RUN_URL" \
    --arg publicBaseUrl "$PUBLIC_BASE_URL" \
    '{
      schemaVersion: $schemaVersion,
      generatedAt: $generatedAt,
      repository: $repository,
      git: {
        sha: $gitSha,
        ref: $gitRef,
        serverVersion: $serverVersion
      },
      github: {
        runId: $runId,
        runNumber: $runNumber,
        runUrl: $githubRunUrl
      },
      publicBaseUrl: $publicBaseUrl,
      totals: {
        suites: length,
        totalTests: ([.[].totalTests] | add // 0),
        passed: ([.[].passed] | add // 0),
        failed: ([.[].failed] | add // 0),
        skipped: ([.[].skipped] | add // 0),
        cantTell: ([.[].cantTell] | add // 0),
        allPassed: all(.[]; .status == "passed" and .totalTests > 0 and .passed == .totalTests and .failed == 0 and .skipped == 0 and .cantTell == 0)
      },
      suites: .
    }' "$SUITE_JSONL" > "$OUTPUT_DIR/conformance-summary.json"

overall_status=$(jq -r 'if .totals.allPassed then "passed" else "failed" end' "$OUTPUT_DIR/conformance-summary.json")
write_badge "$overall_status" "$OUTPUT_DIR/badges/ogc-cite.svg"

total_tests=$(jq -r '.totals.totalTests' "$OUTPUT_DIR/conformance-summary.json")
total_passed=$(jq -r '.totals.passed' "$OUTPUT_DIR/conformance-summary.json")
total_failed=$(jq -r '.totals.failed' "$OUTPUT_DIR/conformance-summary.json")
total_skipped=$(jq -r '.totals.skipped' "$OUTPUT_DIR/conformance-summary.json")
total_canttell=$(jq -r '.totals.cantTell' "$OUTPUT_DIR/conformance-summary.json")
evidence_link="$GITHUB_RUN_URL"
evidence_link_label="GitHub Actions evidence run"
if [[ -n "$PUBLIC_BASE_URL" ]]; then
    evidence_link="${PUBLIC_BASE_URL}/"
    evidence_link_label="Full conformance evidence report"
elif [[ -z "$evidence_link" ]]; then
    evidence_link="index.html"
    evidence_link_label="Full conformance evidence report"
fi

cat > "$OUTPUT_DIR/conformance-summary.md" <<EOF_MD
# Honua OGC CITE Conformance Evidence

- Generated: $GENERATED_AT
- Repository: $REPOSITORY
- Git ref: $GIT_REF
- Git SHA: $GIT_SHA
- Server version: $SERVER_VERSION
- Status: $overall_status
$(
if [[ -n "$GITHUB_RUN_URL" ]]; then
    echo "- GitHub run: [$GITHUB_RUN_URL]($GITHUB_RUN_URL)"
fi
if [[ -n "$PUBLIC_BASE_URL" ]]; then
    echo "- Public evidence URL: [$PUBLIC_BASE_URL/]($PUBLIC_BASE_URL/)"
fi
)

## Totals

- Total tests: $total_tests
- Passed: $total_passed
- Failed: $total_failed
- Skipped: $total_skipped
- CantTell: $total_canttell

A passing evidence bundle requires every suite to report 100% passed tests:
failed, skipped, and CantTell counts must all be zero.

## Suites

| Suite | Status | Total | Passed | Failed | Skipped | CantTell | Success | Summary | Full report |
|---|---:|---:|---:|---:|---:|---:|---:|---|---|
$(cat "$MD_ROWS")

EOF_MD

cat > "$OUTPUT_DIR/website-conformance.md" <<EOF_WEBSITE
# OGC CITE Conformance Evidence

Honua Server's latest public CITE evidence bundle is available here:
[$evidence_link_label]($evidence_link).

Summary for this run:

- Status: **$overall_status**
- Generated: $GENERATED_AT
- Server version: \`$SERVER_VERSION\`
- Git SHA: \`$GIT_SHA\`
- Total tests: $total_tests
- Passed: $total_passed
- Failed: $total_failed
- Skipped: $total_skipped
- CantTell: $total_canttell

This evidence bundle is passing only when every listed CITE suite has 100%
passed tests with zero failed, skipped, or CantTell results.
EOF_WEBSITE

cat > "$OUTPUT_DIR/index.html" <<EOF_HTML
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Honua OGC CITE Conformance Evidence</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f8fafc;
      --panel: #ffffff;
      --text: #172033;
      --muted: #607089;
      --line: #d7deea;
      --pass: #0f766e;
      --fail: #b91c1c;
      --warn: #a16207;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      background: var(--bg);
      color: var(--text);
      line-height: 1.5;
    }
    main {
      max-width: 1180px;
      margin: 0 auto;
      padding: 32px 20px 48px;
    }
    h1 {
      margin: 0 0 8px;
      font-size: 30px;
      letter-spacing: 0;
    }
    p {
      margin: 0;
      color: var(--muted);
    }
    .summary {
      display: grid;
      grid-template-columns: repeat(5, minmax(120px, 1fr));
      gap: 12px;
      margin: 24px 0;
    }
    .metric {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 14px 16px;
    }
    .metric span {
      display: block;
      color: var(--muted);
      font-size: 13px;
    }
    .metric strong {
      display: block;
      margin-top: 2px;
      font-size: 26px;
    }
    .meta {
      margin: 0 0 20px;
      padding: 14px 16px;
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      color: var(--muted);
      font-size: 14px;
    }
    table {
      width: 100%;
      border-collapse: collapse;
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      overflow: hidden;
    }
    th, td {
      padding: 11px 12px;
      border-bottom: 1px solid var(--line);
      text-align: right;
      white-space: nowrap;
    }
    th:first-child, td:first-child {
      text-align: left;
      white-space: normal;
    }
    thead th {
      background: #eef3f8;
      color: #334155;
      font-size: 13px;
      font-weight: 700;
    }
    tbody tr:last-child th,
    tbody tr:last-child td {
      border-bottom: 0;
    }
    .status {
      display: inline-block;
      min-width: 74px;
      padding: 3px 8px;
      border-radius: 999px;
      text-align: center;
      font-size: 12px;
      font-weight: 700;
      text-transform: uppercase;
    }
    tr.passed .status {
      color: #ecfdf5;
      background: var(--pass);
    }
    tr.failed .status {
      color: #fff1f2;
      background: var(--fail);
    }
    tr.missing .status,
    tr.no-results .status {
      color: #fffbeb;
      background: var(--warn);
    }
    a {
      color: #0f5e9c;
      font-weight: 600;
      text-decoration-thickness: 1px;
      text-underline-offset: 2px;
    }
    @media (max-width: 860px) {
      main { padding: 24px 12px 36px; }
      .summary { grid-template-columns: repeat(2, minmax(120px, 1fr)); }
      .table-wrap { overflow-x: auto; }
    }
  </style>
</head>
<body>
<main>
  <h1>Honua OGC CITE Conformance Evidence</h1>
  <p>Full TeamEngine result reports for the Honua Server conformance run.</p>

  <section class="summary" aria-label="Conformance totals">
    <div class="metric"><span>Status</span><strong>$(html_escape "$overall_status")</strong></div>
    <div class="metric"><span>Total</span><strong>$total_tests</strong></div>
    <div class="metric"><span>Passed</span><strong>$total_passed</strong></div>
    <div class="metric"><span>Failed</span><strong>$total_failed</strong></div>
    <div class="metric"><span>Skipped</span><strong>$total_skipped</strong></div>
  </section>

  <section class="meta" aria-label="Run metadata">
    Generated $GENERATED_AT from <strong>$(html_escape "$REPOSITORY")</strong>
    at <code>$(html_escape "$GIT_REF")</code> / <code>$(html_escape "$GIT_SHA")</code>.
    Machine-readable summary: <a href="conformance-summary.json">conformance-summary.json</a>.
    Website markdown: <a href="website-conformance.md">website-conformance.md</a>.
    Badge: <a href="badges/ogc-cite.svg">badges/ogc-cite.svg</a>.
$(
if [[ -n "$GITHUB_RUN_URL" ]]; then
    echo "    GitHub evidence run: <a href=\"$(html_escape "$GITHUB_RUN_URL")\">$(html_escape "$GITHUB_RUN_URL")</a>."
fi
)
  </section>

  <div class="table-wrap">
    <table>
      <thead>
        <tr>
          <th scope="col">Suite</th>
          <th scope="col">Status</th>
          <th scope="col">Total</th>
          <th scope="col">Passed</th>
          <th scope="col">Failed</th>
          <th scope="col">Skipped</th>
          <th scope="col">CantTell</th>
          <th scope="col">Success</th>
          <th scope="col">Summary</th>
          <th scope="col">Full report</th>
        </tr>
      </thead>
      <tbody>
$(cat "$HTML_ROWS")
      </tbody>
    </table>
  </div>
</main>
</body>
</html>
EOF_HTML

echo "CITE evidence bundle written to $OUTPUT_DIR"
echo "Summary: $OUTPUT_DIR/index.html"
echo "JSON: $OUTPUT_DIR/conformance-summary.json"

if [[ "$STRICT" == "true" && "$overall_status" != "passed" ]]; then
    echo "One or more CITE suites are missing, failed, or produced no executable tests." >&2
    exit 1
fi
