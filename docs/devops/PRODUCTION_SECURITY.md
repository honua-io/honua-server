# Production Security Configuration

**Post-MVP:** Audit logging and compliance storage are deferred. This document describes the planned production-grade security work.

## Overview

Honua Server plans to include production-ready security services that replace the development stub implementations:

- **ProductionCryptographicService**: AES-256 encryption and RSA-2048 digital signatures
- **ProductionAuditLogStorage**: File-based audit logging with integrity protection
- **Token Replay Protection**: Enabled by default with JTI tracking

## Security Services

### ProductionCryptographicService

**Features:**
- AES-256 encryption in CBC mode with PKCS7 padding
- RSA-2048 digital signatures with SHA-256 hashing
- Cryptographically secure random key generation
- Proper disposal of cryptographic keys

**Usage:**
```csharp
// Automatic registration in production environments
services.AddSecurityCompliance(configuration);

// Manual registration for specific environments
services.AddProductionSecurityServices(configuration);
```

### ProductionAuditLogStorage

**Features:**
- File-based persistence with automatic log rotation
- Cryptographic integrity protection for all audit events
- Thread-safe concurrent access
- Configurable storage directory
- Integrity validation capabilities

**Configuration:**
```json
{
  "Security": {
    "AuditLogging": {
      "LogDirectory": "/var/log/honua/audit-logs"
    }
  }
}
```

**Default Behavior:**
- Logs stored in `{AppDomain.BaseDirectory}/audit-logs/`
- Automatic hourly log rotation
- Files named: `audit-yyyy-MM-dd-HH.log`
- Compliance reports in `audit-logs/compliance-reports/`

## Token Replay Protection

**Configuration (appsettings.json):**
```json
{
  "Oidc": {
    "TokenValidation": {
      "EnableTokenReplayProtection": true,
      "TokenReplayCacheDuration": "00:10:00"
    }
  }
}
```

**Default Settings:**
- Enabled by default in production
- 10-minute cache duration for JWT IDs
- Uses in-memory cache for tracking

## Environment-Specific Behavior

### Production Environment
- Production security services registered by default
- Validation enforces secure implementations
- Token replay protection enabled
- File-based audit logging

### Development/Test Environments
- Allows stub implementations (for testing)
- Validation can be bypassed with `AllowInMemoryStorage=true`
- Reduced security overhead for development

## Configuration Options

### Security Compliance Options
```json
{
  "Security": {
    "Compliance": {
      "AllowInMemoryStorage": false  // Set to true for dev/test only
    },
    "AuditLogging": {
      "LogDirectory": "/var/log/honua/audit-logs"
    }
  }
}
```

### Key Management

⚠️ **Important**: The default implementation generates keys at startup. For production deployments:

1. **Use external key management** (Azure Key Vault, HashiCorp Vault, etc.)
2. **Implement key rotation** procedures
3. **Backup cryptographic keys** securely

**Custom Key Provider Example:**
```csharp
services.AddScoped<ICryptographicService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ProductionCryptographicService>>();
    var rsaKey = LoadRsaKeyFromKeyVault(); // Your implementation
    var aesKey = LoadAesKeyFromKeyVault(); // Your implementation
    return new ProductionCryptographicService(logger, rsaKey, aesKey);
});
```

## Validation

The system includes automatic validation to prevent accidental use of stub implementations in production:

```csharp
// In Program.cs - after service registration
if (app.Environment.IsProduction())
{
    SecurityProductionServicesExtensions.ValidateProductionSecurityServices(app.Services);
}
```

**Validation Checks:**
- Verifies production implementations are registered
- Tests encryption/decryption functionality
- Tests digital signature operations
- Ensures audit logging is persistent

## Audit Log Integrity

The production audit storage includes integrity validation:

```csharp
var auditStorage = serviceProvider.GetRequiredService<IAuditLogStorage>();
if (auditStorage is ProductionAuditLogStorage productionStorage)
{
    var validationResult = await productionStorage.ValidateIntegrityAsync(
        DateTime.UtcNow.AddDays(-30),
        DateTime.UtcNow);

    if (validationResult.IntegrityPercentage < 100)
    {
        // Handle integrity violations
    }
}
```

## Security Best Practices

1. **Key Management**:
   - Use external key vaults for production keys
   - Implement regular key rotation
   - Never store keys in configuration files

2. **Audit Logs**:
   - Monitor audit log directory disk space
   - Implement log archival procedures
   - Regular integrity validation

3. **Token Security**:
   - Keep replay protection cache duration reasonable (5-15 minutes)
   - Monitor for unusual token replay patterns
   - Use HTTPS only for token endpoints

4. **File Permissions**:
   - Restrict audit log directory access (0750 permissions)
   - Ensure proper ownership of log files
   - Regular security audits of file permissions

## Troubleshooting

### Common Issues

**InvalidOperationException: Production environment requires secure cryptographic implementation**
- Solution: Call `services.AddProductionSecurityServices(configuration)` in Program.cs (post-MVP)
- Post-MVP: Development overrides will be documented alongside the compliance feature

**DirectoryNotFoundException: Audit log directory not found**
- Solution: Ensure the configured directory exists and has write permissions
- Default: `{AppDomain.BaseDirectory}/audit-logs/`

**CryptographicException: Data encryption failed**
- Check: Key generation and cryptographic provider availability
- Verify: No file system permissions issues for key storage

### Monitoring

Monitor these aspects for security health:

1. **Audit Log Volume**: Unexpected increases may indicate security events
2. **Integrity Validation**: Regular checks for tampered logs
3. **Token Replay Attempts**: Monitor for replay attack patterns
4. **Disk Space**: Ensure audit log directory has sufficient space

## Migration from Development

To migrate from development stub implementations:

1. **Update Program.cs**:
   ```csharp
   if (app.Environment.IsProduction())
   {
       builder.Services.AddProductionSecurityServices(builder.Configuration);
   }
   ```

2. **Configure Directories**:
   ```json
   {
     "Security": {
       "AuditLogging": {
         "LogDirectory": "/var/log/honua/audit-logs"
       }
     }
   }
   ```

3. **Set File Permissions**:
   ```bash
   sudo mkdir -p /var/log/honua/audit-logs
   sudo chown honua:honua /var/log/honua/audit-logs
   sudo chmod 750 /var/log/honua/audit-logs
   ```

4. **Validate Configuration**:
   - Test application startup
   - Verify audit log creation
   - Validate encryption/decryption operations

This ensures a secure transition to production-grade security services.
