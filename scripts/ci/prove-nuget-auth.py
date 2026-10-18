#!/usr/bin/env python3
"""Cold-restore the governed private SDK only for authentication changes or explicit runs."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
KEY = "NuGetPackageSourceCredentials_github-honua"
FEED = "https://nuget.pkg.github.com/honua-io/index.json"
PATHS = (".github/actions/setup-dotnet-ci/", "scripts/ci/prove-nuget-auth.py",
         "scripts/ci/test-nuget-auth.py", "NuGet.Config", "NuGet.config")
PACKAGE = "Honua.Sdk.Admin"


class ProofError(Exception):
    pass


def selected(event: str, paths: list[str] | None) -> bool:
    # Dispatch is an explicit proof run. Missing/unreadable PR history fails
    # toward executing the proof, never silently declaring authentication valid.
    return event != "pull_request" or paths is None or any(
        path == prefix or (prefix.endswith("/") and path.startswith(prefix))
        for path in paths for prefix in PATHS)


def changed_paths() -> list[str] | None:
    try:
        subprocess.run(["git", "rev-parse", "--verify", "HEAD^2^{commit}"], cwd=ROOT,
                       capture_output=True, check=True)
        return subprocess.check_output(["git", "diff", "--no-renames", "--name-only", "HEAD^1", "HEAD"],
                                       cwd=ROOT, text=True).splitlines()
    except subprocess.CalledProcessError:
        return None


def clean() -> bool:
    return not subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"],
                                       cwd=ROOT, text=True).strip()


def restore(project: Path, config: Path, cache: Path, credential: str | None):
    environment = dict(os.environ)
    environment.pop(KEY, None)
    if credential is not None:
        environment[KEY] = credential
    environment.update(NUGET_PACKAGES=str(cache / "packages"),
                       NUGET_HTTP_CACHE_PATH=str(cache / "http"),
                       NUGET_PLUGINS_CACHE_PATH=str(cache / "plugins"),
                       DOTNET_CLI_HOME=str(cache / "cli"), DOTNET_NOLOGO="true")
    # Capture diagnostics privately. Do not echo a subprocess error, environment
    # value or raw NuGet output into job logs or artifacts.
    try:
        return subprocess.run(["dotnet", "restore", str(project), "--configfile", str(config),
                               "--packages", str(cache / "packages"), "--no-cache",
                               "--force-evaluate", "--verbosity", "quiet"], cwd=cache,
                              env=environment, capture_output=True, text=True, timeout=150)
    except (OSError, subprocess.TimeoutExpired):
        raise ProofError("Bounded private-feed restore could not complete.") from None


def prove() -> dict:
    if not clean():
        raise ProofError("Authentication proof requires an unchanged tracked checkout.")
    config = next((ROOT / name for name in ("NuGet.Config", "NuGet.config") if (ROOT / name).is_file()), None)
    if config is None:
        raise ProofError("Governed NuGet configuration is missing.")
    before = config.read_bytes()
    credential = os.environ.get(KEY)
    if not credential:
        raise ProofError("Source-specific credential environment was not propagated.")
    versions = ET.parse(ROOT / "Directory.Packages.props").getroot().findall(".//PackageVersion")
    version = next((node.get("Version") for node in versions if node.get("Include") == PACKAGE), None)
    if not version or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        raise ProofError("Governed private SDK version is missing or non-exact.")
    with tempfile.TemporaryDirectory(prefix="honua-nuget-auth-", dir=os.environ.get("RUNNER_TEMP")) as folder:
        work = Path(folder)
        result = {}
        for label, value in (("anonymous", None), ("authenticated", credential)):
            cache = work / label
            cache.mkdir()
            project = cache / "Probe.csproj"
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                               '<TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
                               '</PropertyGroup><ItemGroup><PackageReference Include="' + PACKAGE + '" Version="' + version + '" />'
                               '</ItemGroup></Project>')
            execution = restore(project, config, cache, value)
            if label == "anonymous":
                diagnostics = execution.stdout + execution.stderr
                if execution.returncode == 0 or FEED not in diagnostics or not re.search(r"\b(?:401|403)\b", diagnostics):
                    raise ProofError("Anonymous cold restore did not establish the expected private-feed refusal.")
                result["anonymousRestoreRefused"] = True
            else:
                if execution.returncode != 0:
                    raise ProofError("Authenticated cold restore failed; check package access and feed availability.")
                package = cache / "packages" / PACKAGE.lower() / version / (PACKAGE.lower() + "." + version + ".nupkg")
                if not package.is_file():
                    raise ProofError("Authenticated cold restore produced no expected package bytes.")
                with zipfile.ZipFile(package) as archive:
                    names = [name for name in archive.namelist() if name.endswith(".nuspec") and "/" not in name]
                    if len(names) != 1:
                        raise ProofError("Restored package identity is ambiguous.")
                    metadata = ET.fromstring(archive.read(names[0]))
                    values = {node.tag.split("}")[-1]: node.text for node in metadata.iter()}
                    if values.get("id") != PACKAGE or values.get("version") != version:
                        raise ProofError("Restored package identity differs from the governed reference.")
                result.update(package=PACKAGE, version=version,
                              restoredPackageSha256=hashlib.sha256(package.read_bytes()).hexdigest(),
                              authenticatedColdRestore=True)
        if config.read_bytes() != before or not clean():
            raise ProofError("Cold restore changed tracked source/configuration.")
    return {"schema": "honua.nuget-auth-proof/v1", "status": "passed", "configUnchanged": True,
            "trackedSourceUnchanged": True, "caches": "fresh per attempt; deleted after proof", **result}


def main() -> int:
    if not selected(os.environ.get("GITHUB_EVENT_NAME", ""), changed_paths()):
        print("NuGet authentication paths unchanged; cold proof skipped (ordinary restore remains required).")
        return 0
    try:
        receipt = prove()
    except (ProofError, OSError, ValueError, ET.ParseError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        print("::error::" + (str(error) if isinstance(error, ProofError) else "Private-feed proof could not validate its inputs."), file=sys.stderr)
        return 1
    print(json.dumps(receipt, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
