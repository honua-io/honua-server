# Security Configuration

This guide covers secret management, OIDC hardening, and proxy-related security settings.

Audit logging and compliance storage are not implemented in the current build.

## Secret Management

Avoid storing secrets directly in `appsettings*.json`. Use secret references whenever possible.

### Supported Secret References

- **Environment variables**: `env:VARIABLE_NAME`
- **AWS Secrets Manager**: `aws:secretsmanager:<secret-id>?versionStage=...&versionId=...`
- **Azure Key Vault**: `azure:keyvault:<vault>:<secret>[:<version>]`
- **Custom providers** (connection strings and admin password): Implement `IConnectionSecretResolver` and register in the Postgres security extensions.

Production startup validation rejects plaintext secrets in configuration files. Supply secrets via environment variables or `env:` references.

Secret payloads may be stored as:
- a raw connection string
- JSON with `connectionString` / `ConnectionString`
- JSON with `username`, `password`, `host`, and `dbname`/`database` (optional `port`)

### Provider Credential Requirements

- **AWS Secrets Manager**: `AWS_REGION`/`AWS_DEFAULT_REGION` (or secret ARN) plus credentials from
  `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY`, ECS task metadata, or EC2 IMDS. Optional `AWS_SESSION_TOKEN`.
- **Azure Key Vault**: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` or Managed Identity.

### Examples

```bash
# Default connection string via env reference
ConnectionStrings__DefaultConnection=env:HONUA_DB_URL
HONUA_DB_URL="Host=...;Database=...;Username=...;Password=..."

# Default connection string via AWS Secrets Manager
ConnectionStrings__DefaultConnection=aws:secretsmanager:prod-db-credentials

# Default connection string via Azure Key Vault
ConnectionStrings__DefaultConnection=azure:keyvault:honua-vault:prod-db-credentials

# Admin API key via env reference (automation only, not for browser UI)
HONUA_ADMIN_PASSWORD=env:HONUA_ADMIN_PASSWORD_VALUE
HONUA_ADMIN_PASSWORD_VALUE="super-secret"

# OIDC client secret via env reference
Oidc__Generic__ClientSecret=env:OIDC_CLIENT_SECRET
OIDC_CLIENT_SECRET="oidc-secret"

# Redis connection string via env reference
ConnectionStrings__redis=env:HONUA_REDIS_URL
HONUA_REDIS_URL="localhost:6379"
```

### Secure Connection Registry

Admin endpoints support secret references for managed connections (`SecretRef`/`SecretType`).
The registry uses the same secret resolver pipeline as `DefaultConnection`.

## OIDC Bootstrap (Initial Setup)

Honua does not provide an in-app bootstrap flow for OIDC. Configure the IdP and
set environment variables before first startup. Admin UI uses OIDC bearer tokens;
API keys are automation-only.

**Steps:**
1. Register the Admin UI as an OIDC client and the API as a resource/audience.
2. Ensure the IdP issues an admin role/group claim for privileged users.
3. Configure OIDC environment variables and restart the server.

**Azure AD example:**
```bash
OIDC__ENABLED=true
OIDC__AZUREAD__ENABLED=true
OIDC__AZUREAD__TENANTID="your-tenant-id"
OIDC__AZUREAD__CLIENTID="your-client-id"
OIDC__TOKENVALIDATION__VALIDAUDIENCES__0="api://your-client-id"
OIDC__ADMINROLES__0="admin"
```

**Generic OIDC example:**
```bash
OIDC__ENABLED=true
OIDC__GENERIC__ENABLED=true
OIDC__GENERIC__AUTHORITY="https://your-idp"
OIDC__GENERIC__CLIENTID="your-client-id"
OIDC__GENERIC__CLIENTSECRET=env:OIDC_CLIENT_SECRET
OIDC_CLIENT_SECRET="oidc-secret"
OIDC__ADMINROLES__0="admin"
```

If your IdP uses a non-standard roles claim, set:
`OIDC__CLAIMSMAPPING__ROLECLAIMTYPE="groups"` (or the claim name your IdP uses).

## OIDC Token Replay Protection

Enable token replay protection to reject repeated use of the same JWT.

```bash
# Enable token replay protection
OIDC__TOKENVALIDATION__ENABLETOKENREPLAYPROTECTION=true
OIDC__TOKENVALIDATION__TOKENREPLAYCACHEDURATION=00:10:00
```

- **Cache scope**: Uses `IDistributedCache` when available (e.g. Redis), falling back to `IMemoryCache` (per instance). In multi-instance deployments, configure a shared distributed cache.
- **Token IDs**: Uses `jti` when present, otherwise the raw token hash.

## Forwarded Headers and Public Base URL

When running behind a reverse proxy/load balancer:

```bash
# Forwarded headers configuration
FORWARDEDHEADERS__ENABLED=true
FORWARDEDHEADERS__FORWARDLIMIT=1
FORWARDEDHEADERS__KNOWNPROXIES__0=10.0.0.10

# Public base URL for link generation
PUBLIC__BASEURL=https://api.honua.example.com
```

**Docker Compose example:**
```yaml
services:
  honua:
    environment:
      - FORWARDEDHEADERS__ENABLED=true
      - FORWARDEDHEADERS__FORWARDLIMIT=1
      - FORWARDEDHEADERS__KNOWNPROXIES__0=10.0.0.10
      - PUBLIC__BASEURL=https://api.honua.example.com
```

- **`ForwardedHeaders`** controls trusted proxy header processing.
- **`Public:BaseUrl`** (or `PUBLIC_BASE_URL`) forces correct link generation in OGC/OData responses.
- **Rate limiting** should be enforced at the edge (nginx/ALB/API gateway); see ADR-0004 for the decision.
- **Standalone deployments** should keep forwarded headers disabled.

## Storage and Monitoring Secrets

Use environment references for any storage or monitoring credentials:

```bash
# File storage secrets
FileStorage__AwsS3__AccessKeyId=env:HONUA_S3_KEY_ID
FileStorage__AwsS3__SecretAccessKey=env:HONUA_S3_SECRET
FileStorage__AzureBlob__ConnectionString=env:HONUA_AZURE_BLOB_CONN

# Monitoring alerting secrets
Monitoring__IntelligentAlerting__NotificationChannels__Email__Password=env:HONUA_SMTP_PASSWORD
Monitoring__IntelligentAlerting__NotificationChannels__Slack__WebhookUrl=env:HONUA_SLACK_WEBHOOK
Monitoring__IntelligentAlerting__NotificationChannels__Webhook__Url=env:HONUA_ALERT_WEBHOOK
Monitoring__IntelligentAlerting__NotificationChannels__Webhook__Headers__Authorization=env:HONUA_ALERT_WEBHOOK_AUTH
Monitoring__IntelligentAlerting__NotificationChannels__Sms__ApiKey=env:HONUA_SMS_API_KEY
```

## Rate Limiting

Honua does not implement application-level rate limiting. Rate limiting should be
enforced at the edge infrastructure layer (reverse proxy, load balancer, or API gateway).

### Recommended Approach

| Deployment | Recommended Tool | Notes |
|---|---|---|
| Docker / self-hosted | nginx `limit_req` / Caddy `rate_limit` | Closest to the application |
| AWS ECS / Fargate | ALB request-rate rules or AWS WAF | Managed, scales automatically |
| AWS Lambda | API Gateway throttling + WAF | Per-stage and per-key limits |
| Azure Container Apps | Azure Front Door / WAF policies | Regional rate limiting |
| Azure Functions | Azure API Management or Front Door | Per-subscription throttling |
| Kubernetes | Ingress controller (e.g. nginx-ingress `limit-rps`) | Cluster-level enforcement |

### Configuration Guidance

At minimum, operators should configure:

1. **Global request rate** - cap total requests per IP (e.g. 100 req/s).
2. **Authentication endpoint rate** - stricter limits on `/admin/login` and token endpoints (e.g. 5 req/min per IP).
3. **Upload endpoint rate** - limit attachment uploads to prevent storage abuse (e.g. 10 req/min per IP).
4. **Tile endpoint burst** - map tile requests are bursty by nature; allow short bursts (e.g. 200 req/s) but cap sustained throughput.

### nginx Example

```nginx
# Define rate limit zones
limit_req_zone $binary_remote_addr zone=global:10m rate=100r/s;
limit_req_zone $binary_remote_addr zone=auth:10m rate=5r/m;
limit_req_zone $binary_remote_addr zone=upload:10m rate=10r/m;

server {
    # Global rate limit with burst
    limit_req zone=global burst=50 nodelay;

    # Stricter limit on auth endpoints
    location /admin/login {
        limit_req zone=auth burst=3 nodelay;
        proxy_pass http://honua;
    }

    # Stricter limit on uploads
    location ~ ^/layers/.*/attachments {
        limit_req zone=upload burst=5 nodelay;
        proxy_pass http://honua;
    }
}
```

### AWS WAF Example

```json
{
  "Name": "HonuaRateLimit",
  "Priority": 1,
  "Action": { "Block": {} },
  "Statement": {
    "RateBasedStatement": {
      "Limit": 2000,
      "AggregateKeyType": "IP"
    }
  },
  "VisibilityConfig": {
    "SampledRequestsEnabled": true,
    "CloudWatchMetricsEnabled": true,
    "MetricName": "HonuaRateLimit"
  }
}
```

### Design Decision

Application-level rate limiting was intentionally deferred for the MVP. Edge-based
rate limiting is preferred because it:

- Rejects abusive traffic before it reaches the application process
- Avoids adding middleware latency to every request
- Leverages battle-tested infrastructure components
- Scales independently of application instances

See ADR-0004 for the full architectural decision record.
