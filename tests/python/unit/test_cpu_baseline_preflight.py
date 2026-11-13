"""Exercise the x86-64-v2 CPU baseline preflight against fixture cpuinfo files."""
import os
from pathlib import Path
import subprocess

import pytest

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts/operations/check-cpu-baseline.sh"

# A modern host and the generic QEMU model (qemu64 hides SSSE3, SSE4.1, SSE4.2 and POPCNT).
V2_FLAGS = "fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ht syscall nx lm pni ssse3 cx16 sse4_1 sse4_2 popcnt lahf_lm avx avx2"
QEMU64_FLAGS = "fpu de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 syscall nx lm pni cx16 hypervisor lahf_lm"


def run(tmp_path, flags, mode=None, arch="x86_64"):
    cpuinfo = tmp_path / "cpuinfo"
    if flags is not None:
        cpuinfo.write_text(
            "processor\t: 0\nmodel name\t: test\nflags\t\t: " + flags + "\n\n"
            "processor\t: 1\nflags\t\t: " + flags + "\n")
    env = {**os.environ, "HONUA_CPUINFO_PATH": str(cpuinfo), "HONUA_CPU_ARCH": arch}
    env.pop("HONUA_CPU_PREFLIGHT", None)
    if mode is not None:
        env["HONUA_CPU_PREFLIGHT"] = mode
    return subprocess.run(["sh", str(SCRIPT)], env=env, capture_output=True, text=True, check=False)


@pytest.mark.parametrize("mode", [None, "warn", "enforce"])
def test_v2_cpu_passes_silently(tmp_path, mode):
    result = run(tmp_path, V2_FLAGS, mode)
    assert (result.returncode, result.stderr) == (0, "")


def test_generic_vm_model_warns_by_default_and_names_missing_features(tmp_path):
    result = run(tmp_path, QEMU64_FLAGS)
    assert result.returncode == 0
    assert "WARNING" in result.stderr
    assert "missing: popcnt ssse3 sse4_1 sse4_2)" in result.stderr
    assert "host-passthrough" in result.stderr and "2027.1" in result.stderr


def test_generic_vm_model_fails_when_enforced(tmp_path):
    result = run(tmp_path, QEMU64_FLAGS, "enforce")
    assert result.returncode == 3
    assert "ERROR" in result.stderr and "will not start" in result.stderr


@pytest.mark.parametrize("missing", ["cx16", "lahf_lm", "popcnt", "pni", "ssse3", "sse4_1", "sse4_2"])
def test_each_v2_feature_is_required(tmp_path, missing):
    flags = " ".join(flag for flag in V2_FLAGS.split() if flag != missing)
    result = run(tmp_path, flags, "enforce")
    assert result.returncode == 3
    assert f"(missing: {missing})" in result.stderr


def test_feature_names_match_whole_words_only(tmp_path):
    # "sse4_2x" or "popcntx" must not satisfy sse4_2 / popcnt.
    flags = V2_FLAGS.replace("sse4_2", "sse4_2x").replace("popcnt", "popcntx")
    result = run(tmp_path, flags, "enforce")
    assert result.returncode == 3
    assert "(missing: popcnt sse4_2)" in result.stderr


@pytest.mark.parametrize("arch", ["aarch64", "arm64"])
def test_non_x64_is_not_applicable(tmp_path, arch):
    assert run(tmp_path, QEMU64_FLAGS, "enforce", arch=arch).returncode == 0


def test_off_skips_the_check(tmp_path):
    result = run(tmp_path, QEMU64_FLAGS, "off")
    assert (result.returncode, result.stderr) == (0, "")


def test_unreadable_cpuinfo_skips_with_a_note(tmp_path):
    result = run(tmp_path, None, "enforce")
    assert result.returncode == 0
    assert "cannot read CPU flags" in result.stderr


def test_unknown_mode_is_rejected(tmp_path):
    result = run(tmp_path, V2_FLAGS, "strict")
    assert result.returncode == 2
    assert "warn, enforce or off" in result.stderr
