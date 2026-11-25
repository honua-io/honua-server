"""Diagnostic rules, signal models, and secret redaction for Operator Doctor."""

from __future__ import annotations

import re
from dataclasses import dataclass


@dataclass(frozen=True)
class DiagnosticRule:
    """Deterministic diagnostic matcher and operator remediation recipe."""

    rule_id: str
    category: str
    patterns: tuple[str, ...]
    root_cause: str
    remediation: tuple[str, ...]


@dataclass(frozen=True)
class SignalInput:
    """Normalized health probe and startup log signal."""

    healthz_status: int
    healthz_body: str
    headers: dict[str, str]
    log_lines: list[str]


@dataclass(frozen=True)
class DoctorDiagnosis:
    """Sanitized diagnosis produced by the Operator Doctor."""

    matched_rule: str
    category: str
    root_cause: str
    remediation: list[str]


GLOBAL_FORBIDDEN_PATTERNS: tuple[str, ...] = (
    r"Password=[^;\s]+",
    r"HONUA_ADMIN_PASSWORD=[^\s]+",
    r"AWS_SECRET_ACCESS_KEY=[^\s]+",
    r"BEGIN\s+(?:RSA\s+)?PRIVATE\s+KEY",
    r"Bearer\s+eyJ[A-Za-z0-9_.-]+",
    r"\.cs:line\s+\d+",
    r"\bat\s+(?:System|Microsoft|Npgsql|Honua)\.[A-Za-z0-9_.`]+\(",
)

REDACTION_SUBSTITUTIONS: tuple[tuple[str, str], ...] = (
    (r"(?i)(Password|Pwd|HONUA_ADMIN_PASSWORD|AWS_SECRET_ACCESS_KEY)=[^;\s]+", r"\1=[REDACTED]"),
    (r"(?s)-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", "[REDACTED_KEY]"),
    (r"Bearer\s+[A-Za-z0-9_.-]+", "Bearer [REDACTED]"),
    (r"(?i)(honua_session|api[_-]?key)\s*[:=]\s*[^\s;]+", r"\1=[REDACTED]"),
    (r"\s+at\s+[A-Za-z0-9_.`<>]+\([^)]*\)(?:\s+in\s+[^\s]+:line\s+\d+)?", " [REDACTED_STACK]"),
)

DIAGNOSTIC_RULES: tuple[DiagnosticRule, ...] = (
    DiagnosticRule(
        "wrong_db_password",
        "database",
        (r"\b28P01\b", r"password authentication failed"),
        "PostgreSQL rejected database authentication with SQLSTATE 28P01 due to an invalid password or username.",
        (
            "Update ConnectionStrings__DefaultConnection with valid PostgreSQL credentials.",
            "Verify credentials against the target role and rotate the database password in your secret store.",
            "Restart Honua Server and confirm /healthz/ready returns 200 Ready.",
        ),
    ),
    DiagnosticRule(
        "missing_postgis_extension",
        "database",
        (r'extension\s+"postgis"\s+is\s+not\s+available', r"postgis\.control", r"0A000.*postgis"),
        "Startup database migration failed because the required PostGIS extension is missing on the PostgreSQL host.",
        (
            "Install PostGIS on the database server or switch to the postgis/postgis container image.",
            "Run CREATE EXTENSION IF NOT EXISTS postgis; in the target database as a privileged role.",
            "Re-run startup migrations and verify /healthz/ready transitions to Ready.",
        ),
    ),
    DiagnosticRule(
        "forwarded_headers_misconfigured",
        "networking",
        (r"untrusted-forwarded-headers", r"X-Forwarded-Proto.*untrusted peer", r"ForwardedHeaders:KnownNetworks"),
        "Reverse proxy forwarded headers were ignored because ForwardedHeaders is disabled or peer IP is outside ForwardedHeaders:KnownNetworks.",
        (
            "Set ForwardedHeaders__Enabled=true and add your proxy CIDR to ForwardedHeaders__KnownNetworks__0.",
            "Configure PUBLIC_BASE_URL to the canonical external HTTPS origin for generated links.",
            "Verify the reverse proxy forwards X-Forwarded-Proto and X-Forwarded-Host from a trusted hop.",
        ),
    ),
    DiagnosticRule(
        "expired_license",
        "licensing",
        (
            r"License is expired\.",
            r"licenseId=[^\s]+.*expiresAt=",
            r"License unavailable or expired",
            r"validationState[=:\s\"']+Expired",
        ),
        "The configured Honua license is expired and invalid; paid operations (including reads and exports) have stopped and return HTTP 402 until a valid license is installed.",
        (
            "Renew or replace the license and update the HONUA_LICENSE secret or mounted license file.",
            "Wait up to one minute for automatic license re-validation or restart Honua Server to load the replacement immediately.",
            "Confirm /healthz/metrics reports validationState=Valid and isValid=true for the renewed license.",
        ),
    ),
    DiagnosticRule(
        "temp_storage_disk_full",
        "storage",
        (r"\bENOSPC\b", r"No space left on device", r"temp storage volume at 100%"),
        "Temporary staging storage is out of disk space (ENOSPC), blocking uploads and geoprocessing scratch writes.",
        (
            "Free disk space on the temp storage volume and prune orphaned staging files.",
            "Expand the mounted temp volume capacity or configure a larger scratch volume.",
            "Check /healthz/ready and FileUploadHealthCheck once disk utilization drops below 80%.",
        ),
    ),
    DiagnosticRule(
        "invalid_mtls_trust_anchor",
        "security",
        (r"UntrustedRoot", r"PartialChain", r"mTLS trust anchor"),
        "Mutual TLS client certificate validation failed with UntrustedRoot/PartialChain against the configured mTLS trust anchor.",
        (
            "Verify the configured CA bundle includes the issuing root and intermediate CA certificates.",
            "Update the mTLS trust anchor file and confirm the client presents a complete certificate chain.",
            "Validate TLS handshakes and reload the server trust store.",
        ),
    ),
    DiagnosticRule(
        "port_already_in_use",
        "runtime",
        (r"\bEADDRINUSE\b", r"address already in use", r"Failed to bind to address"),
        "Kestrel failed to bind its listening port because another process already occupies the address (EADDRINUSE).",
        (
            "Inspect listening sockets with ss -tlnp to identify the conflicting process on the port.",
            "Stop the conflicting service or reassign Honua via ASPNETCORE_URLS to an available port.",
            "Restart Honua Server and verify /healthz/live responds with Healthy.",
        ),
    ),
    DiagnosticRule(
        "locked_startup_migration",
        "database",
        (r"migration advisory lock", r"migrations-in-progress", r"migration lock timed out"),
        "Startup schema migration is blocked waiting for the PostgreSQL advisory lock held by another replica or stalled session.",
        (
            "Check active advisory locks in PostgreSQL using pg_locks and pg_stat_activity.",
            "Allow the active replica migration to finish or terminate any orphaned lock-holding session.",
            "Monitor /healthz/ready until X-Honua-Readiness-Reason clears from migrations-in-progress.",
        ),
    ),
    DiagnosticRule(
        "cors_origin_blocked",
        "networking",
        (r"CORS preflight rejected", r"Cors:AllowedOrigins", r"CorsService"),
        "Browser cross-origin request was blocked because the caller origin is absent from Cors:AllowedOrigins.",
        (
            "Add the trusted client origin to Cors__AllowedOrigins__0 in server configuration.",
            "Verify preflight OPTIONS responses return Access-Control-Allow-Origin for the origin.",
            "Avoid wildcard origins when credentialed requests are enabled.",
        ),
    ),
    DiagnosticRule(
        "object_store_unreachable",
        "storage",
        (r"ObjectStorageGeoprocessingOutputStoreHealthCheck", r"Object storage endpoint.*unreachable", r"Bucket .* probe failed"),
        "Configured object storage endpoint or bucket is unreachable, causing artifact and geoprocessing output probes to fail.",
        (
            "Verify CloudStorage endpoint URL, bucket name, and network connectivity from the host.",
            "Check DNS resolution, firewall rules, and object store health for 503/connection errors.",
            "Confirm /healthz/ready clears once object storage connectivity is restored.",
        ),
    ),
)


def sanitize_diagnostic_text(text: str) -> str:
    """Redact credentials, private keys, tokens, and stack frames from text."""
    cleaned = text
    for pattern, replacement in REDACTION_SUBSTITUTIONS:
        cleaned = re.sub(pattern, replacement, cleaned)
    return cleaned


def build_signal_corpus(signal: SignalInput) -> str:
    """Combine probe status, headers, body, and logs into a searchable string."""
    header_lines = [f"{key}: {value}" for key, value in sorted(signal.headers.items())]
    parts = [f"status={signal.healthz_status}", signal.healthz_body, *header_lines, *signal.log_lines]
    return "\n".join(part for part in parts if part)


def _score_rule(rule: DiagnosticRule, corpus: str) -> int:
    """Count how many diagnostic regexes match the signal corpus."""
    return sum(1 for pattern in rule.patterns if re.search(pattern, corpus, re.IGNORECASE))


def diagnose_signal(signal: SignalInput) -> DoctorDiagnosis:
    """Classify a broken deployment signal into a sanitized diagnosis."""
    corpus = build_signal_corpus(signal)
    best_rule: DiagnosticRule | None = None
    best_hits = 0

    for rule in DIAGNOSTIC_RULES:
        hits = _score_rule(rule, corpus)
        if hits > best_hits:
            best_hits = hits
            best_rule = rule

    if best_rule is None:
        return DoctorDiagnosis(
            matched_rule="unknown",
            category="unknown",
            root_cause="Unable to classify deployment fault from available health signals.",
            remediation=["Inspect /healthz/ready, /healthz/deep, and recent structured server logs."],
        )

    return DoctorDiagnosis(
        matched_rule=best_rule.rule_id,
        category=best_rule.category,
        root_cause=sanitize_diagnostic_text(best_rule.root_cause),
        remediation=[sanitize_diagnostic_text(step) for step in best_rule.remediation],
    )


def find_secret_leaks(diagnosis: DoctorDiagnosis, extra_patterns: list[str]) -> list[str]:
    """Return every forbidden secret or stack-trace pattern found in output text."""
    output_text = "\n".join([diagnosis.root_cause, *diagnosis.remediation])
    patterns = [*GLOBAL_FORBIDDEN_PATTERNS, *extra_patterns]
    return [pattern for pattern in patterns if re.search(pattern, output_text, re.IGNORECASE)]
