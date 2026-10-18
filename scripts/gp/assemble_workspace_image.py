#!/usr/bin/env python3
"""One trusted, source-free installed-artifact sandbox assembly/qualification path.

Run under with-build-lock. Input SDK/Nupkg hashes must come from the normal release
attestation. Acquisition uses only published default template locks and the
reviewed wheel manifest, never an editable user project or host credential config.
This script does not publish an image. Normal release publication consumes its
immutable image and evidence only after both language fixtures pass.
"""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
from urllib.request import build_opener, ProxyHandler, HTTPRedirectHandler
from urllib.parse import urlsplit
import zipfile
import xml.etree.ElementTree as ET


def digest(path: Path, algorithm: str = "sha256") -> str:
    result = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""): result.update(chunk)
    return result.hexdigest()


def checked(path: Path, expected: str, algorithm: str = "sha256") -> Path:
    path = path.resolve(strict=True)
    if not re.fullmatch("[a-f0-9]{" + str(hashlib.new(algorithm).digest_size * 2) + "}", expected) or digest(path, algorithm) != expected:
        raise ValueError("Release input " + algorithm.upper() + " mismatch: " + path.name)
    return path


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *_): raise ValueError("Dependency acquisition redirects are not permitted.")


def download(url: str, maximum: int) -> bytes:
    location = urlsplit(url)
    if location.scheme != "https" or location.hostname not in {"pypi.org", "files.pythonhosted.org", "api.nuget.org"} or location.username or location.password:
        raise ValueError("Default dependency acquisition must use the pinned public hosts.")
    opener = build_opener(ProxyHandler({}), NoRedirect())
    with opener.open(url, timeout=60) as response:
        data = response.read(maximum + 1)
    if len(data) > maximum: raise ValueError("Default dependency exceeds acquisition budget.")
    return data


def prepare_verified_nuget_feed(locked: dict, verified_feed: Path) -> None:
    for (name, version), content_hash in sorted(locked.items()):
        if not re.fullmatch(r"[a-z0-9_.-]{1,128}", name) or not re.fullmatch(r"[a-z0-9_.+-]{1,128}", version):
            raise ValueError("Invalid dependency in the published default template lock.")
        expected = base64.b64decode(content_hash, validate=True)
        if len(expected) != 64: raise ValueError("Invalid SHA-512 in the published template lock.")
        filename = f"{name}.{version}.nupkg"
        data = download(f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{filename}", 128 * 1024 * 1024)
        if hashlib.sha512(data).digest() != expected:
            raise ValueError("Default NuGet artifact differs from its lock before extraction: " + name)
        (verified_feed / filename).write_bytes(data)


def assemble(options) -> dict:
    package = checked(options.nupkg, options.nupkg_sha256)
    sdk = checked(options.sdk_archive, options.sdk_sha512, "sha512")
    root = options.root.resolve()
    root.mkdir(parents=True, exist_ok=False)
    inputs = root / "release-inputs"
    inputs.mkdir()
    package_copy = inputs / package.name
    sdk_copy = inputs / sdk.name
    shutil.copyfile(package, package_copy)
    shutil.copyfile(sdk, sdk_copy)
    package = checked(package_copy, options.nupkg_sha256)
    sdk = checked(sdk_copy, options.sdk_sha512, "sha512")
    context = root / "image"
    dotnet_root = context / "dotnet"
    dotnet_root.mkdir(parents=True)
    # Use the complete reviewed SDK archive, including workload manifests/metadata.
    with tarfile.open(sdk) as archive: archive.extractall(dotnet_root, filter="data")
    dotnet = dotnet_root / "dotnet"
    env = {key: os.environ[key] for key in ("PATH", "LANG", "LC_ALL", "TMPDIR", "TEMP", "SystemRoot") if key in os.environ}
    env |= {"DOTNET_ROOT": str(dotnet_root), "DOTNET_CLI_HOME": str(root / "dotnet-home"),
            "PATH": str(dotnet_root) + os.pathsep + env.get("PATH", ""), "NUGET_PACKAGES": str(root / "tool-packages"),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1", "MSBUILDDISABLENODEREUSE": "1",
            "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "NuGetAudit": "false"}
    (root / "global.json").write_text(json.dumps({"sdk": {"version": options.sdk_version, "rollForward": "disable", "allowPrerelease": True}}))
    sdk_versions = subprocess.check_output([str(dotnet), "--list-sdks"], env=env, cwd=root, text=True)
    if not any(line.startswith(options.sdk_version + " ") for line in sdk_versions.splitlines()): raise ValueError("SDK version does not match release selection.")
    with zipfile.ZipFile(package) as archive:
        nuspec = ET.fromstring(archive.read(next(name for name in archive.namelist() if name.endswith(".nuspec"))))
        metadata = next(node for node in nuspec if node.tag.rsplit("}", 1)[-1] == "metadata")
        package_version = next(node.text for node in metadata if node.tag.rsplit("}", 1)[-1] == "version")
        devkit = context / "devkit"
        devkit.mkdir()
        for name in archive.namelist():
            parts = Path(name).parts
            if len(parts) >= 4 and parts[:3] == ("tools", "net10.0", "any") and not name.endswith("/"):
                relative = Path(*parts[3:])
                if ".." in relative.parts: raise ValueError("Unsafe tool package path.")
                destination = devkit / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(archive.read(name))
    if not (devkit / "honua-gp.dll").is_file(): raise ValueError("The published tool lacks its expected net10.0 payload.")
    if not re.fullmatch(r"[a-zA-Z0-9_.+-]{1,128}", package_version or ""):
        raise ValueError("Invalid release package version.")
    tool_feed = root / "verified-tool-feed"
    tool_feed.mkdir()
    shutil.copyfile(package, tool_feed / ("Honua.Geoprocessing.Cli." + package_version + ".nupkg"))
    checked(tool_feed / ("Honua.Geoprocessing.Cli." + package_version + ".nupkg"), options.nupkg_sha256)
    config = root / "NuGet.Config"
    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="release-artifact", value=str(tool_feed))
    ET.ElementTree(configuration).write(config)
    tools = root / "installed"
    subprocess.run([str(dotnet), "tool", "install", "Honua.Geoprocessing.Cli", "--version", package_version,
                    "--tool-path", str(tools), "--configfile", str(config)], env=env, cwd=root, check=True, timeout=180)
    executable = tools / "honua-gp-devkit"
    bootstrap = root / "bootstrap"
    subprocess.run([str(executable), "workspace", "bootstrap", "--output", str(bootstrap)], env=env, cwd=root, check=True, timeout=60)
    defaults = root / "defaults"
    for language in ("python", "dotnet"):
        subprocess.run([str(executable), "init", "--language", language, "--name", "default-" + language,
                        "--output", str(defaults)], env=env, cwd=root, check=True, timeout=60)
    locked = {}
    for path in (defaults / "default-dotnet").rglob("packages.lock.json"):
        for dependencies in json.loads(path.read_text())["dependencies"].values():
            for name, entry in dependencies.items():
                if not entry.get("resolved"): continue
                key = (name.lower(), entry["resolved"].lower())
                value = entry["contentHash"]
                if key in locked and locked[key] != value: raise ValueError("Conflicting default NuGet lock hashes.")
                locked[key] = value
    if not locked: raise ValueError("The installed default .NET template has no dependency lock.")
    seed = root / "dependency-seed"
    seed.mkdir()
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(properties, "TargetFramework").text = "net10.0"
    ET.SubElement(properties, "NuGetAudit").text = "false"
    ET.SubElement(properties, "ManagePackageVersionsCentrally").text = "false"
    ET.SubElement(properties, "ImportDirectoryPackagesProps").text = "false"
    packages = ET.SubElement(project, "ItemGroup")
    for (name, version), _ in sorted(locked.items()): ET.SubElement(packages, "PackageReference", Include=name, Version="[" + version + "]")
    ET.ElementTree(project).write(seed / "seed.csproj")
    verified_feed = root / "verified-default-feed"
    verified_feed.mkdir()
    prepare_verified_nuget_feed(locked, verified_feed)
    # Only verified bytes enter NuGet extraction/MSBuild; no public restore feed.
    sources.find("add").set("key", "verified-default-artifacts")
    sources.find("add").set("value", str(verified_feed))
    ET.ElementTree(configuration).write(config)
    nuget = context / "nuget"
    subprocess.run([str(dotnet), "restore", str(seed / "seed.csproj"), "--packages", str(nuget), "--configfile", str(config),
                    "--disable-parallel", "-p:UseSharedCompilation=false", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false"], env=env, cwd=root, check=True, timeout=900)
    nuget_provenance = {}
    for (name, version), content_hash in sorted(locked.items()):
        directory = nuget / name / version
        artifact = directory / f"{name}.{version}.nupkg"
        if base64.b64encode(bytes.fromhex(digest(artifact, "sha512"))).decode() != content_hash: raise ValueError("Default NuGet artifact differs from published template lock: " + name)
        nuget_provenance[name + "/" + version] = {"contentHash": content_hash, "nupkgSha256": digest(artifact)}
    # Every cache byte comes from this clean restore; remove dependencies not in locks.
    for name in nuget.iterdir():
        for version in name.iterdir():
            if (name.name, version.name) not in locked: shutil.rmtree(version)
        if not any(name.iterdir()): name.rmdir()
    wheels = context / "wheelhouse"
    wheels.mkdir()
    wheel_manifest = json.loads((bootstrap / "default-workspace-wheels.json").read_text())
    for entry in wheel_manifest["wheels"]:
        if not re.fullmatch("[a-zA-Z0-9_.-]+", entry["name"]) or not re.fullmatch("[a-zA-Z0-9_.-]+", entry["version"]): raise ValueError("Invalid reviewed wheel reference.")
        metadata = json.loads(download(f'https://pypi.org/pypi/{entry["name"]}/{entry["version"]}/json', 4 * 1024 * 1024))
        item = next(item for item in metadata["urls"] if item["filename"] == entry["filename"] and item["digests"]["sha256"] == entry["sha256"])
        data = download(item["url"], 32 * 1024 * 1024)
        if hashlib.sha256(data).hexdigest() != entry["sha256"] or Path(entry["filename"]).name != entry["filename"]: raise ValueError("Reviewed wheel artifact mismatch.")
        (wheels / entry["filename"]).write_bytes(data)
    if not re.fullmatch(r"[a-zA-Z0-9_./:-]+@sha256:[a-f0-9]{64}", options.python_image): raise ValueError("Python base requires an immutable release digest.")
    image_file = root / "image.iid"
    subprocess.run(["docker", "build", "--network=none", "--build-arg", "PYTHON_IMAGE=" + options.python_image,
                    "--file", str(bootstrap / "Dockerfile.workspace"), "--iidfile", str(image_file), str(context)], check=True, timeout=900)
    image = image_file.read_text().strip()
    if not re.fullmatch("sha256:[a-f0-9]{64}", image): raise ValueError("Docker returned no immutable image identity.")
    evidence = {"schema": "honua.gp.workspace-assembly/v1", "nupkg": {"version": package_version, "sha256": options.nupkg_sha256},
                "sdk": {"version": options.sdk_version, "archiveSha512": options.sdk_sha512, "archiveSha256": digest(sdk)}, "pythonBase": options.python_image,
                "nuget": nuget_provenance, "wheels": wheel_manifest, "bootstrap": json.loads((bootstrap / "workspace-provenance.json").read_text()), "image": image}
    # Preserve preparation evidence even if qualification fails; never label it qualified.
    evidence_file = root / "assembly.json"
    evidence_file.write_text(json.dumps(evidence, indent=2) + "\n")
    subprocess.run([sys.executable, str(bootstrap / "qualify_installed_workspace.py"), "--devkit", str(executable),
                    "--image", image, "--root", str(root / "installed-qualification")], env=env, cwd=root, check=True, timeout=180)
    subprocess.run([sys.executable, str(bootstrap / "qualify_workspace.py"), "--devkit", str(devkit / "honua-gp.dll"),
                    "--dotnet", str(dotnet), "--image", image, "--root", str(root / "fixture-qualification")], env=env, cwd=root, check=True, timeout=1200)
    evidence["qualified"] = True
    evidence_file.write_text(json.dumps(evidence, indent=2) + "\n")
    return evidence


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--nupkg", type=Path, required=True)
    parser.add_argument("--nupkg-sha256", required=True)
    parser.add_argument("--sdk-archive", type=Path, required=True)
    parser.add_argument("--sdk-sha512", required=True)
    parser.add_argument("--sdk-version", required=True)
    parser.add_argument("--python-image", required=True)
    parser.add_argument("--root", type=Path, required=True)
    print(json.dumps(assemble(parser.parse_args())))
