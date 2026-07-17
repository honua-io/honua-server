# shellcheck shell=bash
# Resolve a Python 3 interpreter that actually runs.
#
# `command -v python3` alone is not enough on Windows: the Microsoft Store ships
# a python3.exe App Execution Alias stub that is present on PATH but exits
# non-zero with "Python was not found", while the real interpreter installs as
# `python`/`py`. A plain `command -v python3` guard therefore passes, the stub
# then fails, and the caller blames its own logic instead of a missing
# interpreter (#2871/#2880 for pre-pr-check.sh itself; #2886 for the child
# validators it invokes). Probe each candidate by *executing* it, and require
# Python 3 so a legacy `python` == python2 is not selected. On Linux/CI
# `python3` matches first, exactly as before — a no-op there.
#
# This mirrors the probe #2880 added to scripts/ci/pre-pr-check.sh so every
# scripts/ci validator resolves Python the same way instead of re-implementing
# (or forgetting) the probe per script.
#
# Usage (source, then resolve or skip):
#
#   # shellcheck source=scripts/ci/lib/python-resolve.sh
#   . "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/python-resolve.sh"
#   if ! PYTHON_BIN="$(honua_resolve_python)"; then
#     echo "⚠️  Skipping <thing> (no working Python 3: tried python3/python/py)"
#     exit 0
#   fi
#   "${PYTHON_BIN}" some-script.py
#
# honua_resolve_python prints the resolved interpreter name on stdout and
# returns 0, or prints nothing and returns 1 when none of the candidates run.
honua_resolve_python() {
  local candidate
  for candidate in python3 python py; do
    if command -v "${candidate}" >/dev/null 2>&1 \
      && "${candidate}" -c 'import sys; sys.exit(0 if sys.version_info[0] == 3 else 1)' >/dev/null 2>&1; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done
  return 1
}
