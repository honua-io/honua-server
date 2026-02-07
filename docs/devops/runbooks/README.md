# Runbooks

Use these playbooks for production incidents. Each runbook is intentionally short: stabilize first, diagnose second, and document after.

## Available Runbooks

- [Server Down](server-down.md)
- [Health Check Failure](health-check-failure.md)
- [High Error Rate](high-error-rate.md)
- [High Response Time](high-response-time.md)
- [High CPU Usage](high-cpu-usage.md)
- [High Memory Usage](high-memory-usage.md)
- [Database Issues](database-issues.md)
- [Security Incidents](security-incidents.md)

## General Rules

- Restore service before full root-cause analysis.
- Prefer safe, reversible changes.
- Capture timelines and key commands for the post-incident review.
