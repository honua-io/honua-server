#!/usr/bin/env python3
"""Exercise analyzer partition ownership with the installed .NET SDK."""

from pathlib import Path
import importlib.util
import subprocess
import tempfile


TARGETS = Path(__file__).resolve().parents[3] / "Directory.Build.targets"
spec = importlib.util.spec_from_file_location("analyzer_source_filter", TARGETS.parent / "scripts/ci/analyzer-source-filter.py")
source_filter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(source_filter)


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
    projects = []
    for is_test in (False, True):
        project_dir = root / ("tests" if is_test else "source")
        project_dir.mkdir()
        project = project_dir / "Probe.csproj"
        projects.append(project.relative_to(root).as_posix())
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
    if source_filter.source_roots(root, projects) != [projects[0]]:
        raise AssertionError("Source roots must exclude evaluated test projects")
    # The property comes from an import, not the project name or inline XML.
    (root / "ownership.props").write_text(
        '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>',
        encoding="utf-8")
    imported = root / "Imported.csproj"
    imported.write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
        '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>'
        f'<Import Project="{TARGETS}" /><Import Project="ownership.props" />'
        '<ItemGroup><Compile Include="Imported.cs" /></ItemGroup></Project>', encoding="utf-8")
    if source_filter.source_roots(root, [imported.name]):
        raise AssertionError("Imported IsTestProject must control source ownership")
    (root / "Imported.cs").write_text("public class Imported { public static int[] Values() => new int[0]; }\n")
    subprocess.run(["dotnet", "restore", str(imported), "--verbosity", "quiet"], check=True)
    for partition in ("source", "tests"):
        result = build(imported, partition)
        if (result.returncode != 0) != (partition == "tests"):
            raise AssertionError(f"Late imported ownership/{partition}: {result.stdout}{result.stderr}")
        if partition == "tests" and "error CA1825" not in result.stdout:
            raise AssertionError("Late imported test project must retain analyzer coverage")
    imported.write_text(imported.read_text().replace(
        '<TargetFramework>net10.0</TargetFramework>', '<TargetFrameworks>net10.0</TargetFrameworks>'))
    if source_filter.source_roots(root, [imported.name]) != [imported.name]:
        raise AssertionError("Multi-targeted projects must conservatively retain source compilation")
print("Analyzer partitions preserve complete diagnostic ownership and compiler failures.")
