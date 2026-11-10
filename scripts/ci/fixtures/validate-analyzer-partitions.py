#!/usr/bin/env python3
"""Exercise analyzer partition ownership with the installed .NET SDK."""

from pathlib import Path
import subprocess
import tempfile


TARGETS = Path(__file__).resolve().parents[3] / "Directory.Build.targets"


def build(project: Path, partition: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["dotnet", "build", str(project), "--no-restore", "--no-incremental",
         "--configuration", "Release", "--verbosity", "quiet",
         f"-p:HonuaAnalyzerPartition={partition}", "-p:TreatWarningsAsErrors=true",
         "-p:UseSharedCompilation=false"],
        capture_output=True, text=True, check=False,
    )


with tempfile.TemporaryDirectory(prefix="honua-analyzer-partitions-") as temporary:
    root = Path(temporary)
    (root / ".editorconfig").write_text(
        "root = true\n[*.cs]\ndotnet_diagnostic.CA1825.severity = warning\n",
        encoding="utf-8",
    )
    for is_test in (False, True):
        project_dir = root / ("tests" if is_test else "source")
        project_dir.mkdir()
        project = project_dir / "Probe.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<TargetFramework>net10.0</TargetFramework>'
            f'<IsTestProject>{str(is_test).lower()}</IsTestProject>'
            '<EnableNETAnalyzers>true</EnableNETAnalyzers>'
            '</PropertyGroup>'
            f'<Import Project="{TARGETS}" /></Project>', encoding="utf-8",
        )
        source = project_dir / "Probe.cs"
        source.write_text("public class Probe { public static int[] Values() => new int[0]; }\n", encoding="utf-8")
        subprocess.run(["dotnet", "restore", str(project), "--verbosity", "quiet"], check=True)
        owner = "tests" if is_test else "source"
        other = "source" if is_test else "tests"
        for partition in (owner, "all", other):
            result = build(project, partition)
            expected_failure = partition != other
            if (result.returncode != 0) != expected_failure:
                raise AssertionError(f"{owner}/{partition}: unexpected result\n{result.stdout}{result.stderr}")
            if expected_failure and "error CA1825" not in result.stdout:
                raise AssertionError(f"{owner}/{partition}: missing analyzer diagnostic\n{result.stdout}")
        invalid = build(project, "typo")
        if invalid.returncode == 0 or "Unknown HonuaAnalyzerPartition" not in invalid.stdout:
            raise AssertionError("Invalid partition must fail closed")
        source.write_text("public class Probe { syntax error }\n", encoding="utf-8")
        for partition in (owner, other):
            result = build(project, partition)
            if result.returncode == 0 or "error CS" not in result.stdout:
                raise AssertionError(f"{partition}: compiler errors must still fail the build")
print("Analyzer partitions preserve complete diagnostic ownership and compiler failures.")
