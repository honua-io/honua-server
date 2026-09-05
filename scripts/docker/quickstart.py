#!/usr/bin/env python3
"""Initialize private per-install credentials and start the local Compose stack."""
import argparse
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[2]
PASSWORD_KEYS = ("POSTGRES_PASSWORD", "MINIO_ROOT_PASSWORD")


def initialize(env_file):
    # Never rotate an existing database password on restart. Preserve unrelated
    # dotenv settings, and require explicit migration of old placeholder values.
    existing = env_file.read_text() if env_file.exists() else ""
    additions = []
    for key in PASSWORD_KEYS:
        match = re.search(rf"^\s*{key}\s*=\s*(.*?)\s*$", existing, re.MULTILINE)
        value = match.group(1).strip("\"'") if match else None
        supplied = os.environ.get(key)
        if supplied is not None:
            if value is not None and supplied != value:
                raise ValueError(f"{key} differs between the environment and the saved env file")
            value = supplied
        if value is not None and (len(value) < 24 or not re.fullmatch(r"[A-Za-z0-9_-]+", value)):
            raise ValueError(f"{key} must contain at least 24 letters, digits, underscores or hyphens; migrate existing credentials explicitly")
        if not match:
            additions.append(f"{key}={value or secrets.token_hex(32)}\n")
    if additions:
        # Exclusive creation prevents two fresh bootstraps from silently choosing
        # different credentials. Existing files retain their non-secret settings.
        flags = os.O_WRONLY | os.O_CREAT | (os.O_APPEND if env_file.exists() else os.O_EXCL)
        fd = os.open(env_file, flags, 0o600)
        with os.fdopen(fd, "a") as stream:
            os.chmod(env_file, 0o600)
            if existing and not existing.endswith("\n"):
                stream.write("\n")
            stream.writelines(additions)
    else:
        os.chmod(env_file, 0o600)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--env-file", type=Path, default=ROOT / ".env")
    parser.add_argument("--init-only", action="store_true")
    args, compose_args = parser.parse_known_args()
    try:
        initialize(args.env_file)
    except (OSError, ValueError) as error:
        # Error messages identify the key or file operation, never a secret value.
        print(f"Quickstart initialization failed: {error}", file=sys.stderr)
        return 1
    if args.init_only:
        return 0
    return subprocess.call(["docker", "compose", "--env-file", str(args.env_file.resolve()),
                            *compose_args, "up", "-d", "--wait", "--wait-timeout", "180"], cwd=ROOT)


if __name__ == "__main__":
    sys.exit(main())
