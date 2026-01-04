# Authentication Troubleshooting Guide

This guide helps resolve authentication and authorization issues in Honua Server, including API keys, OIDC configuration, and access control problems.

## Quick Authentication Diagnostics

### Test Authentication Status

```bash
# Test unauthenticated access to public endpoints
curl -v http://localhost:8080/health

# Test admin endpoint without authentication (should fail)
curl -v http://localhost:8080/admin/configuration

# Test with API key
curl -H "X-API-Key: your-api-key" http://localhost:8080/admin/configuration

# Test OIDC endpoint (if configured)
curl -v http://localhost:8080/.well-known/openid_configuration
```

### Check Authentication Configuration

```bash
# Check environment variables
env | grep -E "(API_KEY|OIDC|AUTH)"

# Check application logs for authentication errors
docker logs honua-server | grep -i auth

# Verify SSL/TLS configuration
openssl s_client -connect localhost:8080 -servername localhost
```

## API Key Authentication Issues

### Issue: `401 Unauthorized` for Admin Endpoints

**Error Response**:
```json
{
  "type": "https://tools.ietf.org/html/rfc7235#section-3.1",
  "title": "Unauthorized",
  "status": 401,
  "detail": "API key required for admin endpoints"
}
```

**Root Cause**: Missing or invalid API key for admin endpoints.

**Solutions**:

1. **Set Admin API Key**:
   ```bash
   # Set environment variable
   export HONUA_ADMIN_PASSWORD="your-secure-api-key-here"

   # For Docker environments
   docker run -e HONUA_ADMIN_PASSWORD="your-secure-api-key" honua-server
   ```

2. **Use Correct Header Format**:
   ```bash
   # Correct API key usage
   curl -H "X-API-Key: your-secure-api-key-here" \
        http://localhost:8080/admin/configuration

   # Alternative using Authorization header
   curl -H "Authorization: Bearer your-secure-api-key-here" \
        http://localhost:8080/admin/configuration
   ```

3. **Generate Secure API Key**:
   ```bash
   # Generate cryptographically secure API key
   openssl rand -base64 32

   # Alternative using uuidgen
   uuidgen

   # Store securely in environment
   export HONUA_ADMIN_PASSWORD=$(openssl rand -base64 32)
   ```

### Issue: API Key Not Being Recognized

**Diagnostic Steps**:

1. **Verify Header Format**:
   ```bash
   # Check if header is being sent correctly
   curl -v -H "X-API-Key: test-key" http://localhost:8080/admin/configuration 2>&1 | grep -E "(X-API-Key|Authorization)"
   ```

2. **Check Application Configuration**:
   ```bash
   # Verify environment variable is set
   echo $HONUA_ADMIN_PASSWORD

   # Check if variable is accessible to application
   docker exec honua-server printenv | grep HONUA_ADMIN_PASSWORD
   ```

3. **Review Application Logs**:
   ```bash
   # Look for authentication-related log entries
   docker logs honua-server | grep -E "(Authentication|API.*key|Unauthorized)"
   ```

**Solutions**:

1. **Fix Environment Variable Name**:
   ```bash
   # Ensure correct variable name
   unset HONUA_ADMIN_PASS  # Remove incorrect variable
   export HONUA_ADMIN_PASSWORD="correct-api-key"
   ```

2. **Restart Application After Configuration Changes**:
   ```bash
   # Restart container to pick up environment changes
   docker restart honua-server

   # Or restart service
   systemctl restart honua-server
   ```

3. **Validate API Key Format**:
   ```bash
   # API key should be non-empty and contain valid characters
   if [[ -z "$HONUA_ADMIN_PASSWORD" ]]; then
       echo "ERROR: HONUA_ADMIN_PASSWORD not set"
   elif [[ ${#HONUA_ADMIN_PASSWORD} -lt 16 ]]; then
       echo "WARNING: API key is too short (minimum 16 characters)"
   else
       echo "API key configuration looks good"
   fi
   ```

## OIDC (OpenID Connect) Authentication Issues

### Issue: `500 Internal Server Error` During OIDC Setup

**Error in Logs**:
```
Microsoft.AspNetCore.Authentication.AuthenticationFailureException: Failed to retrieve OIDC configuration
```

**Root Cause**: OIDC provider not reachable or misconfigured.

**Diagnostic Steps**:

1. **Test OIDC Provider Connectivity**:
   ```bash
   # Test OIDC discovery endpoint
   curl -v "${OIDC__GENERIC__AUTHORITY}/.well-known/openid-configuration"

   # Example for common providers:
   # Auth0: curl -v "https://your-tenant.auth0.com/.well-known/openid-configuration"
   # Azure AD: curl -v "https://login.microsoftonline.com/your-tenant/.well-known/openid-configuration"
   # Keycloak: curl -v "https://keycloak.example.com/realms/your-realm/.well-known/openid-configuration"
   ```

2. **Verify DNS Resolution**:
   ```bash
   # Test DNS resolution for OIDC provider
   nslookup your-oidc-provider.com
   ping your-oidc-provider.com
   ```

3. **Check SSL/TLS Certificate**:
   ```bash
   # Verify SSL certificate validity
   openssl s_client -connect your-oidc-provider.com:443 -servername your-oidc-provider.com
   ```

**Solutions**:

1. **Configure OIDC Environment Variables**:
   ```bash
   # Basic OIDC configuration
   export OIDC__ENABLED="true"
   export OIDC__GENERIC__ENABLED="true"
   export OIDC__GENERIC__AUTHORITY="https://your-oidc-provider.com"
   export OIDC__GENERIC__CLIENTID="your-client-id"
   export OIDC__GENERIC__CLIENTSECRET="your-client-secret"

   # Optional advanced settings
   export OIDC__GENERIC__RESPONSETYPE="code"
   export OIDC__REQUIREHTTPS="true"
   export OIDC__TOKENVALIDATION__VALIDATEISSUER="true"
   ```

2. **Handle Network/Proxy Issues**:
   ```bash
   # Set proxy if required
   export HTTP_PROXY="http://proxy.company.com:8080"
   export HTTPS_PROXY="https://proxy.company.com:8080"

   # Skip SSL verification for development only
   export OIDC__REQUIREHTTPS="false"  # NOT for production!
   ```

3. **Configure Trusted Certificate Store**:
   ```bash
   # Add custom CA certificate (if using internal PKI)
   sudo cp your-ca-cert.crt /usr/local/share/ca-certificates/
   sudo update-ca-certificates

   # For containers, mount certificate volume
   docker run -v /etc/ssl/certs:/etc/ssl/certs:ro honua-server
   ```

### Issue: OIDC Token Validation Failures

**Error in Logs**:
```
Microsoft.IdentityModel.Tokens.SecurityTokenValidationException: IDX10205: Issuer validation failed
```

**Root Cause**: Token issuer mismatch or clock skew.

**Solutions**:

1. **Verify Issuer Configuration**:
   ```bash
   # Check OIDC discovery document for correct issuer
   curl -s "${OIDC__GENERIC__AUTHORITY}/.well-known/openid-configuration" | jq '.issuer'

   # Ensure OIDC__GENERIC__AUTHORITY matches the issuer exactly
   export OIDC__GENERIC__AUTHORITY="https://exact-issuer-from-discovery-document"
   ```

2. **Handle Clock Skew**:
   ```bash
   # Sync system clock
   sudo ntpdate -s time.nist.gov

   # Or configure NTP service
   sudo systemctl enable ntp
   sudo systemctl start ntp

   # Check current time
   date
   ```

3. **Configure Token Validation Options**:
   ```bash
   # Allow some clock skew tolerance
   export OIDC__TOKENVALIDATION__CLOCKSKEW="00:05:00"  # 5 minutes

   # Set token lifetime validation
   export OIDC__TOKENVALIDATION__VALIDATELIFETIME="true"
   ```

### Issue: OIDC Redirect URI Mismatch

**Error**: `redirect_uri_mismatch`

**Root Cause**: The redirect URI in the OIDC provider doesn't match the application's callback URL.

**Solutions**:

1. **Configure Correct Redirect URIs in OIDC Provider**:
   ```
   # Add these URIs to your OIDC provider configuration:
   http://localhost:8080/signin-oidc          # Local development
   https://your-domain.com/signin-oidc        # Production
   https://your-domain.com/signout-callback-oidc  # Sign-out callback
   ```

2. **Set Application URLs**:
   ```bash
   # Configure application URLs
   export ASPNETCORE_URLS="https://0.0.0.0:8080"
   export APPLICATION_URL="https://your-domain.com"

   # If you override the callback paths, keep the IdP redirect URIs in sync
   export OIDC__GENERIC__CALLBACKPATH="/signin-oidc"
   export OIDC__GENERIC__SIGNEDOUTCALLBACKPATH="/signout-callback-oidc"
   ```

## Access Control and Authorization Issues

### Issue: Authenticated User Lacks Required Permissions

**Error Response**:
```json
{
  "type": "https://tools.ietf.org/html/rfc7235#section-3.3",
  "title": "Forbidden",
  "status": 403,
  "detail": "Insufficient privileges for this operation"
}
```

**Root Cause**: User authenticated but lacks required roles or claims.

**Diagnostic Steps**:

1. **Examine JWT Token Claims**:
   ```bash
   # Decode JWT token (replace YOUR_TOKEN with actual token)
   echo "YOUR_TOKEN" | cut -d. -f2 | base64 -d | jq .

   # Check for required claims
   echo "YOUR_TOKEN" | cut -d. -f2 | base64 -d | jq '.roles, .groups, .scope'
   ```

2. **Check Application Role Configuration**:
   ```bash
   # Review role requirements in application logs
   docker logs honua-server | grep -E "(role|permission|claim)"
   ```

**Solutions**:

1. **Configure Required Claims in OIDC Provider**:
   ```json
   // Example Auth0 rule to add admin role
   function addAdminRole(user, context, callback) {
     if (user.email === 'admin@example.com') {
       context.idToken['https://honua.app/roles'] = ['admin'];
       context.accessToken['https://honua.app/roles'] = ['admin'];
     }
     callback(null, user, context);
   }
   ```

2. **Map OIDC Claims to Application Roles**:
   ```bash
   # Configure claim mapping
   export OIDC__CLAIMSMAPPING__ROLECLAIMTYPE="https://honua.app/roles"
   export OIDC__CLAIMSMAPPING__NAMECLAIMTYPE="name"
   export OIDC__CLAIMSMAPPING__EMAILCLAIMTYPE="email"
   ```

3. **Set Up Role-Based Access Control**:
   ```bash
   # Configure admin users
   export HONUA_ADMIN_EMAILS="admin@example.com,manager@example.com"

   # Configure role requirements
   export HONUA_ADMIN_ROLE="admin"
   export HONUA_USER_ROLE="user"
   ```

## SSL/TLS and Certificate Issues

### Issue: SSL Certificate Validation Errors

**Error**: `SSL connection could not be established`

**Diagnostic Steps**:

1. **Test SSL Configuration**:
   ```bash
   # Test SSL handshake
   openssl s_client -connect localhost:8080 -servername localhost

   # Check certificate details
   echo | openssl s_client -connect localhost:8080 2>/dev/null | openssl x509 -text
   ```

2. **Verify Certificate Chain**:
   ```bash
   # Check certificate chain validity
   curl -vI https://localhost:8080/health

   # Test with specific SSL version
   curl --tlsv1.2 -vI https://localhost:8080/health
   ```

**Solutions**:

1. **Configure Proper SSL Certificates**:
   ```bash
   # For development, generate self-signed certificate
   openssl req -x509 -newkey rsa:4096 -keyout key.pem -out cert.pem -days 365 -nodes \
       -subj "/C=US/ST=CA/L=San Francisco/O=Honua/CN=localhost"

   # Use in application
   export ASPNETCORE_Kestrel__Certificates__Default__Path=cert.pem
   export ASPNETCORE_Kestrel__Certificates__Default__KeyPath=key.pem
   ```

2. **Configure Certificate Store**:
   ```bash
   # Import certificate to trusted store (Linux)
   sudo cp cert.pem /usr/local/share/ca-certificates/honua.crt
   sudo update-ca-certificates

   # For Docker containers
   docker run -v /usr/local/share/ca-certificates:/usr/local/share/ca-certificates:ro honua-server
   ```

3. **Handle Certificate Validation in Development**:
   ```bash
   # Disable SSL validation for development only
   export ASPNETCORE_ENVIRONMENT="Development"
   export DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT=false

   # Skip certificate validation (DEVELOPMENT ONLY!)
   export ASPNETCORE_Kestrel__Certificates__Default__AllowInvalid=true
   ```

## Common Configuration Patterns

### Environment Variable Templates

**Development Configuration**:
```bash
# Basic development setup
export ASPNETCORE_ENVIRONMENT="Development"
export HONUA_ADMIN_PASSWORD="dev-admin-key-123"
export ConnectionStrings__DefaultConnection="Host=localhost;Database=honua;Username=postgres;Password=postgres"

# Disable HTTPS redirects for development
export ASPNETCORE_URLS="http://0.0.0.0:8080"
```

**Production Configuration**:
```bash
# Production security settings
export ASPNETCORE_ENVIRONMENT="Production"
export HONUA_ADMIN_PASSWORD="$(openssl rand -base64 32)"
export ASPNETCORE_URLS="https://0.0.0.0:8080"
export ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

# OIDC configuration
export OIDC__ENABLED="true"
export OIDC__GENERIC__ENABLED="true"
export OIDC__GENERIC__AUTHORITY="https://your-oidc-provider.com"
export OIDC__GENERIC__CLIENTID="your-production-client-id"
export OIDC__GENERIC__CLIENTSECRET="your-production-client-secret"
export OIDC__REQUIREHTTPS="true"
```

**Docker Compose Configuration**:
```yaml
services:
  honua-server:
    environment:
      # Authentication
      HONUA_ADMIN_PASSWORD: "${HONUA_ADMIN_PASSWORD}"

      # OIDC
      OIDC__ENABLED: "${OIDC__ENABLED}"
      OIDC__GENERIC__ENABLED: "${OIDC__GENERIC__ENABLED}"
      OIDC__GENERIC__AUTHORITY: "${OIDC__GENERIC__AUTHORITY}"
      OIDC__GENERIC__CLIENTID: "${OIDC__GENERIC__CLIENTID}"
      OIDC__GENERIC__CLIENTSECRET: "${OIDC__GENERIC__CLIENTSECRET}"

      # Security headers
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      ASPNETCORE_URLS: "http://0.0.0.0:8080"
```

## Security Best Practices

### API Key Management

1. **Generate Strong API Keys**:
   ```bash
   # Use cryptographically secure random generation
   openssl rand -base64 32 > /dev/null  # Good
   # Don't use: date | md5sum  # Predictable, insecure
   ```

2. **Store Keys Securely**:
   ```bash
   # Use secrets management
   # Kubernetes: kubectl create secret generic honua-api-key --from-literal=key="$(openssl rand -base64 32)"
   # Docker Swarm: echo "$(openssl rand -base64 32)" | docker secret create honua-api-key -
   # Azure Key Vault: az keyvault secret set --vault-name honua-vault --name api-key --value "$(openssl rand -base64 32)"
   ```

3. **Rotate Keys Regularly**:
   ```bash
   # Automated key rotation script
   #!/bin/bash
   NEW_KEY=$(openssl rand -base64 32)
   kubectl patch secret honua-api-key -p='{"data":{"key":"'$(echo -n "$NEW_KEY" | base64)'"}}'
   kubectl rollout restart deployment honua-server
   ```

### HTTPS Configuration

```bash
# Production HTTPS settings
export ASPNETCORE_HTTPS_PORT="8443"
export ASPNETCORE_URLS="https://0.0.0.0:8443"

# Security headers
export SecurityHeaders__StrictTransportSecurity="max-age=31536000; includeSubDomains"
export SecurityHeaders__ContentSecurityPolicy="default-src 'self'"
```

## Troubleshooting Checklist

### Pre-Flight Checks
- [ ] Environment variables are set and non-empty
- [ ] API keys are sufficiently random and properly encoded
- [ ] OIDC provider is reachable and configured correctly
- [ ] SSL certificates are valid and properly trusted
- [ ] System clock is synchronized
- [ ] Network connectivity allows outbound HTTPS requests

### Authentication Flow Validation
- [ ] Health endpoint accessible without authentication
- [ ] Admin endpoints require proper authentication
- [ ] OIDC discovery endpoint returns valid configuration
- [ ] Token validation works with current time
- [ ] Claims mapping produces expected roles

### Common Fixes
- [ ] Restart application after configuration changes
- [ ] Clear browser cache and cookies
- [ ] Check firewall rules for OIDC callbacks
- [ ] Verify redirect URIs in OIDC provider match exactly
- [ ] Confirm issuer claim matches OIDC authority

## Getting Help

For authentication issues not covered here:

1. **Collect authentication diagnostics**:
   ```bash
   # Create authentication diagnostic report
   {
       echo "=== Environment Variables ==="
       env | grep -E "(HONUA|OIDC|AUTH)" | sed 's/=.*/=***REDACTED***/'

       echo "=== OIDC Discovery ==="
       curl -s "${OIDC__GENERIC__AUTHORITY}/.well-known/openid-configuration" | jq .

       echo "=== Application Logs ==="
       docker logs honua-server 2>&1 | tail -100 | grep -E "(auth|Auth|AUTH)"
   } > auth-diagnostic-report.txt
   ```

2. **Include specific error messages and HTTP status codes**
3. **Provide OIDC provider type and configuration (without secrets)**
4. **Share curl commands that reproduce the issue**
5. **Include browser developer tools network tab screenshots for web flows**
