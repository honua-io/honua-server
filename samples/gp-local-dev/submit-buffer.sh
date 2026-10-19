#!/usr/bin/env bash
# Run the installed-SDK local GP quickstart (see submit-buffer.py).
# Prerequisite: python -m pip install honua-sdk
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "${PYTHON:-python3}" "$HERE/submit-buffer.py"
