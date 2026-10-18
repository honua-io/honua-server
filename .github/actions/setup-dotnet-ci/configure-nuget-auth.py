#!/usr/bin/env python3
"""Pass ephemeral NuGet source credentials through the runner environment only."""
from __future__ import annotations

import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

SOURCE = "github-honua"
CREDENTIAL = "NuGetPackageSourceCredentials_" + SOURCE
FEED = "https://nuget.pkg.github.com/honua-io/index.json"


class ConfigurationError(Exception):
    pass


def configured(root: Path) -> bool:
    for name in ("NuGet.Config", "NuGet.config"):
        path = root / name
        if not path.is_file():
            continue
        try:
            document = ET.fromstring(path.read_bytes())
        except (OSError, ET.ParseError):
            raise ConfigurationError("Cannot read NuGet package-source configuration.") from None
        sources = {}
        for item in document.findall("./packageSources/*"):
            if item.tag == "clear":
                sources.clear()
            elif item.tag == "remove":
                sources.pop(item.get("key"), None)
            elif item.tag == "add":
                sources[item.get("key")] = item.get("value")
        if SOURCE in sources:
            if sources[SOURCE] != FEED:
                raise ConfigurationError("Named GitHub package source has an unexpected endpoint.")
            return True
    return False


def configure(root: Path, environment: dict[str, str]) -> bool:
    root = root.resolve()
    if not configured(root):
        return False
    username = environment.get("HONUA_NUGET_USERNAME", "")
    token = environment.get("HONUA_NUGET_TOKEN", "")
    if any(not value or any(c in value for c in ";\r\n\0") for value in (username, token)):
        raise ConfigurationError("GitHub package authentication inputs are missing or malformed.")
    output = environment.get("GITHUB_ENV", "")
    if not output:
        raise ConfigurationError("Runner environment file is unavailable.")
    path = Path(output).resolve()
    if path.is_relative_to(root):
        raise ConfigurationError("Runner credentials must remain outside the source checkout.")
    try:
        # NuGet's documented source-name credential environment has precedence
        # over config-file credentials. Leave source mapping/config discovery to
        # NuGet; never rewrite the tracked config or create a parallel feed list.
        with path.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(f"{CREDENTIAL}=Username={username};Password={token};ValidAuthenticationTypes=Basic\n")
    except OSError:
        raise ConfigurationError("Cannot write the runner credential environment.") from None
    return True


def main() -> int:
    try:
        active = configure(Path.cwd(), dict(os.environ))
    except ConfigurationError as error:
        # All diagnostics are fixed messages, never exception/input/config dumps.
        print(f"::error::{error}", file=sys.stderr)
        return 1
    print("GitHub Packages credentials configured in the runner environment." if active
          else "GitHub package source is absent; credential environment unchanged.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
