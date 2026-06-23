#!/usr/bin/env bash
# Surgical retry — extract the EXACT failed test FQNs from a CI run and re-run
# ONLY those, never a full shard rerun. Used for (a) post-AI-fix verification and
# (b) a targeted retry when specific failing tests are known.
#
# Why surgical: `gh run rerun --failed` re-runs whole shards (tens of minutes,
# tens of thousands of tests). When the exact failing tests are known, re-running
# just them (`dotnet test <proj> --filter "FullyQualifiedName=A|FullyQualifiedName=B"`)
# verifies a fix in seconds and proves the fix without re-paying the full matrix.
# The train NEVER `gh run rerun --failed`s a shard when specific FQNs are known.

# train_failed_test_names <run-id>: parse the failing-test FQNs from a CI run's
# test results / logs, one FQN per line (sorted-unique). For .NET, xUnit/VSTest
# failure lines read like:
#     Failed Honua.Core.Tests.Query.FilterTests.Parses_Nested   [12 ms]
#     [xUnit.net 00:00:00.42]   Honua.Server.Tests.Stac.ItemTests.Returns200 [FAIL]
# We extract the dotted FQN token after "Failed " or before " [FAIL]".
#
# Live path scrapes `gh run view --log-failed`. Test override:
# TRAIN_RUN_LOG_FOR <cmd> is invoked with the run id and must print the raw log
# text (offline fixtures). TRAIN_FAILED_TESTS_FOR_RUN <cmd> can short-circuit and
# emit the FQNs directly (used by the trunk-pre-existing-tests path).
train_failed_test_names() {
  local run_id="$1"
  if [[ -n "${TRAIN_FAILED_TESTS_FOR_RUN:-}" ]]; then
    "${TRAIN_FAILED_TESTS_FOR_RUN}" "${run_id}" | sed '/^$/d' | sort -u
    return 0
  fi
  local log
  if [[ -n "${TRAIN_RUN_LOG_FOR:-}" ]]; then
    log="$("${TRAIN_RUN_LOG_FOR}" "${run_id}")"
  elif [[ -n "${TRAIN_RUN_LOG_TEXT:-}" ]]; then
    log="${TRAIN_RUN_LOG_TEXT}"
  else
    [[ -z "${run_id}" ]] && return 0
    log="$(gh run view "${run_id}" --log-failed 2>/dev/null || echo "")"
  fi
  train_parse_failed_test_names "${log}"
}

# train_parse_failed_test_names <log-text>: pure parser (no I/O) — emit the
# failing-test FQNs found in VSTest/xUnit/dotnet-test failure lines. Recognizes:
#   * "Failed <FQN>"            (VSTest/dotnet-test console reporter)
#   * "<FQN> [FAIL]"           (xUnit.net console reporter)
#   * "Failed!  - ... <FQN>"   (defensively, an FQN token after "Failed")
# An FQN is a dotted identifier path (>=1 dot), optionally with a method and
# arguments stripped (we keep up to the method name, dropping "(args)").
train_parse_failed_test_names() {
  local text="$1"
  printf '%s\n' "${text}" | awk '
    {
      line = $0
      fqn = ""
      # Pattern A: "... Failed <FQN> ..."  (VSTest reporter)
      if (match(line, /Failed[[:space:]]+[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z0-9_]+/)) {
        tok = substr(line, RSTART, RLENGTH)
        sub(/^Failed[[:space:]]+/, "", tok)
        fqn = tok
      }
      # Pattern B: "<FQN> [FAIL]"  (xUnit reporter) — overrides A if present.
      if (match(line, /[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z0-9_]+[[:space:]]+\[FAIL\]/)) {
        tok = substr(line, RSTART, RLENGTH)
        sub(/[[:space:]]+\[FAIL\].*$/, "", tok)
        fqn = tok
      }
      if (fqn != "") {
        # Drop any trailing "(args)" parametrized-test suffix and method parens.
        sub(/\(.*$/, "", fqn)
        # Must contain at least one dot to be a real FQN.
        if (index(fqn, ".") > 0) print fqn
      }
    }
  ' | sed '/^$/d' | sort -u
}

# train_build_test_filter <fqn-list-newline>: build the dotnet-test --filter
# expression that selects exactly those FQNs:
#   "FullyQualifiedName=A|FullyQualifiedName=B"
# Empty FQN list => empty string (caller must guard). Pure + testable.
train_build_test_filter() {
  local fqns="$1"
  printf '%s\n' "${fqns}" | sed '/^$/d' | sort -u | awk '
    { if (NR>1) printf "|"; printf "FullyQualifiedName=%s", $0 }
    END { if (NR>0) printf "\n" }
  '
}

# train_build_js_test_pattern <fqn-list-newline>: build a Jest/Vitest -t pattern
# (alternation of the leaf test names) for the JS equivalent of a surgical rerun:
#   "TestA|TestB"  (used as `jest -t "<pattern>"` / `vitest -t "<pattern>"`).
# We use the trailing identifier of each FQN as the JS test name token.
train_build_js_test_pattern() {
  local fqns="$1"
  printf '%s\n' "${fqns}" | sed '/^$/d' | sort -u | awk '
    {
      n = split($0, p, ".")
      leaf = p[n]
      if (NR>1) printf "|"; printf "%s", leaf
    }
    END { if (NR>0) printf "\n" }
  '
}

# train_build_py_test_pattern <fqn-list-newline>: build a pytest -k expression
# (alternation) for the Python equivalent: "TestA or TestB".
train_build_py_test_pattern() {
  local fqns="$1"
  printf '%s\n' "${fqns}" | sed '/^$/d' | sort -u | awk '
    {
      n = split($0, p, ".")
      leaf = p[n]
      if (NR>1) printf " or "; printf "%s", leaf
    }
    END { if (NR>0) printf "\n" }
  '
}

# train_surgical_test_projects <fqn-list-newline>: map failing FQNs back to the
# test PROJECT(s) that own them, so the surgical rerun targets the right csproj.
# Heuristic: the FQN's leading namespace segments name the test assembly, which
# (by repo convention) maps to tests/dotnet/<Assembly>/<Assembly>.csproj. We take
# the longest dotted prefix that matches a real *.csproj under tests/dotnet/.
# Test override: TRAIN_TEST_PROJECT_FOR <cmd> is invoked with an FQN and prints
# the project path (offline fixtures). Emits unique project paths.
train_surgical_test_projects() {
  local fqns="$1"
  local fqn
  while IFS= read -r fqn; do
    [[ -z "${fqn}" ]] && continue
    if [[ -n "${TRAIN_TEST_PROJECT_FOR:-}" ]]; then
      "${TRAIN_TEST_PROJECT_FOR}" "${fqn}"
      continue
    fi
    # Try progressively shorter dotted prefixes as the assembly/project name.
    local prefix="${fqn}" proj=""
    while [[ "${prefix}" == *.* ]]; do
      prefix="${prefix%.*}"
      local cand="${TRAIN_REPO_ROOT}/tests/dotnet/${prefix}/${prefix}.csproj"
      if [[ -f "${cand}" ]]; then proj="${cand}"; break; fi
    done
    [[ -n "${proj}" ]] && printf '%s\n' "${proj}"
  done <<<"$(printf '%s\n' "${fqns}" | sed '/^$/d' | sort -u)" | sort -u
}

# train_surgical_rerun <run-id> <fqn-list-newline>: re-run ONLY the given failing
# tests via `dotnet test <proj> --filter "<filter>"`, per owning project. Returns
# 0 if all targeted reruns pass, 1 if any fails, 2 if no FQNs / no project found
# (caller must fall back). Side-effecting in the sense it runs tests, but it does
# NOT push/merge/comment — it is a LOCAL verification under the build lock.
#
# Test override: TRAIN_SURGICAL_RUNNER <cmd> is invoked as
#   "${TRAIN_SURGICAL_RUNNER}" <project> <filter>
# and its exit code is used verbatim (offline fixtures never call dotnet).
train_surgical_rerun() {
  local run_id="$1" fqns="$2"
  fqns="$(printf '%s\n' "${fqns}" | sed '/^$/d' | sort -u)"
  if [[ -z "${fqns}" ]]; then
    train_warn "surgical rerun: no FQNs supplied; nothing to re-run"
    return 2
  fi
  local filter; filter="$(train_build_test_filter "${fqns}")"
  if [[ -z "${filter}" ]]; then
    train_warn "surgical rerun: empty filter; nothing to re-run"
    return 2
  fi
  local projects; projects="$(train_surgical_test_projects "${fqns}")"
  if [[ -z "${projects}" ]]; then
    train_warn "surgical rerun: could not map FQNs to a test project; caller should fall back"
    return 2
  fi

  local proj rc=0
  while IFS= read -r proj; do
    [[ -z "${proj}" ]] && continue
    train_log "surgical rerun: dotnet test ${proj} --filter \"${filter}\""
    if [[ -n "${TRAIN_SURGICAL_RUNNER:-}" ]]; then
      "${TRAIN_SURGICAL_RUNNER}" "${proj}" "${filter}" || rc=1
    else
      ( cd "${TRAIN_REPO_ROOT}" && with-build-lock dotnet test "${proj}" --filter "${filter}" ) || rc=1
    fi
  done <<<"${projects}"
  return "${rc}"
}
