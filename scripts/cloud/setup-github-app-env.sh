#!/usr/bin/env bash
set -euo pipefail

APP_ID="2932471"
INSTALLATION_ID="111991908"
DEFAULT_PEM_SOURCE="/mnt/c/Users/mike/Downloads/wsl-cli.2026-02-23.private-key.pem"

PEM_SOURCE="${1:-$DEFAULT_PEM_SOURCE}"
PEM_DEST_DIR="$HOME/.config/github-app"
PEM_DEST_PATH="$PEM_DEST_DIR/private-key.pem"
PROFILE_FILE="$HOME/.profile"
MARKER_START="# >>> honua github app env >>>"
MARKER_END="# <<< honua github app env <<<"

if [[ ! -r "$PEM_SOURCE" ]]; then
    echo "Error: PEM file is not readable: $PEM_SOURCE" >&2
    echo "Pass an explicit path as the first argument if needed." >&2
    exit 1
fi

mkdir -p "$PEM_DEST_DIR"
cp "$PEM_SOURCE" "$PEM_DEST_PATH"
chmod 600 "$PEM_DEST_PATH"

tmp_file="$(mktemp)"
if [[ -f "$PROFILE_FILE" ]]; then
    awk -v start="$MARKER_START" -v end="$MARKER_END" '
        BEGIN { skipping = 0 }
        $0 == start { skipping = 1; next }
        $0 == end { skipping = 0; next }
        !skipping { print }
    ' "$PROFILE_FILE" > "$tmp_file"
else
    : > "$tmp_file"
fi

{
    echo
    echo "$MARKER_START"
    echo "export GITHUB_APP_ID=\"$APP_ID\""
    echo "export GITHUB_APP_INSTALLATION_ID=\"$INSTALLATION_ID\""
    echo "export GITHUB_APP_PEM_PATH=\"$PEM_DEST_PATH\""
    echo "$MARKER_END"
} >> "$tmp_file"

mv "$tmp_file" "$PROFILE_FILE"

# Validate by loading profile in a subshell.
bash -lc 'source ~/.profile >/dev/null 2>&1; test -n "${GITHUB_APP_ID:-}" && test -n "${GITHUB_APP_INSTALLATION_ID:-}" && test -n "${GITHUB_APP_PEM_PATH:-}" && test -r "${GITHUB_APP_PEM_PATH:-}"'

echo "Configured GitHub App environment variables."
echo "App ID: $APP_ID"
echo "Installation ID: $INSTALLATION_ID"
echo "PEM path: $PEM_DEST_PATH"
echo
echo "Apply to current shell:"
echo "  source \"$PROFILE_FILE\""
echo
echo "Quick check:"
echo "  echo \"\${GITHUB_APP_ID:+set} \${GITHUB_APP_INSTALLATION_ID:+set} \${GITHUB_APP_PEM_PATH:+set}\""
echo "  test -r \"\$GITHUB_APP_PEM_PATH\" && echo \"pem readable\""
