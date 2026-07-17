# shellcheck shell=bash
# CR-safe jq wrapper for scripts/ci consumers.
#
# The standard `jq` for Windows is a native PE binary that writes stdout in
# TEXT mode, so it translates every LF it emits into CRLF. Shell code that
# captures `jq` output into a variable and then compares it, tests it for
# emptiness, uses it as a path, or iterates it (`[[ -f "$x" ]]`,
# `[[ "$a" == "$b" ]]`, `${x:-fallback}`, `while read`) then works with a
# trailing carriage return it never expected — the file "does not exist",
# `""` looks non-empty so a `:-` fallback never fires, and equality fails.
#
# This defines a `jq` shell function that shadows the binary for every call
# site in the sourcing script (and only that script — it is deliberately NOT
# exported, so child processes get the real binary and must source this file
# themselves). It strips the text-mode CR at line ends, converting the CRLF
# artifact back to LF. It is the exact inverse of the text-mode translation:
#   - It removes ONLY a carriage return immediately before a line ending, so a
#     legitimate CR embedded inside a JSON string value (mid-line) is
#     preserved — `sed 's/\r$//'` anchors on `$`.
#   - It is a NO-OP on Linux, where jq already emits LF-only output: there is
#     no `\r$` to match, so every line passes through unchanged. CI runs Linux
#     and is therefore unaffected.
#   - `${PIPESTATUS[0]}` propagates jq's own exit status (not sed's), so
#     `jq -e` truthiness checks and parse-error codes keep working regardless
#     of whether the caller set `pipefail`.
jq() {
  command jq "$@" | sed 's/\r$//'
  return "${PIPESTATUS[0]}"
}
