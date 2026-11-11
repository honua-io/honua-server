"""Read solution project paths with normalized lookup keys."""

from pathlib import Path
import re


def solution_projects(root: Path) -> dict[str, str]:
    return {path.replace("\\", "/"): path for path in re.findall(
        r'^Project\([^\n]+? = "[^"]+", "([^"]+\.csproj)"',
        (root / "Honua.sln").read_text(), re.MULTILINE)}
