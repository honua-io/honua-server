# Rate Limiting

Honua Server **does not include application-level rate limiting** (see [ADR-0004](../contributor/adr/0004-proxy-rate-limiting.md)). Enforce limits at the edge where traffic is centralized.

This guide intentionally focuses on the checked-in **AWS** and **Azure** templates.

---

## Template Locations

| Platform | Template | Purpose |
|---|---|---|
| AWS | `docs/devops/examples/aws-waf-rate-limit.tf` | ALB + WAFv2 rate-limit rules and ALB association |
| Azure | `docs/devops/examples/azure-application-gateway-waf-rate-limit-policy.json` | Application Gateway WAF custom rate-limit rules |

---

## Recommended Starting Limits

Tune these per environment after observing real traffic.

| Endpoint Pattern | Suggested Rate |
|---|---|
| `/rest/services/*/query` | 200 req/min/IP |
| `/rest/services/*/FeatureServer` | 300 req/min/IP |
| `/ogc/collections/*/items` | 200 req/min/IP |
| `/odata/*/Features` | 200 req/min/IP |
| `/rest/services/*/MapServer/export` | 100 req/min/IP |
| `/ogc/collections/*/tiles` | 500 req/min/IP |
| `/admin/*` | 30 req/min/IP |
| `/healthz/*` | Exempt |
| `/metrics` | Exempt |

---

## AWS Option (ALB + WAFv2)

1. Copy `docs/devops/examples/aws-waf-rate-limit.tf` into your AWS Terraform stack.
2. Set `alb_arn` to the ALB that fronts Honua.
3. Apply Terraform and verify `waf_web_acl_arn` output.
4. If using `infrastructure/terraform/examples/aws/main.tf`, pass the ACL ARN into the module:

```hcl
module "honua" {
  source = "../../modules/aws-ecs"

  # Existing module arguments...
  waf_web_acl_arn = var.waf_web_acl_arn
}
```

---

## Azure Option (Application Gateway + WAF)

1. Use `docs/devops/examples/azure-application-gateway-waf-rate-limit-policy.json` as the WAF policy payload.
2. Create or update an Application Gateway WAF policy with those custom rules.
3. Associate that WAF policy to the Application Gateway resource that fronts Honua.

The policy template includes:
- API rate-limit rule for `/rest/`, `/ogc/`, `/odata/`
- Strict admin rate-limit rule for `/admin/`
- Per-client grouping via `ClientAddr`

---

## Required Honua Proxy Settings

When deploying behind ALB/Application Gateway, enable forwarded-header processing and set the public base URL so generated links and scheme are correct.

### Environment Variable Example

```bash
ForwardedHeaders__Enabled=true
ForwardedHeaders__ForwardLimit=2
ForwardedHeaders__KnownProxies__0=10.0.0.10
PUBLIC_BASE_URL=https://gis.example.com
```

`PUBLIC_BASE_URL` and `Public__BaseUrl` are both accepted.

### appsettings.json Example

```json
{
  "ForwardedHeaders": {
    "Enabled": true,
    "ForwardLimit": 2,
    "KnownProxies": ["10.0.0.10"]
  },
  "Public": {
    "BaseUrl": "https://gis.example.com"
  }
}
```

`KnownProxies` must contain trusted proxy hops. If your edge IPs change frequently, place a fixed trusted proxy tier in front of Honua and allowlist that tier.

---

## Validation Checklist

1. Verify forwarded headers are applied by checking generated links and redirect URLs use `https` and the public host.
2. Burst requests through edge endpoints and confirm 429 responses appear at the edge.
3. Ensure health probes and `/metrics` are exempt from rate limiting.
4. Monitor blocked request counts and tune thresholds based on legitimate traffic patterns.

---

## Related Documentation

- [ADR-0004: Proxy/Edge Rate Limiting](../contributor/adr/0004-proxy-rate-limiting.md)
- [ADR-0020: MVP Operational Deferrals](../contributor/adr/0020-mvp-operational-deferrals.md)
- [Security Configuration](SECURITY_CONFIGURATION.md)
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
