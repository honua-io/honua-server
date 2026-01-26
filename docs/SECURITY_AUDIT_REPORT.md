# Security Audit Report

Date: 2026-01-26
Scope: Issue #39 (Security hardening and input validation)

## Summary
- XSS prevention applied to HTML responses (title + JSON encoding) for OGC API Features metadata and feature outputs.
- Route parameter validation now uses `IRouteParameterValidator` for OGC collection/feature IDs and FeatureServer attachment service/layer IDs.
- applyEdits attribute length enforcement is active via schema `FieldDefinition.Length` checks, with a maximum supported length of 8000 for string fields.
- Request payload sizes are bounded by `Limits.Edits.MaxPayloadSize` (defense in depth for large documents).
- Security headers middleware is enabled in the request pipeline.

## Dependency Vulnerability Scan
- Workflow: Security Scans
- Run: 21328234554 (success)
- Timestamp: 2026-01-25T06:26:18Z
- Run URL: https://github.com/honua-io/honua-server/actions/runs/21328234554
- Result: No high/critical vulnerabilities reported in the nightly scan.

## Security Testing Evidence
- 2026-01-26: `dotnet test tests/Honua.Server.Tests/Honua.Server.Tests.csproj --filter FullyQualifiedName~SecurityComplianceTests`
- Result: Passed 16 / 16 tests

## Notes and Decisions
- HTML response templates use `WebUtility.HtmlEncode` for both page titles and JSON payloads to prevent XSS in `f=html` outputs.
- String attribute length validation is enforced where schema length is defined; fields without an explicit length map to database `TEXT` and are limited by payload size constraints.
