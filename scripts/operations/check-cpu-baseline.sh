#!/bin/sh
# Honua Server CPU baseline preflight (honua-server#7382).
#
# Honua Server releases built on .NET 11 (2027.1 and later) require an x86-64-v2 CPU on x64
# (CMPXCHG16B, LAHF/SAHF, POPCNT, SSE3, SSSE3, SSE4.1, SSE4.2) and refuse to start without it.
# Any server CPU since about 2009 qualifies, but virtual machines that use a generic CPU model
# (QEMU/KVM "qemu64" or "kvm64", some Proxmox defaults) hide these flags from the guest.
#
# Run it before installing or upgrading, on the host or inside the Honua image the host will run
# (the CPU a container sees is the one the server will see):
#   sh scripts/operations/check-cpu-baseline.sh
#   docker run --rm -i --entrypoint sh <image> -s < scripts/operations/check-cpu-baseline.sh
#   kubectl run honua-cpu-check --rm -i --restart=Never --image=<image> --command -- sh -s < scripts/operations/check-cpu-baseline.sh
#
# HONUA_CPU_PREFLIGHT selects the mode:
#   warn    (default) print what is missing and exit 0; .NET 10 releases run on any x64 CPU.
#   enforce exit 3 when a required feature is missing.
#   off     skip the check.
# Exit codes: 0 baseline met, not applicable, or warn; 2 bad mode; 3 enforced and missing.
# HONUA_CPUINFO_PATH and HONUA_CPU_ARCH override /proc/cpuinfo and `uname -m` (tests only).
set -eu

mode="${HONUA_CPU_PREFLIGHT:-warn}"
case "$mode" in
  warn|enforce) ;;
  off) exit 0 ;;
  *) echo "honua-cpu-preflight: HONUA_CPU_PREFLIGHT must be warn, enforce or off (got '$mode')" >&2; exit 2 ;;
esac

arch="${HONUA_CPU_ARCH:-$(uname -m)}"
case "$arch" in
  x86_64|amd64) ;;
  *) exit 0 ;;  # The x86-64-v2 floor applies to x64 only.
esac

cpuinfo="${HONUA_CPUINFO_PATH:-/proc/cpuinfo}"
# The first "flags" line describes the CPU model the hypervisor presents; every vCPU shares it.
flags="$(sed -n 's/^flags[[:space:]]*:[[:space:]]*//p' "$cpuinfo" 2>/dev/null | head -n 1)" || flags=""
if [ -z "$flags" ]; then
  echo "honua-cpu-preflight: cannot read CPU flags from $cpuinfo; skipping the x86-64-v2 check" >&2
  exit 0
fi

# Linux names for the x86-64-v2 features: pni is SSE3, lahf_lm is LAHF/SAHF in 64-bit mode.
missing=""
for feature in cx16 lahf_lm popcnt pni ssse3 sse4_1 sse4_2; do
  case " $flags " in
    *" $feature "*) ;;
    *) missing="${missing:+$missing }$feature" ;;
  esac
done

if [ -z "$missing" ]; then
  exit 0
fi

if [ "$mode" = enforce ]; then
  level=ERROR
  consequence="This Honua Server release requires x86-64-v2 and will not start on this CPU."
else
  level=WARNING
  consequence="This release still runs, but Honua Server 2027.1 and later (.NET 11) will not start on this CPU."
fi

cat >&2 <<EOF
honua-cpu-preflight: $level: the CPU does not provide x86-64-v2 (missing: $missing).
$consequence
On a virtual machine, present the host CPU or an x86-64-v2 model to the guest:
  - libvirt/KVM: <cpu mode='host-passthrough'/> (or a named model such as Nehalem or newer)
  - Proxmox VE: CPU type 'host' or 'x86-64-v2-AES'
  - QEMU: -cpu host (or -cpu Nehalem or newer), not qemu64/kvm64
On physical hardware, the CPU predates x86-64-v2 (about 2009) and must be replaced.
Guide: docs/guides/deploy/troubleshooting.md#cpu-baseline-x86-64-v2
EOF

[ "$mode" = enforce ] && exit 3
exit 0
