#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: sudo ./scripts/host/reclaim-wsl-space.sh [options]

Options:
  --no-docker   Skip Docker cleanup.
  --zero-fill   Write/remove a temporary zero file to improve VHDX compaction.
  -h, --help    Show this help.

Examples:
  sudo ./scripts/host/reclaim-wsl-space.sh
  sudo ./scripts/host/reclaim-wsl-space.sh --zero-fill
EOF
}

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run with sudo: sudo $0 [options]" >&2
  exit 1
fi

DO_DOCKER=true
ZERO_FILL=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-docker)
      DO_DOCKER=false
      shift
      ;;
    --zero-fill)
      ZERO_FILL=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 1
      ;;
  esac
done

echo "== Before =="
df -h /
du -sh /var/cache/apt 2>/dev/null || true
if command -v docker >/dev/null 2>&1; then
  docker system df || true
fi

echo
echo "== APT cleanup =="
if command -v apt-get >/dev/null 2>&1; then
  apt-get autoremove --purge -y
  apt-get clean
else
  echo "apt-get not found, skipping."
fi

echo
echo "== Journal cleanup =="
journalctl --vacuum-time=7d || true

if [[ "${DO_DOCKER}" == "true" ]]; then
  echo
  echo "== Docker cleanup =="
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    docker system prune -a --volumes -f
  else
    echo "Docker not available/accessible, skipping."
  fi
fi

echo
echo "== Trim filesystem =="
fstrim -av || true

if [[ "${ZERO_FILL}" == "true" ]]; then
  echo
  echo "== Zero-fill pass (optional compaction helper) =="
  echo "Writing /zero.fill until disk is full, then deleting it..."
  dd if=/dev/zero of=/zero.fill bs=1M status=progress || true
  sync
  rm -f /zero.fill
  sync
fi

echo
echo "== After =="
df -h /
du -sh /var/cache/apt 2>/dev/null || true
if command -v docker >/dev/null 2>&1; then
  docker system df || true
fi

cat <<'EOF'

Linux cleanup complete.
Next (Windows side) to shrink ext4.vhdx:
1) wsl --shutdown
2) Compact ext4.vhdx in elevated PowerShell with diskpart:
   diskpart
   select vdisk file="C:\Users\<you>\AppData\Local\Packages\<YourDistro>\LocalState\ext4.vhdx"
   attach vdisk readonly
   compact vdisk
   detach vdisk
   exit
EOF
