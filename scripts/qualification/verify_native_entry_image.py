"""Verify actual image ogr2ogr exec is held before main; never AWS qualification."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import time
import uuid


def docker(*arguments):
    return subprocess.run(["docker", *arguments], check=True, text=True, capture_output=True, timeout=40).stdout.strip()


def verify(image, source):
    identity = json.loads(docker("image", "inspect", image))[0]
    if identity["Config"]["Labels"].get("org.opencontainers.image.revision") != source:
        raise RuntimeError("worker image source mismatch")
    if identity["Config"]["User"] != "1001:1001":
        raise RuntimeError("native image user changed")
    expected_executable = docker("run", "--rm", "--read-only", "--network", "none",
                                 "--entrypoint", "/usr/bin/readlink", image, "-f", "/usr/bin/ogr2ogr")
    observations = []
    for cancel in (False, True):
        with tempfile.TemporaryDirectory(prefix="honua-native-entry-image-") as temporary:
            root = Path(temporary).resolve()
            root.chmod(0o777)  # only this throwaway bind, for the unchanged image UID1001
            name, nonce = "honua-entry-" + uuid.uuid4().hex, uuid.uuid4().hex
            mount = "/run/honua/entry-control"
            try:
                docker("run", "--detach", "--name", name, "--read-only", "--network", "none",
                       "--mount", f"type=bind,source={root},target={mount}",
                       "--env", "LD_PRELOAD=/app/libhonua_gp_entry.so",
                       "--env", f"HONUA_GP_NATIVE_ENTRY_RECORD={mount}/entry.json",
                       "--env", f"HONUA_GP_NATIVE_ENTRY_RELEASE={mount}/release",
                       "--env", f"HONUA_GP_NATIVE_ENTRY_NONCE={nonce}",
                       "--entrypoint", "/usr/bin/ogr2ogr", image, "--version")
                deadline = time.monotonic() + 15
                while not (root / "entry.json").exists():
                    if time.monotonic() >= deadline or docker("inspect", "--format", "{{.State.Running}}", name) != "true":
                        raise RuntimeError("actual image native entry missing")
                    time.sleep(0.05)
                # Docker copies UID1001's private record as the invoking host user;
                # no permission or image-user relaxation is needed to read it.
                copied = root / "copied-entry.json"
                docker("cp", f"{name}:{mount}/entry.json", str(copied))
                entry = json.loads(copied.read_text())
                if (entry.get("schema") != "honua.gp-native-entry/v1" or entry.get("boundary") != "native-exec-entry-paused"
                        or entry.get("pid") != 1 or entry.get("nonce") != nonce
                        or entry.get("executable") != expected_executable):
                    raise RuntimeError("image native entry identity mismatch")
                time.sleep(0.2)
                if docker("inspect", "--format", "{{.State.Running}}", name) != "true" or "GDAL" in docker("logs", name):
                    raise RuntimeError("fast native main ran before release")
                if cancel:
                    docker("kill", "--signal", "TERM", name)
                    exit_code = int(docker("wait", name))
                    if exit_code == 0 or "GDAL" in docker("logs", name):
                        raise RuntimeError("cancelled native entry reached main")
                else:
                    (root / "release").write_text(nonce)
                    exit_code = int(docker("wait", name))
                    if exit_code != 0 or "GDAL" not in docker("logs", name):
                        raise RuntimeError("released native main did not execute")
                observations.append({"cancelled": cancel, "entry": entry, "exitCode": exit_code})
            finally:
                subprocess.run(["docker", "rm", "--force", name], capture_output=True, timeout=20)
    return {"schema": "honua.native-entry-image-control/v1", "imageId": identity["Id"],
            "sourceSha": source, "qualification": False, "observations": observations}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    receipt = verify(args.image, args.source_sha)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
