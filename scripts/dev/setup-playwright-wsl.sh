#!/usr/bin/env bash
set -euo pipefail

if [[ ! -f /etc/os-release ]]; then
  echo "Unsupported environment: /etc/os-release not found." >&2
  exit 1
fi

# shellcheck source=/dev/null
source /etc/os-release

if [[ "${ID:-}" != "ubuntu" ]]; then
  echo "This script currently supports Ubuntu only. Detected: ${ID:-unknown}" >&2
  exit 1
fi

if [[ -z "${VERSION_ID:-}" ]]; then
  echo "Unable to determine Ubuntu VERSION_ID." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
playwright_csproj="$repo_root/tests/Honua.Admin.Playwright/Honua.Admin.Playwright.csproj"
playwright_script="$repo_root/tests/Honua.Admin.Playwright/bin/Debug/net10.0/playwright.ps1"
ms_repo_deb="/tmp/packages-microsoft-prod.deb"

echo "Installing prerequisites..."
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y \
  wget gpg apt-transport-https software-properties-common ca-certificates

if [[ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]]; then
  echo "Adding Microsoft package repository for Ubuntu ${VERSION_ID}..."
  wget -q "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -O "$ms_repo_deb"
  sudo dpkg -i "$ms_repo_deb"
  rm -f "$ms_repo_deb"
fi

echo "Installing PowerShell..."
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y powershell

if ! command -v pwsh >/dev/null 2>&1; then
  echo "PowerShell installation failed: 'pwsh' not found in PATH." >&2
  exit 1
fi

echo "Building Playwright test project..."
dotnet build "$playwright_csproj"

if [[ ! -f "$playwright_script" ]]; then
  echo "Playwright setup script not found at: $playwright_script" >&2
  exit 1
fi

echo "Installing Playwright Linux dependencies..."
pwsh "$playwright_script" install-deps

echo "Installing Playwright Chromium browser..."
pwsh "$playwright_script" install chromium

echo
echo "Playwright setup complete."
echo "Run tests with:"
echo "dotnet test $playwright_csproj --no-build --verbosity minimal"
