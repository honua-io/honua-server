# Content Security Policy (CSP) Enhancement for Honua Server

## Overview

This document describes the enhanced Content Security Policy (CSP) implementation for the Honua Server geospatial API platform. The implementation provides comprehensive XSS protection while enabling necessary resources for geospatial data visualization and processing.

## Key Features

### 1. Environment-Specific Policies

The CSP system automatically adapts based on the deployment environment:

- **Production**: Restrictive policies with no unsafe directives
- **Development**: Relaxed policies allowing localhost and debugging tools
- **Custom**: Fully configurable policies for specific deployment needs

### 2. Geospatial-Specific Optimizations

The CSP builder includes predefined configurations for geospatial APIs:

- **Tile Server Support**: Trusted domains for map tile loading
- **Mapping CDN Integration**: Popular mapping library CDNs
- **WebSocket Support**: Real-time geospatial data connections
- **Data URI Support**: Inline map icons and geographic data

### 3. Security Validation and Reporting

- **Policy Validation**: Automatic detection of insecure configurations
- **Violation Reporting**: Endpoint for collecting CSP violations from browsers
- **Suspicious Activity Detection**: Identification of potential attack patterns
- **Comprehensive Logging**: Structured security event logging

## Configuration

### Basic Configuration

```json
{
  "SecurityHeaders": {
    "Csp": {
      "PolicyType": "GeospatialApi",
      "AllowDevelopmentFeatures": false,
      "ReportOnly": false
    }
  }
}
```

### Advanced Configuration

```json
{
  "SecurityHeaders": {
    "Csp": {
      "PolicyType": "GeospatialApi",
      "AllowDevelopmentFeatures": false,
      "TrustedTileServers": [
        "*.openstreetmap.org",
        "api.mapbox.com",
        "*.tiles.mapbox.com"
      ],
      "TrustedCdns": [
        "cdnjs.cloudflare.com",
        "unpkg.com"
      ],
      "TrustedGeospatialApis": [
        "api.mapbox.com",
        "nominatim.openstreetmap.org"
      ],
      "WebSocketUrls": [
        "wss://realtime.example.com"
      ],
      "AllowedScriptHashes": [
        "sha256-abc123def456..."
      ],
      "AllowedStyleHashes": [
        "sha256-xyz789uvw012..."
      ],
      "CustomDirectives": {
        "manifest-src": "'self'",
        "worker-src": "'self' blob:"
      },
      "ReportUri": "/csp-violation-report",
      "ReportOnly": false
    }
  }
}
```

## Policy Types

### 1. GeospatialApi (Recommended)

Balanced policy optimized for geospatial applications:
- Allows necessary resources for map rendering
- Supports common geospatial data formats
- Maintains strong security posture

**Generated Policy Example:**
```
default-src 'self';
script-src 'self';
style-src 'self' 'unsafe-inline';
img-src 'self' data: blob: *.openstreetmap.org;
connect-src 'self' api.mapbox.com;
worker-src 'self' blob:;
object-src 'none';
frame-ancestors 'none'
```

### 2. ApiOnly

Extremely restrictive policy for API-only endpoints:
- Blocks all resource loading
- Suitable for REST APIs with no UI components
- Maximum security for backend services

**Generated Policy Example:**
```
default-src 'none';
script-src 'none';
style-src 'none';
img-src 'none';
connect-src 'none';
object-src 'none';
frame-ancestors 'none'
```

### 3. Custom

Fully configurable policy using only provided directives:
- Complete control over CSP directives
- No predefined rules
- Suitable for specialized deployments

## Environment-Specific Configurations

### Development Configuration

**File**: `appsettings.Development.json`

```json
{
  "SecurityHeaders": {
    "EnableHsts": false,
    "Csp": {
      "PolicyType": "GeospatialApi",
      "AllowDevelopmentFeatures": true,
      "TrustedTileServers": [
        "localhost:*",
        "127.0.0.1:*",
        "*.localhost"
      ],
      "WebSocketUrls": [
        "ws://localhost:*",
        "wss://localhost:*"
      ]
    }
  }
}
```

**Key Features:**
- Disabled HSTS (allows HTTP in development)
- Allows `'unsafe-eval'` for debugging tools
- Permits localhost connections on all ports
- Relaxed policies for development productivity

### Production Configuration

**File**: `appsettings.Production.json`

```json
{
  "SecurityHeaders": {
    "EnableHsts": true,
    "HstsPreload": true,
    "Csp": {
      "PolicyType": "GeospatialApi",
      "AllowDevelopmentFeatures": false,
      "TrustedTileServers": [
        "*.openstreetmap.org",
        "api.mapbox.com"
      ],
      "ReportUri": "/csp-violation-report"
    }
  }
}
```

**Key Features:**
- Strict HSTS with preload
- No unsafe directives
- Curated list of trusted domains
- CSP violation reporting enabled

## Violation Reporting

### Endpoint

The CSP violation report endpoint (`/csp-violation-report`) automatically:

1. **Accepts Anonymous Reports**: Browsers send reports without authentication
2. **Logs Structured Data**: Security events are properly logged
3. **Detects Suspicious Activity**: Identifies potential attack patterns
4. **Returns 204 No Content**: Acknowledges receipt without information leakage

### Suspicious Pattern Detection

The system automatically flags violations containing:
- `javascript:` URLs
- Browser extension URLs
- `data:text/html` content
- Eval expressions
- VBScript content

### Log Examples

**Normal Violation:**
```
CSP violation reported - Blocked URI: https://untrusted.cdn.com/script.js,
Directive: script-src, Policy: default-src 'self'; script-src 'self',
Client IP: 192.168.1.100
```

**Suspicious Violation:**
```
Suspicious CSP violation detected - Blocked URI: javascript:alert('xss'),
Source: eval, Client IP: 10.0.0.50
```

## Security Best Practices

### 1. Hash-Based Inline Content

Instead of `'unsafe-inline'`, use content hashes:

```json
{
  "AllowedScriptHashes": [
    "sha256-abc123def456789"
  ],
  "AllowedStyleHashes": [
    "sha256-xyz789uvw012345"
  ]
}
```

### 2. Gradual Policy Tightening

Start with report-only mode to assess impact:

```json
{
  "ReportOnly": true,
  "ReportUri": "/csp-violation-report"
}
```

### 3. Regular Policy Review

Monitor violation reports to:
- Identify unnecessary trusted domains
- Detect new security threats
- Optimize policy effectiveness

### 4. Environment Separation

Maintain different policies per environment:
- Development: Relaxed for productivity
- Staging: Production-like for testing
- Production: Maximum security

## Testing

The implementation includes comprehensive tests:

### Unit Tests

- **ContentSecurityPolicyBuilderTests**: Builder functionality and validation
- **SecurityHeadersMiddlewareTests**: Middleware integration and configuration

### Integration Tests

- **CspViolationReportEndpointTests**: End-to-end violation reporting
- Real HTTP request testing with various report formats

### Test Coverage

Tests verify:
- Policy generation for all configuration types
- Environment-specific behavior
- Validation warnings for insecure configurations
- Violation report processing
- Edge cases and error handling

## Performance Considerations

### 1. Policy Caching

CSP policies are built once during application startup and cached for the lifetime of the application.

### 2. Minimal Overhead

The middleware adds minimal performance impact:
- Single string concatenation per request
- No dynamic policy generation
- Efficient header application

### 3. Violation Report Processing

Violation reports are processed asynchronously to avoid blocking browser requests.

## Migration Guide

### From Simple String Policy

**Before:**
```json
{
  "ContentSecurityPolicy": "default-src 'self'; script-src 'self'"
}
```

**After:**
```json
{
  "Csp": {
    "PolicyType": "GeospatialApi",
    "AllowDevelopmentFeatures": false
  }
}
```

### Backward Compatibility

The system maintains backward compatibility:
- Existing `ContentSecurityPolicy` string configuration continues to work
- New `Csp` configuration takes precedence when present
- No breaking changes to existing deployments

## Troubleshooting

### Common Issues

1. **Maps Not Loading**
   - Add tile server domains to `TrustedTileServers`
   - Check browser console for CSP violations

2. **JavaScript Errors in Development**
   - Set `AllowDevelopmentFeatures: true`
   - Add localhost domains to trusted lists

3. **Third-Party Libraries Blocked**
   - Add CDN domains to `TrustedCdns`
   - Consider using script/style hashes

### Debug Mode

Enable debug logging:

```json
{
  "Logging": {
    "LogLevel": {
      "Honua.Server.Features.Infrastructure.Security": "Debug"
    }
  }
}
```

This provides detailed information about:
- Policy generation process
- Validation warnings
- Configuration processing

## Security Considerations

### 1. Trusted Domain Management

Regularly review and audit trusted domains:
- Remove unused domains
- Verify domain ownership
- Monitor for domain compromise

### 2. Report Analysis

Analyze violation reports for:
- Potential attack attempts
- Policy effectiveness
- Required policy adjustments

### 3. Incident Response

In case of security incidents:
1. Review recent CSP violation reports
2. Tighten policies if necessary
3. Monitor for unusual patterns

## Conclusion

The enhanced CSP implementation provides:
- **Strong Security**: Protection against XSS and injection attacks
- **Geospatial Optimization**: Support for mapping and GIS applications
- **Flexibility**: Environment-specific and customizable policies
- **Monitoring**: Comprehensive violation reporting and logging
- **Maintainability**: Clear configuration and validation

This implementation follows security best practices while maintaining the usability required for modern geospatial applications.