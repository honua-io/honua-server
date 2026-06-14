# Harden a production deployment

Work through this checklist before exposing Honua to the internet; each item is enforced or configured by a setting you can verify in `.env.production.example`.

**Prerequisites:** A deployment you can restart with new environment variables, and an edge proxy/load balancer in front of the server (see [Deployment scenarios](../deploy/cloud-deployments.md)).

## Checklist

### Secrets and authentication

- [ ] `HONUA_ADMIN_PASSWORD` is set to a strong value from a secret manager — it is the root admin credential and is required for production admin access.
- [ ] CI and integrations use scoped, expiring API keys (`POST /api/v1/admin/api-keys`), not the admin password — rotate with `POST /api/v1/admin/api-keys/{id}/rotate` ([Authenticate clients](authentication.md)).
- [ ] OIDC client secrets and database credentials are injected from your secret manager, never committed; rotate on a schedule.
- [ ] `Security__ConnectionEncryption__MasterKey` (secure connection registry cipher key) comes from a secret store; rotating it requires a redeploy.
- [ ] Registered data-source connections reference secrets by provider reference (`provider` + `ref`, e.g. `env` or `azure-key-vault`) in metadata connection secrets instead of inlining credentials; validate with `GET /api/v1/admin/configuration/secrets/validate`.
- [ ] `HONUA_DEV_AUTH` is **unset** — in production it blocks startup, and the only valid use is the in-process `Test` environment.

### Transport and edge

- [ ] TLS terminates at your reverse proxy or load balancer, and `SecurityHeaders__EnableHsts=true` with `SecurityHeaders__HstsMaxAge=31536000` — see [TLS and mTLS](tls-and-mtls.md).
- [ ] `ForwardedHeaders__Enabled=true` with `ForwardedHeaders__KnownProxies__0` listing each trusted hop, and `PUBLIC_BASE_URL=https://gis.example.com` so absolute links and `Location` headers carry the public hostname.
- [ ] PostgreSQL connection string uses `SSL Mode=VerifyFull` (or at minimum `Require`) with `Trust Server Certificate=false`.
- [ ] `Cors__AllowedOrigins__0..n` is an explicit allowlist of your apps' origins and `Cors__AllowCredentials=false` unless cookies are truly required.
- [ ] Rate limiting is enforced at the edge (WAF/ALB/Application Gateway) — application-level rate limiting is deferred for MVP, so the server will not throttle for you.
- [ ] `/api/v1/admin/*`, `/metrics`, and `/monitoring/*` are additionally restricted at the edge (network allowlist or VPN); they require admin auth in-app but should not be internet-reachable.
- [ ] A CSP for any hosted UI is rolled out at the edge, report-only first — violations land on `POST /csp-violation-report`.

### Limits and upload/import posture

- [ ] Query and geometry limits are production-tuned (`Limits__Query__MaxRecordCount`, `Limits__Query__QueryTimeout`, `Limits__Geometry__MaxGeometrySize`, …).
- [ ] Attachment limits fit your use case (`Limits__Attachments__MaxAttachmentSize`, `MaxAttachmentsPerFeature`, `AllowedMimeTypes` — defaults 5 MB / 5 per feature / `image/*,application/pdf`).
- [ ] Import upload limits reviewed via `GET /api/v1/admin/import/limits`.
- [ ] SSRF posture for remote imports understood: URL-based import sources must be HTTPS, without embedded credentials, resolving to public addresses on recognized S3/Azure object-storage hosts — private and loopback targets are rejected server-side. Outbound webhook URLs get the same validation. Keep network egress policies as defense in depth.

### Demo and development surfaces

- [ ] `HONUA_SERVE_API_DOCS` is unset or `false` — the `/docs` interactive API explorer defaults to Development-only; do not enable it in production.
- [ ] `HONUA_SERVE_STAC_DEMO=false` so the hosted STAC demo at `/samples/stac-ops` is not served.
- [ ] `HONUA_ENABLE_OBSERVABILITY_TEST_SEED` is unset (setting it in production fails startup).

## Verify

```bash
curl -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  "https://gis.example.com/api/v1/admin/configuration/secrets/validate"
```

Also confirm from outside the network: `curl -I https://gis.example.com/docs` returns `404`, `/metrics` is unreachable, and responses carry `Strict-Transport-Security`.

## Troubleshoot

| Symptom | Fix |
|---|---|
| Server refuses to start after hardening | Startup configuration validation rejects contradictions (e.g. `HONUA_DEV_AUTH` in production, Basic compat without HTTPS); the log names the offending key. |
| Redirects/links point at an internal host | Set `PUBLIC_BASE_URL` and verify forwarded headers from the proxy. |
| Browser apps suddenly blocked | The origin is missing from `Cors__AllowedOrigins__*` — entries are exact origins (scheme + host + port). |
| Remote import rejected as disallowed address | Working as intended for private/loopback URLs; host the file on supported public object storage or upload it directly. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [TLS and mTLS](tls-and-mtls.md)
- [Control access to services and layers](access-control.md)
- [Check compliance posture](compliance.md)
