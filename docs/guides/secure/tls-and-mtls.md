# Terminate TLS and require client certificates

Encrypt every hop — browser to edge, edge to Honua, Honua to PostgreSQL — and optionally require mTLS client certificates on admin and native surfaces.

**Prerequisites:** A reverse proxy or load balancer you control (nginx, ALB, Application Gateway, ingress), and an admin API key for the trust-management endpoints.

## Steps

### 1. Terminate TLS at the edge

Terminate TLS at your proxy/load balancer — Honua binds plain HTTP behind it — then tell Honua about the proxy so client IPs and public URLs resolve correctly:

```bash
ForwardedHeaders__Enabled=true
ForwardedHeaders__ForwardLimit=2
ForwardedHeaders__KnownProxies__0=10.0.0.10
PUBLIC_BASE_URL=https://gis.example.com
SecurityHeaders__EnableHsts=true
SecurityHeaders__HstsMaxAge=31536000
```

`KnownProxies` must list only trusted hops; `PUBLIC_BASE_URL` drives absolute links and `Location` headers (the request `Host` header is never reflected). Direct Kestrel HTTPS is supported, but the proxy-first pattern keeps certificate rotation out of the app.

### 2. Encrypt the database connection

Honua connects to PostgreSQL via Npgsql. In production use full verification:

```bash
ConnectionStrings__DefaultConnection="Host=db.example.com;Port=5432;Database=honua;Username=honua_app;Password=$DB_PASSWORD;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/rds-ca.pem;Trust Server Certificate=false"
```

| SSL mode | Use |
|---|---|
| `Require` | Encrypted, no cert verification — internal/staging only |
| `VerifyCA` | Verifies the server CA — managed services with custom CAs |
| `VerifyFull` | Verifies CA and hostname — production |

For AWS RDS/Aurora download the bundle from `https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem`; for Azure Flexible Server use DigiCert Global Root G2 (`https://cacerts.digicert.com/DigiCertGlobalRootG2.crt.pem`). Mount the file into the container and reference it via `Root Certificate`.

### 3. Choose an mTLS enforcement mode

Client-certificate authentication is server-side validation of the *caller's* certificate, aimed at native operator clients and the admin surface:

```bash
Authentication__ClientCertificates__Mode=RequiredForAdmin
```

| Mode | Behavior |
|---|---|
| `Disabled` | Ignore client certificates (default) |
| `Optional` | A valid mapped certificate is one more admin auth scheme; API key/OIDC still work |
| `RequiredForAdmin` | Certificate required for admin path prefixes (default `/api/v1/admin`) |
| `RequiredForNative` | Certificate required for the native gRPC services |
| `RequiredForEnvironment` | Both admin and native surfaces |

### 4. Define a trust profile and principal mapping

A trust profile says which issuers to trust; principal mappings turn a certificate SAN into a Honua principal with roles:

```bash
Authentication__ClientCertificates__TrustProfiles__0__ProfileId=prod-native
Authentication__ClientCertificates__TrustProfiles__0__EnvironmentId=prod
Authentication__ClientCertificates__TrustProfiles__0__AcceptedIssuerSubjects__0=CN=Honua Prod Operator CA
Authentication__ClientCertificates__TrustProfiles__0__RequireChainTrust=true
Authentication__ClientCertificates__TrustProfiles__0__AllowedSanTypes__0=SanUri
Authentication__ClientCertificates__TrustProfiles__0__PrincipalMappings__0__MappingId=prod-console-admin
Authentication__ClientCertificates__TrustProfiles__0__PrincipalMappings__0__MatchType=SanUri
Authentication__ClientCertificates__TrustProfiles__0__PrincipalMappings__0__MatchValue=spiffe://honua/prod/console-admin
Authentication__ClientCertificates__TrustProfiles__0__PrincipalMappings__0__PrincipalId=native-prod-admin
Authentication__ClientCertificates__TrustProfiles__0__PrincipalMappings__0__Roles__0=admin
```

`AcceptedIssuerSubjects` alone is forgeable, so the server requires `RequireChainTrust=true` with it; for a private CA not in the OS trust store, add `CustomTrustAnchorCertificates`. Prefer SAN URI/email mappings over subject fallback. The mapped principal must still satisfy admin RBAC.

### 5. Forward certificates from a trusted proxy (optional)

If TLS (including the client handshake) terminates at the proxy, enable forwarded certificates only when the proxy strips inbound spoofed headers:

```bash
Authentication__ClientCertificates__ForwardedCertificate__Enabled=true
Authentication__ClientCertificates__ForwardedCertificate__HeaderName=X-Forwarded-Client-Cert
Authentication__ClientCertificates__ForwardedCertificate__TrustedProxyNetworks__0=10.0.0.0/24
```

Headers arriving from outside `TrustedProxyNetworks` are rejected with `client_certificate_forwarding_untrusted`.

### 6. Manage trust at runtime

Profiles, mappings, and revocations are also manageable without restarts under `/api/v1/admin/security/client-certificates/*` (list/create/update/delete profiles, `.../mappings`, `.../revocations` by SHA-256 fingerprint or issuer+serial). Mirror admin mutations back into your configuration source so they survive restarts and multi-node rollout.

## Verify

Probe a certificate without storing it:

```bash
curl -X POST "https://gis.example.com/api/v1/admin/security/client-certificates/validate" \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" -H "Content-Type: application/json" \
  -d "{\"certificate\":\"$(base64 -w0 client.der)\",\"encoding\":\"base64Der\"}"
```

```json
{ "success": true, "data": { "valid": true, "profileId": "prod-native" } }
```

Untrusted certificates return `200` with `data.valid=false` and a stable `data.code`. Then hit a protected admin route with and without the certificate: failures return `401` problem details with codes like `client_certificate_missing`.

## Troubleshoot

| Symptom | Fix |
|---|---|
| `client_certificate_untrusted_issuer` / `_untrusted_chain` | The chain must build cryptographically; add intermediates or configure `CustomTrustAnchorCertificates` for a private CA. |
| `client_certificate_forwarding_untrusted` behind a proxy | The immediate peer IP is outside `TrustedProxyNetworks`; when `ForwardedHeaders__Enabled=true` Honua checks the pre-rewrite peer IP, so list the proxy's real address. |
| Profile rejected at startup or upsert (`400`) | Enabled profiles need an issuer subject, thumbprint, or custom anchor — and `AcceptedIssuerSubjects` requires `RequireChainTrust=true`. |
| Browser/gRPC-Web users prompted for certificates | They shouldn't be: required native modes exempt `application/grpc-web*` requests; only native HTTP/2 gRPC and admin paths are enforced. |
| Native gRPC mTLS fails locally | mTLS-required modes need HTTPS/HTTP2 to Kestrel (or trusted TLS termination); local h2c ports cannot satisfy them. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Production security checklist](production-checklist.md)
- [Authenticate clients](authentication.md)
