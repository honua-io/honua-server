# Security Fixes Migration Guide

This guide helps teams safely deploy the critical security fixes to production environments.

## Pre-Deployment Checklist

### 1. Environment Configuration Validation

**Critical:** Ensure your environment variables are correctly configured for the new authentication bypass logic.

#### Development/Test Environments
```bash
# These environments can use auth bypass
ASPNETCORE_ENVIRONMENT=Development  # or Test
HONUA_DEV_AUTH=true
```

#### Production/Staging Environments
```bash
# These environments will enforce authentication
ASPNETCORE_ENVIRONMENT=Production  # or Staging
# HONUA_DEV_AUTH should NOT be set or set to false
HONUA_ADMIN_PASSWORD=<strong-password>
```

### 2. CORS Configuration Review

Update your CORS configuration to comply with the new security requirements:

#### appsettings.Production.json
```json
{
  "Cors": {
    "AllowedOrigins": [
      "https://app.yourdomain.com",
      "https://admin.yourdomain.com"
    ],
    "AllowCredentials": false,
    "PreflightMaxAgeMinutes": 10
  }
}
```

#### For credentials-enabled scenarios (use sparingly)
```json
{
  "Cors": {
    "AllowedOrigins": [
      "https://app.yourdomain.com"  // EXACT domains only - NO wildcards!
    ],
    "AllowCredentials": true,
    "PreflightMaxAgeMinutes": 5
  }
}
```

### 3. Database Query Validation

Review any custom database queries or field names to ensure they comply with the new validation rules:

#### Valid Field Names ✅
- `field_name`
- `FieldName`
- `field-name`
- `_private_field`
- `field123`

#### Invalid Field Names ❌
- `'; DROP TABLE users; --`
- `field with spaces`
- `123field` (starts with number)
- `field@invalid`

## Deployment Steps

### Step 1: Staging Deployment

1. Deploy to staging environment first
2. Run verification script:
   ```bash
   ./scripts/security/verify-security-fixes.sh https://staging.yourdomain.com
   ```
3. Test critical user flows
4. Monitor logs for security events

### Step 2: Production Deployment

1. **Maintenance Window:** Schedule a brief maintenance window
2. **Backup:** Ensure recent backup is available
3. **Deploy:** Deploy the security fixes
4. **Verify:** Run verification script immediately after deployment
5. **Monitor:** Watch for security-related log events

### Step 3: Post-Deployment Monitoring

Monitor these log events for the first 24-48 hours:

- **EventId 4112** - Development bypass environment mismatches
- **EventId 4113** - Production-sanitized admin password failures
- Authentication 401 response patterns
- Field validation errors
- CORS origin violations

## Breaking Changes Assessment

### Low Risk Changes ✅
- Enhanced field validation (invalid fields already failed)
- Improved error message sanitization
- Stricter CORS with credentials

### Medium Risk Changes ⚠️
- Authentication bypass logic (affects development environments)
- CORS wildcard behavior with credentials

### Migration Issues & Solutions

#### Issue: Development Auth Bypass Not Working

**Symptoms:** Development environment shows 401 errors unexpectedly

**Solution:**
```bash
# Check environment configuration
echo $ASPNETCORE_ENVIRONMENT  # Should be "Development" or "Test"
echo $HONUA_DEV_AUTH          # Should be "true"

# Verify application configuration
grep -r "IsDevelopmentMode\|IsTestMode" appsettings*.json
```

#### Issue: CORS Errors After Deployment

**Symptoms:** Browser console shows CORS errors for previously working origins

**Solution:**
1. Review your `AllowedOrigins` configuration
2. If credentials are required, ensure exact domain matches (no wildcards)
3. For non-credential requests, wildcards are still supported

#### Issue: Field Validation Errors

**Symptoms:** Database queries failing with field validation errors

**Solution:**
1. Review field names in your queries
2. Ensure they follow naming conventions (alphanumeric, underscore, hyphen only)
3. Check for any injection attempts being legitimately blocked

## Testing Procedures

### Automated Testing

```bash
# Run security tests
dotnet test tests/dotnet/Honua.Server.Tests/Features/Security/CriticalSecurityFixTests.cs

# Run integration tests
dotnet test tests/dotnet/Honua.Server.Tests/ --filter="Category=Security"

# Run verification script
./scripts/security/verify-security-fixes.sh
```

### Manual Testing

#### 1. Authentication Bypass Test
```bash
# Should return 401 in production
curl -i http://your-server/admin/health
```

#### 2. SQL Injection Test
```bash
# Should be safely handled or rejected
curl "http://your-server/ogc/features/v1/collections/test/items?filter=name=''; DROP TABLE users; --'"
```

#### 3. CORS Test
```bash
# Should not expose credentials to malicious origins
curl -H "Origin: https://evil.com" \
     -H "Access-Control-Request-Method: GET" \
     -X OPTIONS \
     http://your-server/api/data
```

#### 4. Information Disclosure Test
```bash
# Should return generic error in production
curl -H "Authorization: Bearer invalid" http://your-server/admin/health
```

## Rollback Plan

If issues arise during deployment:

### Quick Rollback (< 5 minutes)
1. Revert to previous container/deployment
2. Restore previous environment configuration
3. Verify service health

### Configuration-Only Rollback
If only configuration issues:
1. Update environment variables to previous values
2. Restart application
3. Monitor for restoration of functionality

## Security Monitoring Setup

### Log Monitoring Queries

#### Authentication Security Events
```
EventId:4112 OR EventId:4113 OR "Development bypass blocked"
```

#### SQL Injection Attempts
```
"Invalid attribute name" OR "Field names must start with"
```

#### CORS Security Events
```
"CORS" AND ("evil" OR "malicious" OR "unauthorized")
```

### Alerting Thresholds

- **High Priority:** > 10 security events per minute
- **Medium Priority:** > 100 security events per hour
- **Monitor:** Any EventId 4112 or 4113 events

## Team Communication

### Deployment Announcement Template

```
🔒 SECURITY DEPLOYMENT NOTICE

Critical security fixes are being deployed:
- Authentication bypass protection enhanced
- SQL injection prevention improved  
- CORS credential security strengthened
- Information disclosure prevention added

Timeline: [DATE/TIME]
Expected Impact: Minimal (security improvements only)
Rollback Plan: Available if needed

Contact: [SECURITY TEAM CONTACT]
```

## Compliance Documentation

This deployment addresses:
- **CWE-287** (Improper Authentication)
- **CWE-89** (SQL Injection) 
- **CWE-200** (Information Exposure)
- **CWE-346** (Origin Validation Error)

Security audit findings have been resolved and verified.

---

## Support Contacts

- **Security Issues:** [Security Team]
- **Deployment Issues:** [DevOps Team]  
- **Application Issues:** [Development Team]

For urgent security concerns, follow incident response procedures.