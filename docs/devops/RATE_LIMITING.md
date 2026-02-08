# Rate Limiting

Honua Server **does not include application-level rate limiting** (see [ADR-0004](../contributor/adr/0004-proxy-rate-limiting.md)). Rate limiting is enforced at the edge — your reverse proxy, load balancer, API gateway, or WAF — where limits are global, centralized, and aligned with your deployment topology.

This guide provides production-ready configuration templates for common edge providers.

---

## Why Edge Rate Limiting?

| Concern | App-Level | Edge-Level |
|---|---|---|
| Global limit enforcement | Per-instance only | Across all instances |
| DDoS / abuse protection | Limited | WAF + rate limiting combined |
| Config complexity | App config + Redis | Single proxy config |
| 429 response customization | Custom middleware | Proxy-native |
| Additional dependencies | Redis or equivalent | None (proxy already exists) |

---

## Recommended Limits

These are starting points — tune based on your traffic patterns and capacity.

| Endpoint Pattern | Suggested Rate | Burst | Notes |
|---|---|---|---|
| `/rest/services/*/query` | 200 req/min/IP | 30 | Heavy spatial queries |
| `/rest/services/*/FeatureServer` | 300 req/min/IP | 50 | Metadata and CRUD |
| `/ogc/collections/*/items` | 200 req/min/IP | 30 | OGC feature queries |
| `/odata/*/Features` | 200 req/min/IP | 30 | OData queries |
| `/rest/services/*/MapServer/export` | 100 req/min/IP | 10 | Map rendering (CPU-intensive) |
| `/ogc/collections/*/tiles` | 500 req/min/IP | 80 | Tile serving (cached) |
| `/admin/*` | 30 req/min/IP | 5 | Admin operations |
| `/healthz/*` | Exempt | — | Health probes |
| `/metrics` | Exempt | — | Prometheus scraping |

---

## nginx

### Basic Rate Limiting

```nginx
http {
    # Define rate limit zones (shared memory)
    limit_req_zone $binary_remote_addr zone=api_general:10m rate=200r/m;
    limit_req_zone $binary_remote_addr zone=api_heavy:10m rate=100r/m;
    limit_req_zone $binary_remote_addr zone=admin:10m rate=30r/m;

    # Custom 429 response
    limit_req_status 429;

    server {
        listen 443 ssl;

        # Health and metrics — no rate limiting
        location /healthz {
            proxy_pass http://honua_backend;
        }

        location /metrics {
            proxy_pass http://honua_backend;
            # Restrict to internal Prometheus scraper
            allow 10.0.0.0/8;
            deny all;
        }

        # Admin endpoints — strict limits
        location /admin/ {
            limit_req zone=admin burst=5 nodelay;
            proxy_pass http://honua_backend;
        }

        # MapServer export — CPU-intensive rendering
        location ~ /rest/services/.*/MapServer/export {
            limit_req zone=api_heavy burst=10 nodelay;
            proxy_pass http://honua_backend;
            proxy_read_timeout 60s;
        }

        # General API endpoints
        location /rest/ {
            limit_req zone=api_general burst=30 nodelay;
            proxy_pass http://honua_backend;
        }

        location /ogc/ {
            limit_req zone=api_general burst=30 nodelay;
            proxy_pass http://honua_backend;
        }

        location /odata/ {
            limit_req zone=api_general burst=30 nodelay;
            proxy_pass http://honua_backend;
        }
    }
}
```

### Per-Endpoint Rate Limiting with Maps

For finer control, use `map` to vary limits by endpoint:

```nginx
http {
    limit_req_zone $binary_remote_addr zone=query:10m rate=200r/m;
    limit_req_zone $binary_remote_addr zone=tiles:10m rate=500r/m;
    limit_req_zone $binary_remote_addr zone=render:10m rate=100r/m;

    map $uri $rate_zone {
        ~*/tiles/        tiles;
        ~*/MapServer/    render;
        default          query;
    }

    server {
        location /rest/ {
            limit_req zone=$rate_zone burst=20 nodelay;
            proxy_pass http://honua_backend;
        }
    }
}
```

### Logging Rate-Limited Requests

```nginx
log_format rate_limit '$remote_addr - [$time_local] "$request" '
                      '$status $body_bytes_sent '
                      '"$http_referer" $limit_req_status';

server {
    access_log /var/log/nginx/rate_limited.log rate_limit if=$limit_req_status;
}
```

---

## AWS Application Load Balancer + WAF

### WAF Rate-Based Rule (CloudFormation)

```yaml
RateLimitRule:
  Type: AWS::WAFv2::WebACL
  Properties:
    Name: honua-rate-limit
    Scope: REGIONAL
    DefaultAction:
      Allow: {}
    Rules:
      - Name: api-rate-limit
        Priority: 1
        Action:
          Block:
            CustomResponse:
              ResponseCode: 429
              CustomResponseBodyKey: rate-limited
        Statement:
          RateBasedStatement:
            Limit: 2000  # per 5-minute window per IP
            AggregateKeyType: IP
            ScopeDownStatement:
              ByteMatchStatement:
                FieldToMatch:
                  UriPath: {}
                TextTransformations:
                  - Priority: 0
                    Type: NONE
                SearchString: /rest/
                PositionalConstraint: STARTS_WITH
      - Name: admin-rate-limit
        Priority: 2
        Action:
          Block:
            CustomResponse:
              ResponseCode: 429
        Statement:
          RateBasedStatement:
            Limit: 300  # per 5-minute window per IP
            AggregateKeyType: IP
            ScopeDownStatement:
              ByteMatchStatement:
                FieldToMatch:
                  UriPath: {}
                TextTransformations:
                  - Priority: 0
                    Type: NONE
                SearchString: /admin/
                PositionalConstraint: STARTS_WITH
    CustomResponseBodies:
      rate-limited:
        ContentType: APPLICATION_JSON
        Content: '{"error":"Rate limit exceeded. Please retry later."}'
```

### Terraform (aws_wafv2)

```hcl
resource "aws_wafv2_web_acl" "honua" {
  name  = "honua-rate-limit"
  scope = "REGIONAL"

  default_action { allow {} }

  rule {
    name     = "api-rate-limit"
    priority = 1

    action { block {} }

    statement {
      rate_based_statement {
        limit              = 2000
        aggregate_key_type = "IP"

        scope_down_statement {
          byte_match_statement {
            field_to_match { uri_path {} }
            positional_constraint = "STARTS_WITH"
            search_string         = "/rest/"
            text_transformation {
              priority = 0
              type     = "NONE"
            }
          }
        }
      }
    }

    visibility_config {
      sampled_requests_enabled   = true
      cloudwatch_metrics_enabled = true
      metric_name                = "honua-api-rate-limit"
    }
  }
}
```

---

## Kubernetes NGINX Ingress Controller

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: honua-server
  annotations:
    # Global rate limit: 200 requests/minute per IP
    nginx.ingress.kubernetes.io/limit-rps: "4"
    nginx.ingress.kubernetes.io/limit-burst-multiplier: "8"
    nginx.ingress.kubernetes.io/limit-connections: "20"

    # Custom 429 response
    nginx.ingress.kubernetes.io/custom-http-errors: "429"
    nginx.ingress.kubernetes.io/default-backend: honua-error-pages

    # Exempt health checks from rate limiting
    nginx.ingress.kubernetes.io/server-snippet: |
      location /healthz {
        limit_req off;
        proxy_pass http://upstream;
      }
spec:
  rules:
    - host: gis.example.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: honua-server
                port:
                  number: 8080
```

For per-path limits, use separate Ingress resources:

```yaml
# Admin endpoints — strict limits
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: honua-admin
  annotations:
    nginx.ingress.kubernetes.io/limit-rps: "1"
    nginx.ingress.kubernetes.io/limit-burst-multiplier: "5"
spec:
  rules:
    - host: gis.example.com
      http:
        paths:
          - path: /admin
            pathType: Prefix
            backend:
              service:
                name: honua-server
                port:
                  number: 8080
```

---

## Azure Application Gateway + WAF Policy

```json
{
  "properties": {
    "customRules": [
      {
        "name": "ApiRateLimit",
        "priority": 1,
        "ruleType": "RateLimitRule",
        "rateLimitDuration": "OneMin",
        "rateLimitThreshold": 200,
        "matchConditions": [
          {
            "matchVariables": [{ "variableName": "RequestUri" }],
            "operator": "BeginsWith",
            "matchValues": ["/rest/", "/ogc/", "/odata/"]
          }
        ],
        "groupByUserSession": [
          { "groupByVariables": [{ "variableName": "ClientAddr" }] }
        ],
        "action": "Block"
      },
      {
        "name": "AdminRateLimit",
        "priority": 2,
        "ruleType": "RateLimitRule",
        "rateLimitDuration": "OneMin",
        "rateLimitThreshold": 30,
        "matchConditions": [
          {
            "matchVariables": [{ "variableName": "RequestUri" }],
            "operator": "BeginsWith",
            "matchValues": ["/admin/"]
          }
        ],
        "groupByUserSession": [
          { "groupByVariables": [{ "variableName": "ClientAddr" }] }
        ],
        "action": "Block"
      }
    ]
  }
}
```

---

## GCP Cloud Armor

```yaml
securityPolicy:
  name: honua-rate-limit
  rules:
    - action: throttle
      priority: 1000
      match:
        expr:
          expression: "request.path.startsWith('/rest/') || request.path.startsWith('/ogc/') || request.path.startsWith('/odata/')"
      rateLimitOptions:
        rateLimitThreshold:
          count: 200
          intervalSec: 60
        conformAction: allow
        exceedAction: deny(429)
        enforceOnKey: IP

    - action: throttle
      priority: 1001
      match:
        expr:
          expression: "request.path.startsWith('/admin/')"
      rateLimitOptions:
        rateLimitThreshold:
          count: 30
          intervalSec: 60
        conformAction: allow
        exceedAction: deny(429)
        enforceOnKey: IP
```

---

## Monitoring Rate Limits

### Key Metrics to Track

| Metric | Source | Alert Threshold |
|---|---|---|
| 429 response rate | Edge logs / metrics | > 5% of total requests |
| Blocked request count | WAF dashboard | Sustained spike |
| Unique IPs being limited | Edge logs | > 10 concurrent |
| Request latency (P99) | Application metrics | Indicates if limits are too loose |

### nginx Prometheus Exporter

If using the nginx VTS module or the Prometheus exporter:

```promql
# 429 response rate
rate(nginx_http_requests_total{status="429"}[5m])
  / rate(nginx_http_requests_total[5m])

# Alert: more than 5% of requests being rate limited
rate(nginx_http_requests_total{status="429"}[5m])
  / rate(nginx_http_requests_total[5m]) > 0.05
```

### AWS WAF Metrics

```promql
# CloudWatch metric: BlockedRequests from WAF
aws_wafv2_blocked_requests_sum > 100
```

---

## Tuning Guide

1. **Start permissive** — set limits at 2-3x your expected peak traffic.
2. **Monitor 429 rates** — if legitimate users hit limits, increase the threshold.
3. **Differentiate endpoints** — tile serving can handle higher rates than map rendering.
4. **Use burst allowances** — short bursts (page loads) should succeed; sustained abuse should be blocked.
5. **Exempt internal traffic** — health probes, metrics scrapers, and inter-service calls should bypass limits.
6. **Consider API keys** — if you add API key authentication, rate limit per key instead of per IP for fairer distribution.

---

## Related Documentation

- [ADR-0004: Proxy/Edge Rate Limiting](../contributor/adr/0004-proxy-rate-limiting.md) — architectural decision
- [ADR-0020: MVP Operational Deferrals](../contributor/adr/0020-mvp-operational-deferrals.md) — MVP scope
- [Security Configuration](SECURITY_CONFIGURATION.md) — authentication and edge security
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md) — deployment patterns
