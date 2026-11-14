"""Bounded, secret-redacted failure evidence for the manual Compose journey."""
import json
import re
import subprocess

from compose_quickstart import ALL_SECRET_KEYS, read_secret


def capture(compose, env, arguments):
    """Diagnostic failures must never replace the readiness failure."""
    try:
        result = subprocess.run(compose + arguments, env=env, text=True,
                                capture_output=True, timeout=15, check=False)
        return result.stdout if result.returncode == 0 else ""
    except (OSError, UnicodeError, subprocess.TimeoutExpired):
        return ""


def redact(text, values):
    for value in sorted(set(values), key=len, reverse=True):
        if value:
            text = text.replace(value, "[redacted]")
    text = re.sub(r"(?i)((?:password|token|secret|key)\s*[=:]\s*)([^;\s]+)",
                  r"\1[redacted]", text)
    return re.sub(r"[A-Za-z0-9+/=_-]{32,}", "[redacted]", text)


def readiness_diagnostics(compose, env, install):
    # Fail closed if the complete generated-secret set cannot be read. Never
    # print config, inspect environment, or diagnostic command stderr.
    try:
        values = [read_secret(install, key) for key in ALL_SECRET_KEYS]
    except (OSError, UnicodeError):
        return "readiness diagnostics unavailable (secret inventory unreadable)"
    states = capture(compose, env, ["ps", "--all", "--format", "json"])
    try:
        rows = json.loads(states) if states.lstrip().startswith("[") else [
            json.loads(line) for line in states.splitlines() if line.strip()]
        states = json.dumps([{key: row.get(key) for key in
                              ("Service", "State", "Health", "ExitCode")} for row in rows])
    except (ValueError, TypeError, AttributeError):
        states = "unavailable"
    logs = capture(compose, env, ["logs", "--no-color", "--tail", "80", "honua"])
    errors = [redact(line, values)[:300] for line in logs.splitlines()
              if re.search(r"exception|critical|fail|error", line, re.I)]
    return f"services={redact(states, values)[:2000]}; server errors={errors[-8:]}"
