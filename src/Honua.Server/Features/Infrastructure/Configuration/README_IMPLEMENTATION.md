# Centralized Secret Management and Configuration Standardization Implementation

This implementation provides a comprehensive solution for centralized secret management and configuration validation standardization across the Honua server application.

## Overview

The implementation consists of several key components:

1. **ISecretProvider** - Centralized secret management abstraction
2. **Configuration Validation Attributes** - Enhanced validation with environment awareness  
3. **StandardTtlOptions** - Standardized cache TTL configuration
4. **ConfigurationValidator** - Comprehensive validation and discovery service
5. **Service Extensions** - Easy integration with ASP.NET Core DI

## Key Features

### Centralized Secret Management
- Unified interface for Azure Key Vault, AWS Secrets Manager, and environment variables
- Automatic fallback chain: Cloud secrets → Environment variables → Configuration files
- Caching with configurable TTL and size limits
- Audit logging for secret access patterns
- Startup validation without exposing secret values

### Enhanced Configuration Validation
- Custom validation attributes with environment awareness
- Automatic environment variable name generation
- Suggested fixes in error messages
- Consistent TTL validation across all cache layers
- Configuration discovery for documentation generation

### Standardized Cache TTL Management
- Five standardized TTL categories (VeryShort, Short, Medium, Long, VeryLong, Negative)
- Environment-aware defaults (reduced TTLs in development)
- Consistency validation across TTL tiers
- Jitter support to prevent cache stampedes

## Usage Examples

### 1. Basic Secret Reference Configuration

```json
{
  "Database": {
    "ConnectionString": "azure:keyvault:my-vault:database-connection"
  },
  "Cache": {
    "ConnectionString": "env:REDIS_CONNECTION_STRING"  
  }
}
```

### 2. Enhanced Options Class with Validation

```csharp
public sealed class MyServiceOptions
{
    public const string SectionName = "MyService";

    [RequiredConfiguration(
        ConfigurationPath = SectionName,
        SuggestedFix = "Set ApiUrl to your service endpoint")]
    [ValidUrl(
        RequiredSchemes = new[] { "https" },
        RequireHttpsInProduction = true)]
    public string ApiUrl { get; set; } = string.Empty;

    [SecretReference(
        AllowedProviders = new[] { "env", "azure", "aws" },
        AllowPlainTextInDevelopment = true)]
    public string ApiKey { get; set; } = string.Empty;

    [ValidTtl(
        MinimumTtl = "00:01:00",
        MaximumTtl = "01:00:00",
        WarnInDevelopment = true)]
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(15);
}
```

### 3. Service Registration

```csharp
// In Program.cs
builder.Services.AddStandardConfiguration(builder.Configuration, builder.Environment.IsDevelopment());

// Register specific options with validation and secret resolution
builder.Services.ConfigureWithValidation<MyServiceOptions>(
    builder.Configuration, 
    MyServiceOptions.SectionName,
    isRequired: true,
    enableSecretResolution: true);
```

### 4. Standardized TTL Configuration

```json
{
  "StandardTtl": {
    "VeryShort": "00:00:30",  // 30 seconds - real-time data
    "Short": "00:05:00",      // 5 minutes - query results
    "Medium": "00:30:00",     // 30 minutes - metadata
    "Long": "04:00:00",       // 4 hours - configuration
    "VeryLong": "24:00:00",   // 24 hours - static data
    "Negative": "00:01:00"    // 1 minute - failed lookups
  }
}
```

## Integration Steps

### 1. Update Program.cs

Add the new configuration services early in the service registration:

```csharp
// After builder creation, before other service registration
builder.Services.AddStandardConfiguration(builder.Configuration, builder.Environment.IsDevelopment());

// Register existing options with enhanced validation
builder.Services.ConfigureWithValidation<DatabaseOptions>(
    builder.Configuration, 
    DatabaseOptions.SectionName);

builder.Services.ConfigureWithValidation<CacheOptions>(
    builder.Configuration, 
    CacheOptions.SectionName);
```

### 2. Update Existing Options Classes

Add validation attributes to existing options:

```csharp
public sealed class ExistingOptions
{
    [RequiredConfiguration(ConfigurationPath = "Existing")]
    public string RequiredValue { get; set; } = string.Empty;
    
    [SecretReference(AllowPlainTextInDevelopment = true)]
    public string SecretValue { get; set; } = string.Empty;
}
```

### 3. Environment Variables

The system automatically maps configuration paths to environment variables:

- `Database:ConnectionString` → `Database__ConnectionString`
- `Cache:Redis:Password` → `Cache__Redis__Password`

### 4. Secret References

Support for multiple secret providers:

- `env:SECRET_NAME` - Environment variable
- `azure:keyvault:vault-name:secret-name` - Azure Key Vault
- `aws:secretsmanager:secret-id` - AWS Secrets Manager
- `vault:secret/path` - HashiCorp Vault (future)

## Configuration Validation

### Startup Validation

The system validates all configuration during startup:

```text
[INFO] Starting configuration validation...
[INFO] Configuration validation completed successfully. Validated 8 sections
```

Error example:
```text
[ERROR] Configuration error: [Database] Configuration 'Database:ConnectionString' - is required. Set environment variable 'Database__ConnectionString' or add to appsettings.json.
```

### TTL Consistency Validation

Ensures TTL values follow the hierarchy: VeryShort ≤ Short ≤ Medium ≤ Long ≤ VeryLong

### Secret Reference Validation

Tests secret references without retrieving values:
- Validates reference format
- Checks provider availability  
- Verifies access permissions
- Reports configuration errors

## Security Considerations

### Secret Handling
- Secrets are never logged in plain text
- Cache entries are automatically cleaned up
- Secret references are masked in logs
- Startup validation doesn't expose secret values

### Environment-Aware Security
- Plain text secrets forbidden in production
- HTTPS required for URLs in production
- Stronger validation in production environments
- Development mode allows relaxed validation

## Performance Optimizations

### Caching Strategy
- 5-minute TTL for secrets by default
- LRU eviction with configurable max size
- Background cleanup of expired entries
- Concurrent access protection

### Validation Efficiency
- Parallel validation of multiple configuration sections
- Cached validation metadata
- Minimal reflection overhead
- Fast startup validation

## Error Messages and Diagnostics

### Enhanced Error Messages
```text
Configuration 'Database:ConnectionString' - is required. 
Set environment variable 'Database__ConnectionString' or add to appsettings.json. 
Suggested fix: Use secret reference format: provider:path
```

### Startup Diagnostics
- Configuration summary logging
- Feature flag status reporting
- Secret reference validation results
- TTL consistency checking

## Migration Guide

### From Existing Configuration

1. Install new validation attributes on existing options classes
2. Update Program.cs to use `AddStandardConfiguration()`
3. Migrate sensitive values to secret references
4. Adopt standardized TTL categories
5. Test validation in development environment

### Backward Compatibility

- Existing configuration continues to work
- Validation is opt-in per options class
- Secret resolution is optional
- Plain text values allowed in development

## Testing

### Unit Tests
- Mock secret providers for testing
- Validation attribute testing
- TTL calculation verification
- Error message validation

### Integration Tests  
- End-to-end secret resolution
- Startup validation scenarios
- Configuration binding validation
- Error handling verification

## Best Practices

### Configuration Design
- Use secret references for sensitive data
- Adopt standardized TTL categories
- Add validation attributes to all options
- Provide helpful error messages and suggested fixes

### Secret Management
- Use cloud secret management in production
- Rotate secrets regularly
- Monitor secret access patterns
- Validate secret references at startup

### Performance
- Cache frequently accessed secrets
- Use appropriate TTL categories
- Monitor cache hit rates
- Clean up expired entries

This implementation provides a solid foundation for secure, validated, and maintainable configuration management across the Honua platform.