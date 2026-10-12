#!/usr/bin/env python3
"""Opt-in OData composition experiment; never substitutes for repository gates."""
from __future__ import annotations
import argparse
import contextlib
import ctypes
import datetime
import hashlib
import json
import math
import os
import platform
from pathlib import Path
import re
import shutil
import signal
import statistics
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

PROJECTS = {
    "baseline": {"unit": "tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj",
                 "component": "tests/dotnet/Honua.Protocols.OData.ComponentTests/Honua.Protocols.OData.ComponentTests.csproj",
                 "image": "src/Honua.Server/Honua.Server.csproj"},
    "candidate": {"unit": "tests/dotnet/Honua.Protocols.OData.UnitTests/Honua.Protocols.OData.UnitTests.csproj",
                  "component": "tests/dotnet/Honua.Protocols.OData.ComponentTests/Honua.Protocols.OData.ComponentTests.csproj",
                  "image": "tests/dotnet/Honua.OData.TestHost/Honua.OData.TestHost.csproj"},
}
UNIT_CLASSES = ('ODataCloudNativeRegressionTests', 'ODataFeatureProviderResolverTests', 'ODataSearchServiceSqlTests', 'ODataQueryParameterAdapterOrderByTests', 'ODataGeometryConverterTests', 'ODataCrsUtilitiesTests', 'ODataApplyPipelineTests', 'ODataEtagSerializationTests', 'ODataSqlFragmentMergeHelperTests', 'ODataPageSizeClampTests', 'ODataSkipTokenServiceTests', 'ODataCrudServiceStorageLayerTests', 'ODataComputeServiceTests')
COMPONENT_FILTER = "FullyQualifiedName~Honua.Protocols.OData.ComponentTests.ODataContractTests"
UNIT_FILTER = "|".join("FullyQualifiedName~" + name for name in UNIT_CLASSES)
SCENARIOS = ("cold-build-warm-packages", "unchanged-incremental", "odata-only-edit")
SCHEMA = "honua.odata-pilot/v1"
ITEMS = "Compile,EmbeddedResource,Content,None,AdditionalFiles,Analyzer,ProjectReference"
PROPERTIES = "MSBuildProjectFullPath,MSBuildAllProjects,TargetFramework,TargetFrameworks,DefineConstants,RuntimeIdentifier,Configuration"


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")


def command(args, root):
    return subprocess.check_output(args, cwd=root, text=True).strip()


def runtime_pin(root):
    match = re.search(r"^ARG DOTNET_ASPNET_IMAGE=(\S+@sha256:[0-9a-f]{64})$", (root / "Dockerfile").read_text(), re.M)
    if not match:
        raise ValueError("Main Dockerfile must supply one digest-pinned ASP.NET runtime")
    return match[1]


PILOT_METADATA_PROPERTIES = {
    "IncludeSourceRevisionInInformationalVersion": "false",
    "EnableSourceControlManagerQueries": "false",
    "EnableSourceLink": "false",
}


def props(configuration, composition, runtime=None):
    return [f"-p:Configuration={configuration}", f"-p:HonuaBuildProfile={composition}", "-p:TreatWarningsAsErrors=true", "-p:UseSharedCompilation=false",
            *[f"-p:{key}={value}" for key, value in PILOT_METADATA_PROPERTIES.items()],
            *([f"-p:RuntimeIdentifier={runtime}", "-p:SelfContained=false"] if runtime else [])]


def plan(root, variant, selection, configuration, composition, test_filter, runtime=None):
    if runtime and selection != "image":
        raise ValueError("--runtime applies only to image selection")
    runtime = (runtime or "linux-x64") if selection == "image" else None
    if runtime not in (None, "linux-x64", "linux-arm64"):
        raise ValueError("Image runtime must be linux-x64 or linux-arm64")
    project = PROJECTS[variant][selection]
    test_filter = test_filter or (UNIT_FILTER if selection == "unit" else COMPONENT_FILTER if selection == "component" else "")
    blockers = [] if (root / project).is_file() else [f"Missing extracted project: {project}"]
    if selection != "image" and not test_filter:
        blockers.append("Explicit comparable test filter required for baseline and candidate")
    return {"schema": SCHEMA, "variant": variant, "selection": selection, "project": project,
            "configuration": configuration, "composition": composition, "filter": test_filter,
            "blockers": blockers, "optIn": True, "fullGatesUnchanged": True,
            "entryProjects": [project, "src/Honua.Server/Honua.Server.csproj"] if variant == "baseline" and selection == "component" else [project],
            "externalPrerequisites": ["Orchestrated full/minimal host receipt and external URL/admin key; canonical odata.yaml seeds, page limit2, edit payload4096"] if selection == "component" else ["Managed bounded builder argv and matching fresh shared-admission receipt"] if selection == "image" else [],
            "commands": [["dotnet", "restore", project, *props(configuration, composition, runtime)],
                         ["dotnet", "build", project, "--no-restore", *props(configuration, composition, runtime)],
                         ["dotnet", "publish" if selection == "image" else "test", project, "--no-build", "--no-restore", *props(configuration, composition, runtime)]],
            "runtimeIdentifier": runtime, "dockerPlatform": {"linux-x64": "linux/amd64", "linux-arm64": "linux/arm64"}.get(runtime),
            "runtimeImage": runtime_pin(root) if selection == "image" else None}


def normalize(value, root):
    if isinstance(value, dict):
        return {normalize(k, root): normalize(v, root) for k, v in value.items()}
    if isinstance(value, list):
        return [normalize(v, root) for v in value]
    return value.replace(str(root), "$REPO") if isinstance(value, str) else value


def flow_reservation_helper():
    override = os.environ.get("HONUA_FLOW_RESERVATION_HELPER")
    if override:
        return Path(override).expanduser().resolve()
    selected = shutil.which("dotnet")
    if not selected:
        return None
    shim = Path(selected).resolve()
    # Only the selected lane shim's adjacent library is relevant. A different
    # checkout installed elsewhere must not govern this executor's arguments.
    if shim.parent.name != "lane-bin":
        return None
    helper = shim.parent.parent / "lib" / "build-reservation.py"
    return helper if helper.is_file() else None


def evaluation_preflight(root, project, configuration, composition, runtime=None, helper=None):
    # The canonical helper's key operation is read-only: it validates argument
    # parsing without acquiring capacity or starting MSBuild.
    helper = Path(helper) if helper else flow_reservation_helper()
    if helper is None:
        return
    if not helper.is_file():
        raise ValueError("Configured Flow reservation helper is missing")
    query = ["msbuild", str(root / project), f"-getItem:{ITEMS}", f"-getProperty:{PROPERTIES}",
             *props(configuration, composition, runtime)]
    checked = subprocess.run([sys.executable, str(helper), "key", *query], cwd=root,
                             capture_output=True, text=True, timeout=10)
    if checked.returncode:
        raise ValueError("Installed Flow reservation parser cannot admit MSBuild evaluation queries; "
                         "review/update its support for -getItem: and -getProperty: before retrying. "
                         "No restore or build started. " + checked.stderr.strip()[-1000:])


def evaluated_inputs(root, project, configuration, composition, evidence, measure=None, runtime=None):
    def invoke(name, argv):
        if measure is None:
            return command(argv, root)
        measure(name, argv)
        return (evidence / name / "output.log").read_text().strip()
    entries = [project] if isinstance(project, str) else project
    graph = {"roots": entries, "projects": {}}
    for index, entry in enumerate(entries):
        graph_file = evidence / f"restore-graph-{index}.json"
        invoke(f"evaluate-graph-{index}", ["dotnet", "msbuild", entry, "-t:GenerateRestoreGraphFile", f"-p:RestoreGraphOutputPath={graph_file}",
                 "/verbosity:quiet", *props(configuration, composition, runtime)])
        restored = json.loads(graph_file.read_text())
        graph["projects"].update(restored["projects"])
    projects = sorted(Path(p).resolve() for p in graph["projects"])
    files = set()
    evaluations = {}
    for index, path in enumerate(projects):
        raw = invoke(f"evaluate-{index:03d}", ["dotnet", "msbuild", str(path), f"-getItem:{ITEMS}", f"-getProperty:{PROPERTIES}",
                       *props(configuration, composition, runtime)])
        evaluated = json.loads(raw)
        if evaluated["Properties"].get("TargetFrameworks"):
            raise ValueError(f"Multi-target graph needs explicit per-framework evaluation: {path}")
        # Restore graphs can omit build-only analyzer/project references. Never
        # hash only their project file while ignoring their actual source closure.
        for reference in evaluated["Items"].get("ProjectReference", []):
            reference_path = reference.get("FullPath") or reference.get("Identity")
            if not reference_path:
                raise ValueError(f"Evaluated ProjectReference lacks a path: {path}")
            child = Path(reference_path)
            child = (child if child.is_absolute() else path.parent / child).resolve()
            if child not in projects:
                raise ValueError(f"Evaluated ProjectReference omitted from restore graph: {child}; complete build-only/analyzer closure required")
        evaluations[str(path)] = evaluated
        files.add(path)
        for imported in evaluated["Properties"].get("MSBuildAllProjects", "").split(";"):
            if imported:
                files.add(Path(imported).resolve())
        for items in evaluated["Items"].values():
            for item in items:
                identity = item.get("FullPath") or item.get("Identity")
                if identity:
                    full = Path(identity)
                    files.add((full if full.is_absolute() else path.parent / full).resolve())
        # Directory-level build inputs can be implicit imports and package locks
        # affect restore even when absent from Compile/Content item lists.
        for parent in [path.parent, *path.parents]:
            if not parent.is_relative_to(root):
                continue
            for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.config", "global.json"):
                if (parent / name).is_file():
                    files.add(parent / name)
        if (path.parent / "packages.lock.json").is_file():
            files.add(path.parent / "packages.lock.json")
    for name in ("global.json", "NuGet.config", ".editorconfig", "scripts/ci/odata-pilot/pilot.py"):
        if (root / name).is_file():
            files.add(root / name)
    inputs = {}
    for path in sorted(files):
        # Generated build outputs are not source identity. Their generation is
        # described by the evaluated graph and its checked-in source inputs.
        if path.is_relative_to(root) and any(p in {"obj", "bin"} for p in path.relative_to(root).parts):
            continue
        if path.is_dir():
            continue
        key = str(path.relative_to(root)) if path.is_relative_to(root) else str(path)
        inputs[key] = hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else "MISSING"
    # Only the three documented filesystem timestamps are nonsemantic item
    # metadata. Keep every other evaluated item value in the hashed identity;
    # raw query output remains untouched in its evidence stage output.log.
    stable_evaluations = json.loads(json.dumps(evaluations))
    for evaluation in stable_evaluations.values():
        for items in evaluation["Items"].values():
            for item in items:
                for key in list(item):
                    if key.lower() in {"modifiedtime", "createdtime", "accessedtime"}:
                        del item[key]
    evaluated = normalize({"graph": graph, "evaluations": stable_evaluations}, root)
    identity = {"sdk": command(["dotnet", "--version"], root), "os": platform.system(), "osVersion": platform.release(), "architecture": platform.machine(),
                "configuration": configuration, "composition": composition, "buildProperties": props(configuration, composition, runtime), "evaluatedGraph": evaluated, "inputs": inputs}
    return {"key": digest(identity), "identity": identity, "projectCount": len(projects),
            "compileItemCount": sum(len(e["Items"].get("Compile", [])) for e in evaluations.values()),
            "projects": [str(p.relative_to(root)) for p in projects]}


class ContainmentError(ValueError):
    pass


def process_snapshot():
    result = {}
    for path in Path("/proc").glob("[0-9]*/stat"):
        try:
            fields = path.read_text().rsplit(")", 1)[1].split()
            result[int(path.parent.name)] = {"parent": int(fields[1]), "rss": int(fields[21]) * os.sysconf("SC_PAGE_SIZE"),
                                             "start": fields[19], "state": fields[0]}
        except (OSError, ValueError, IndexError, ProcessLookupError):
            continue
    return result


def owned_processes(process, known):
    snapshot = process_snapshot()
    if process.poll() is None and process.pid not in snapshot:
        raise ContainmentError("Cannot inspect the owned process; descendant containment is unproven")
    roots = {pid for pid, started in known.items() if pid in snapshot and snapshot[pid]["start"] == started}
    if process.pid not in known and process.pid in snapshot and process.poll() is None:
        roots.add(process.pid)
    # This single-threaded harness enables subreaping before launching work and
    # rejects pre-existing children. Adopted children therefore belong to this
    # stage, including guardians which deliberately create their own session.
    roots |= {pid for pid, item in snapshot.items() if item["parent"] == os.getpid()}
    while True:
        children = {pid for pid, item in snapshot.items() if item["parent"] in roots}
        if children.issubset(roots):
            break
        roots |= children
    for pid in roots & snapshot.keys():
        known[pid] = snapshot[pid]["start"]
    live = {pid: snapshot[pid] for pid in roots & snapshot.keys() if snapshot[pid]["state"] != "Z"}
    for pid in roots & snapshot.keys():
        if pid != process.pid and snapshot[pid]["parent"] == os.getpid() and snapshot[pid]["state"] == "Z":
            with contextlib.suppress(ChildProcessError):
                os.waitpid(pid, os.WNOHANG)
    return live


def stop_owned(process, known):
    started = time.monotonic()
    while True:
        try:
            live = owned_processes(process, known)
        except ContainmentError:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=2)
            raise
        if not live:
            return
        if time.monotonic() - started > 20:
            raise ContainmentError("Owned descendants did not terminate; source inputs retained for manual reconciliation")
        sig = signal.SIGTERM if time.monotonic() - started < 2 else signal.SIGKILL
        for pid, identity in live.items():
            # Recheck PID start identity immediately before signalling. Never
            # target a process solely by name, a stale PID, or another job's group.
            current = process_snapshot().get(pid)
            if current and current["start"] == identity["start"]:
                with contextlib.suppress(ProcessLookupError):
                    os.kill(pid, sig)
        process.poll()
        time.sleep(.05)


def stage(name, argv, root, evidence, timeout):
    if platform.system() != "Linux" or ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0) != 0:
        raise ValueError("Measured execution requires Linux owned-descendant containment; planning remains portable")
    if any(item["parent"] == os.getpid() and item["state"] != "Z" for item in process_snapshot().values()):
        raise ContainmentError("Harness already has live children; refusing ambiguous ownership")
    directory = evidence / name
    directory.mkdir()
    env = os.environ.copy()
    env["HONUA_BUILD_SLOT_LEDGER_DIR"] = str(directory / "slot-ledger")
    env["MSBUILDDISABLENODEREUSE"] = "1"
    env["UseSharedCompilation"] = "false"
    start = time.monotonic()
    started_epoch = time.time()
    peak = 0
    known = {}
    failure = None
    with (directory / "output.log").open("w") as log:
        process = subprocess.Popen(argv, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            while True:
                live = owned_processes(process, known)
                peak = max(peak, sum(item["rss"] for item in live.values()))
                if process.poll() is not None and not live:
                    break
                if time.monotonic() - start > timeout:
                    raise ValueError(f"Stage {name} exceeded {timeout}s")
                time.sleep(.1)
        except BaseException as error:
            failure = error
            stop_owned(process, known)
        finally:
            if process.poll() is None:
                stop_owned(process, known)
            process.wait()
    waits = []
    for path in (directory / "slot-ledger").glob("*.log"):
        waits.extend(int(n) for n in re.findall(r"waited=(\d+)", path.read_text()))
    elapsed = time.monotonic() - start
    finished_epoch = time.time()
    result = {"name": name, "argv": argv, "seconds": elapsed, "startedEpochSeconds": started_epoch,
              "startedMonotonicSeconds": start, "finishedEpochSeconds": finished_epoch, "clockSkewSeconds": abs(finished_epoch - started_epoch - elapsed), "exitCode": process.returncode, "error": str(failure) if failure else None,
              "peakProcessTreeRssBytes": peak or None, "queueWaitSeconds": sum(waits) if waits else None,
              "queueWaitUnavailableReason": None if waits else "No isolated Flow build-slot ledger emitted",
              "peakJobMemoryBytes": None, "jobMemoryUnavailableReason": "Harness does not own an exclusive job cgroup"}
    write(directory / "stage.json", result)
    if failure:
        raise failure
    if process.returncode:
        raise ValueError(f"Stage {name} failed ({process.returncode}); see {directory / 'output.log'}")
    return result


def trx_results(directory):
    results = []
    for path in directory.glob("*.trx"):
        document = ET.parse(path)
        methods = {}
        for unit in document.findall(".//{*}UnitTest"):
            method = unit.find("{*}TestMethod")
            if method is not None:
                methods[unit.attrib["id"]] = method.attrib.get("className", "") + "." + method.attrib.get("name", "")
        for result in document.findall(".//{*}UnitTestResult"):
            results.append({"identity": methods.get(result.attrib["testId"], "UNKNOWN") + "|" + result.attrib["testName"],
                            "outcome": result.attrib["outcome"], "startedAt": result.attrib.get("startTime"),
                            "duration": result.attrib.get("duration")})
    if not results or any(r["identity"].startswith("UNKNOWN") for r in results):
        raise ValueError("Missing or unidentified TRX results; no comparable test evidence")
    identities = [r["identity"] for r in results]
    if len(identities) != len(set(identities)):
        raise ValueError("Duplicate TRX test identities")
    return sorted(results, key=lambda r: r["identity"])


def output_size(root, projects):
    paths = set()
    for project in projects:
        for directory in (root / project).parent.glob("*"):
            if directory.name in {"bin", "obj"} and directory.is_dir():
                paths.update(p for p in directory.rglob("*") if p.is_file() and not p.is_symlink())
    return sum(p.stat().st_size for p in paths)


@contextlib.contextmanager
def edit_source(root, scenario, relative, pair):
    if scenario != "odata-only-edit":
        yield
        return
    if not relative or not relative.startswith("src/Honua.Protocols.OData/") or not relative.endswith(".cs"):
        raise ValueError("OData edit scenario requires --edit-file src/Honua.Protocols.OData/...cs")
    path = (root / relative).resolve()
    if not path.is_relative_to(root / "src/Honua.Protocols.OData"):
        raise ValueError("Edit path escapes OData sources")
    original = path.read_bytes()
    modified = original + f"\n// OData pilot paired edit {pair}\n".encode()
    try:
        path.write_bytes(modified)
        yield
    finally:
        if isinstance(sys.exc_info()[1], ContainmentError):
            raise ContainmentError("OData edit retained because descendant termination is unproven")
        if path.read_bytes() != modified:
            raise ValueError("OData source changed concurrently; preserve it and reconcile the edit manually")
        path.write_bytes(original)


def ensure_runtime_only(directory):
    for path in directory.rglob("*"):
        name = path.name.lower()
        if path.is_symlink() or name in {"nuget.config", ".npmrc", ".env", "sdk", "roslyn"} or name.startswith(("microsoft.build", "microsoft.codeanalysis", "csc.", "vbc.")) or name.endswith((".pfx", ".pem", ".key")):
            raise ValueError(f"Compiler/SDK/credential input prohibited in runtime context: {path.name}")
        if path.is_file() and path.suffix.lower() == ".json":
            try:
                value = json.loads(path.read_text())
            except (UnicodeDecodeError, json.JSONDecodeError):
                continue
            def contains_credentials(item):
                if isinstance(item, dict):
                    for key, child in item.items():
                        field = re.sub(r"[^a-z0-9]", "", re.split(r":|__", key.lower())[-1])
                        if isinstance(child, str) and child and (field in {"password", "adminpassword", "masterkey", "clientsecret", "apikey", "secretaccesskey", "accesskey", "token", "signingkey", "privatesigningkey", "privatekey", "encryptionkey", "symmetricsecuritykey"}
                            or re.search(r"(?:Password|Pwd)\s*=\s*[^;]+", child, re.I)):
                            return True
                        if contains_credentials(child):
                            return True
                if isinstance(item, list):
                    return any(contains_credentials(child) for child in item)
                return False
            if contains_credentials(value):
                raise ValueError(f"Embedded credentials prohibited in runtime context: {path.name}")


def managed_builder(config_path, receipt_path, context, tag, pin, assembly, input_key, root, evidence, timeout, platform="linux/amd64"):
    config = json.loads(config_path.read_text())
    argv = config.get("argv")
    if not isinstance(argv, list) or not argv or any(not isinstance(value, str) for value in argv):
        raise ValueError("Managed builder config requires an argv array")
    if receipt_path.exists():
        raise ValueError("Builder receipt path must be new")
    substitutions = {"{context}": str(context), "{tag}": tag, "{runtime}": pin, "{assembly}": assembly,
                     "{inputKey}": input_key, "{receipt}": str(receipt_path), "{platform}": platform}
    expanded = []
    for value in argv:
        for key, replacement in substitutions.items():
            value = value.replace(key, replacement)
        expanded.append(value)
    measured = stage("image", expanded, root, evidence, timeout)
    receipt = json.loads(receipt_path.read_text())
    limits = receipt.get("limits", {})
    if (receipt.get("schema") != "honua.odata-pilot-managed-builder/v1"
        or receipt.get("inputKey") != input_key or receipt.get("image") != tag
        or receipt.get("platform") != platform
        or receipt.get("sharedAdmission") is not True or receipt.get("boundedBuildKit") is not True
        or not receipt.get("reservationId") or receipt.get("exitCode") != 0
        or not isinstance(receipt.get("peakJobMemoryBytes"), int) or isinstance(receipt.get("peakJobMemoryBytes"), bool) or receipt["peakJobMemoryBytes"] <= 0
        or not finite_between(limits.get("memoryMaxBytes"), 1, 8 * 1024**3)
        or not finite_between(limits.get("cpuQuotaPercent"), 1, 400)
        or not finite_between(limits.get("pidsMax"), 1, 512)
        or not bounded_interval(receipt.get("startedEpochSeconds"), receipt.get("finishedEpochSeconds"), measured["startedEpochSeconds"], measured["finishedEpochSeconds"])):
        raise ValueError("Managed builder lacks fresh matching bounds, shared reservation, BuildKit ownership, or job-memory evidence")
    measured["managedBuilderReceipt"] = receipt
    measured["peakJobMemoryBytes"] = receipt["peakJobMemoryBytes"]
    measured["jobMemoryUnavailableReason"] = None
    measured["jobMemoryScope"] = "Owned bounded BuildKit job only"
    write(evidence / "image/stage.json", measured)
    return measured


def run(args):
    if args.selection == "component":
        raise ValueError("Measured component runs require an implemented managed host lifecycle controller; precreated host receipts cannot bind a live host to this execution. Run the component project directly for developer validation.")
    root = args.root.resolve()
    evidence = args.output.resolve()
    if evidence.exists():
        raise ValueError("Evidence destination must be new; never overwrite a measured run")
    evidence.mkdir(parents=True)
    planned = plan(root, args.variant, args.selection, args.configuration, args.composition, args.filter, getattr(args, "runtime", None))
    write(evidence / "plan.json", planned)
    if planned["blockers"]:
        raise ValueError("; ".join(planned["blockers"]))
    project = planned["project"]
    evaluation_preflight(root, project, args.configuration, args.composition, planned["runtimeIdentifier"])
    validate_scenario(args.selection, args.scenario)
    if args.selection == "component" and (not args.host_receipt or not os.environ.get("HONUA_ODATA_TEST_BASE_URL") or not os.environ.get("HONUA_ODATA_TEST_ADMIN_KEY")):
        raise ValueError("Component comparison requires orchestrated host receipt and external host URL/admin key for both variants")
    if args.selection == "image" and (not args.builder_json or not args.builder_receipt):
        raise ValueError("Image build requires an explicit managed bounded builder and fresh shared-admission receipt")
    common = props(args.configuration, args.composition, planned["runtimeIdentifier"])
    stages = []
    receipt = {**planned, "scenario": args.scenario, "pair": args.pair, "qualified": False, "stages": stages,
               "sourceCommit": command(["git", "rev-parse", "HEAD"], root),
               "trackedDiffDigest": hashlib.sha256(command(["git", "diff", "HEAD", "--binary"], root).encode()).hexdigest(),
               "memoryMetric": "100ms sampled sum of RSS in each owned subprocess tree; shared pages can be counted more than once"}
    try:
        # Restore is separately timed; cold-build retains the warmed package cache.
        stages.append(stage("restore", ["dotnet", "restore", project, *common], root, evidence, args.timeout))
        if len(planned["entryProjects"]) > 1:
            stages.append(stage("restore-full-host", ["dotnet", "restore", planned["entryProjects"][1], *common], root, evidence, args.timeout))
        if args.scenario == "cold-build-warm-packages":
            stages.append(stage("clean", ["dotnet", "clean", project, *common], root, evidence, args.timeout))
            if len(planned["entryProjects"]) > 1:
                stages.append(stage("clean-full-host", ["dotnet", "clean", planned["entryProjects"][1], *common], root, evidence, args.timeout))
        else:
            # Establish the exact unedited inputs before timing an incremental
            # build; do not mislabel a caller's empty output tree as incremental.
            stages.append(stage("warm-up", ["dotnet", "build", project, "--no-restore", *common], root, evidence, args.timeout))
            if len(planned["entryProjects"]) > 1:
                stages.append(stage("warm-up-full-host", ["dotnet", "build", planned["entryProjects"][1], "--no-restore", *common], root, evidence, args.timeout))
        with edit_source(root, args.scenario, args.edit_file, args.pair):
            def measure(name, argv):
                result = stage(name, argv, root, evidence, args.timeout)
                stages.append(result)
                return result
            inputs = evaluated_inputs(root, planned["entryProjects"], args.configuration, args.composition, evidence, measure, planned["runtimeIdentifier"])
            receipt["artifact"] = inputs
            host_build = None
            if args.selection == "component" and args.variant == "baseline":
                host_build = stage("build-full-host", ["dotnet", "build", "src/Honua.Server/Honua.Server.csproj", "--no-restore", *common], root, evidence, args.timeout)
                stages.append(host_build)
            build = stage("build", ["dotnet", "build", project, "--no-restore", *common], root, evidence, args.timeout)
            stages.append(build)
            if args.selection == "image":
                publish = evidence / "publish"
                stages.append(stage("publish", ["dotnet", "publish", project, "--no-build", "--no-restore", "--output", str(publish), *common], root, evidence, args.timeout))
                context = evidence / "image-context"
                context.mkdir()
                removed_configs = [p.relative_to(publish).as_posix() for p in publish.rglob("appsettings.*.json")]
                shutil.copytree(publish, context / "publish", ignore=shutil.ignore_patterns("appsettings.*.json"))
                receipt["excludedEnvironmentConfigs"] = sorted(removed_configs)
                ensure_runtime_only(context / "publish")
                shutil.copyfile(Path(__file__).resolve().parents[3] / "docker/odata-pilot/Dockerfile", context / "Dockerfile")
                pin = runtime_pin(root)
                receipt["artifact"]["runtimeImage"] = pin
                receipt["artifact"]["key"] = digest({"publishIdentity": inputs["key"], "runtimeImage": pin,
                    "dockerfile": hashlib.sha256((context / "Dockerfile").read_bytes()).hexdigest()})
                tag = "honua-odata-pilot:" + receipt["artifact"]["key"][:16]
                assembly = "Honua.Server" if args.variant == "baseline" else "Honua.OData.TestHost"
                stages.append(managed_builder(args.builder_json, args.builder_receipt, context, tag, pin, assembly,
                    receipt["artifact"]["key"], root, evidence, args.timeout, planned["dockerPlatform"]))
                receipt["imageBytes"] = int(command(["docker", "image", "inspect", tag, "--format", "{{.Size}}"], root))
                inspected_platform = json.loads(command(["docker", "image", "inspect", tag, "--format", "{{json .}}"], root))
                actual_platform = inspected_platform["Os"] + "/" + inspected_platform["Architecture"]
                if actual_platform != planned["dockerPlatform"]:
                    raise ValueError("Built image platform differs from publish runtime")
                receipt["imagePlatform"] = actual_platform
                receipt["image"] = tag
                receipt["tests"] = []
                receipt["firstTestSeconds"] = None
            else:
                host = None
                if args.selection == "component":
                    host = json.loads(args.host_receipt.read_text())
                    expected_mode = "full" if args.variant == "baseline" else "minimal"
                    if (host.get("inputKey") != inputs["key"] or host.get("mode") != expected_mode
                        or host.get("bounded") is not True or host.get("ready") is not True
                        or host.get("seedManifest") != "odata.yaml" or not isinstance(host.get("startupSeconds"), (int, float))
                        or not finite_between(host["startupSeconds"], 0, args.timeout) or host.get("pageLimit") != 2 or host.get("editPayloadLimit") != 4096):
                        raise ValueError("Orchestrated host receipt does not match current graph, seed/config, bounded readiness, or startup timing")
                    receipt["host"] = host
                trx = evidence / "trx"
                stages.append(stage("test", ["dotnet", "test", project, "--no-build", "--no-restore", "--configuration", args.configuration,
                    "--filter", planned["filter"], "--logger", "trx;LogFileName=tests.trx", "--results-directory", str(trx), *common], root, evidence, args.timeout))
                receipt["tests"] = trx_results(trx)
                receipt["buildToTestsCompleteSeconds"] = build["seconds"] + stages[-1]["seconds"]
                first_build = host_build or build
                starts = [datetime.datetime.fromisoformat(r["startedAt"].replace("Z", "+00:00")).timestamp()
                          for r in receipt["tests"] if r["startedAt"]]
                first = min(starts) if len(starts) == len(receipt["tests"]) else None
                if first is not None and stages[-1]["startedEpochSeconds"] <= first <= stages[-1]["finishedEpochSeconds"]:
                    receipt["firstTestSeconds"] = stages[-1]["startedMonotonicSeconds"] - first_build["startedMonotonicSeconds"] + first - stages[-1]["startedEpochSeconds"] + (host["startupSeconds"] if host else 0)
                    receipt["firstTestClock"] = "Monotonic build-to-test-stage span plus TRX UTC offset within checked test-stage clock"
                    if host:
                        receipt["firstTestClock"] += "; plus separately measured external host startup (not part of the stage span)"
                else:
                    receipt["firstTestSeconds"] = None
                    receipt["firstTestUnavailableReason"] = "Missing TRX start times or clock mismatch"
            receipt["diskOutputBytes"] = output_size(root, inputs["projects"])
            receipt["peakProcessTreeRssBytes"] = max((s["peakProcessTreeRssBytes"] or 0 for s in stages), default=0) or None
            receipt["qualified"] = args.selection != "image" and all(r["outcome"] == "Passed" for r in receipt["tests"])
    finally:
        write(evidence / "receipt.json", receipt)
    return receipt


def validate_scenario(selection, scenario):
    if selection == "component" and scenario == "odata-only-edit":
        raise ValueError("Component odata-only-edit requires a controller callback after the edited build; prebuilt host receipts cannot match its changed input key")


def bounded_interval(start, end, outer_start, outer_end):
    return (finite_between(start, outer_start, outer_end)
            and finite_between(end, outer_start, outer_end) and start <= end)


def finite_between(value, low, high):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and low <= value <= high


def compare(receipts, selection):
    selected = [r for r in receipts if r.get("selection") == selection]
    evidence = {}
    issues = []
    if selection == "component":
        issues.append("Component promotion is blocked until managed host lifecycle control and live execution binding are implemented; precreated receipts are insufficient")
    signatures = {tuple(sorted((test["identity"], test["outcome"]) for test in receipt.get("tests", []))) for receipt in selected}
    if len(signatures) != 1:
        issues.append("All scenarios and repetitions must retain the same successful test identities/results")
    for scenario in SCENARIOS:
        pairs = []
        for pair in (1, 2, 3):
            sides = {}
            for variant in ("baseline", "candidate"):
                matched = [r for r in selected if r.get("scenario") == scenario and r.get("pair") == pair and r.get("variant") == variant]
                if len(matched) != 1:
                    issues.append(f"{scenario}/{pair}: expected exactly one {variant} receipt")
                    continue
                sides[variant] = matched[0]
            if len(sides) != 2:
                continue
            baseline, candidate = sides["baseline"], sides["candidate"]
            if not all(r.get("schema") == SCHEMA and r.get("qualified") for r in sides.values()):
                issues.append(f"{scenario}/{pair}: unqualified or incompatible receipts")
                continue
            identities = [{r["identity"]: r["outcome"] for r in receipt["tests"]} for receipt in sides.values()]
            valid_tests = all(len(identity) == len(receipt["tests"]) and all(outcome == "Passed" for outcome in identity.values())
                              for identity, receipt in zip(identities, sides.values()))
            if not valid_tests or not identities[0] or identities[0] != identities[1]:
                issues.append(f"{scenario}/{pair}: test identities or outcomes differ")
                continue
            comparable_timing = True
            for receipt in sides.values():
                timed = [s for s in receipt.get("stages", []) if s["name"].startswith("build") or s["name"] == "test"]
                if len(timed) < 2 or any(s.get("queueWaitSeconds") != 0 or not finite_between(s.get("clockSkewSeconds"), 0, .1) for s in timed):
                    comparable_timing = False
            for receipt in sides.values():
                if selection == "component":
                    host = receipt.get("host", {})
                    if host.get("queueWaitSeconds") != 0 or not finite_between(host.get("clockSkewSeconds"), 0, .1):
                        comparable_timing = False
            if not comparable_timing:
                issues.append(f"{scenario}/{pair}: unknown/nonzero queue delay or invalid stage clocks; promotion ineligible")
                continue
            fields = ("sdk", "os", "osVersion", "architecture", "configuration", "composition")
            if any(baseline["artifact"]["identity"][f] != candidate["artifact"]["identity"][f] for f in fields):
                issues.append(f"{scenario}/{pair}: SDK/platform/configuration/composition mismatch")
                continue
            metrics = {}
            for name, path in (("graph", ("artifact", "projectCount")), ("firstTest", ("firstTestSeconds",)),
                               ("peakMemory", ("peakProcessTreeRssBytes",))):
                values = []
                for receipt in (baseline, candidate):
                    value = receipt
                    for segment in path:
                        value = value.get(segment) if isinstance(value, dict) else None
                    values.append(value)
                if not all(isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) and v > 0 for v in values):
                    issues.append(f"{scenario}/{pair}: missing or invalid {name} metric")
                    continue
                metrics[name] = {"baseline": values[0], "candidate": values[1], "reduction": 1 - values[1] / values[0]}
            pairs.append(metrics)
        evidence[scenario] = pairs
    reductions = {}
    for scenario, pairs in evidence.items():
        if len(pairs) != 3 or any(len(p) != 3 for p in pairs):
            continue
        reductions[scenario] = {metric: statistics.median(p[metric]["reduction"] for p in pairs) for metric in ("graph", "firstTest", "peakMemory")}
    cold = reductions.get("cold-build-warm-packages", {})
    thresholds = {"graphReductionAtLeast50Percent": cold.get("graph", -1) >= .50,
                  "cleanBuildToFirstTestReductionAtLeast30Percent": cold.get("firstTest", -1) >= .30,
                  "peakProcessMemoryReductionAtLeast25Percent": cold.get("peakMemory", -1) >= .25,
                  "noIncrementalRegression": all(
                      scenario in reductions and all(p["firstTest"]["candidate"] <= p["firstTest"]["baseline"] for p in evidence[scenario])
                      for scenario in ("unchanged-incremental", "odata-only-edit"))}
    return {"schema": SCHEMA, "selection": selection, "pairedEvidence": evidence, "medianReductions": reductions,
            "thresholds": thresholds, "issues": issues, "passed": not issues and all(thresholds.values()),
            "scope": "Opt-in pilot only; full-solution, provider, AOT, image and protocol-composition gates remain required"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="operation", required=True)
    for operation in ("plan", "identity", "run"):
        child = sub.add_parser(operation)
        child.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3])
        child.add_argument("--variant", choices=PROJECTS, required=True)
        child.add_argument("--selection", choices=("unit", "component", "image"), required=True)
        child.add_argument("--configuration", default="Release")
        child.add_argument("--composition", choices=("full", "no-cloud", "slim"), default="full")
        child.add_argument("--filter", default="")
        child.add_argument("--runtime", choices=("linux-x64", "linux-arm64"))
        child.add_argument("--output", type=Path, required=True)
        if operation == "run":
            child.add_argument("--scenario", choices=SCENARIOS, required=True)
            child.add_argument("--pair", type=int, choices=(1, 2, 3), required=True)
            child.add_argument("--edit-file")
            child.add_argument("--timeout", type=int, default=2400)
            child.add_argument("--host-receipt", type=Path)
            child.add_argument("--builder-json", type=Path)
            child.add_argument("--builder-receipt", type=Path)
    child = sub.add_parser("compare")
    child.add_argument("--receipt", type=Path, action="append", required=True)
    child.add_argument("--selection", choices=("unit", "component"), required=True)
    child.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.operation == "plan":
            result = plan(args.root.resolve(), args.variant, args.selection, args.configuration, args.composition, args.filter, getattr(args, "runtime", None))
            write(args.output, result)
            return 0 if not result["blockers"] else 2
        if args.operation == "identity":
            root = args.root.resolve()
            planned = plan(root, args.variant, args.selection, args.configuration, args.composition, args.filter, getattr(args, "runtime", None))
            if planned["blockers"]:
                raise ValueError("; ".join(planned["blockers"]))
            evaluation_preflight(root, planned["project"], args.configuration, args.composition, planned["runtimeIdentifier"])
            if args.output.exists():
                raise ValueError("Identity evidence destination must be new")
            args.output.mkdir(parents=True)
            for index, entry in enumerate(planned["entryProjects"]):
                stage(f"restore-{index}", ["dotnet", "restore", entry, *props(args.configuration, args.composition, planned["runtimeIdentifier"])], root, args.output, 2400)
            def measured(name, argv):
                return stage(name, argv, root, args.output, 2400)
            identity = evaluated_inputs(root, planned["entryProjects"], args.configuration, args.composition, args.output, measured, planned["runtimeIdentifier"])
            write(args.output / "artifact.json", identity)
            return 0
        if args.operation == "run":
            run(args)
            return 0
        result = compare([json.loads(p.read_text()) for p in args.receipt], args.selection)
        write(args.output, result)
        return 0 if result["passed"] else 2
    except (ValueError, OSError, subprocess.CalledProcessError, KeyError, TypeError) as error:
        print(f"OData pilot failed closed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
