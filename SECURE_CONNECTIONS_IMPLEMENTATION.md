# Secure Data Connections Implementation

## Overview

This implementation provides a comprehensive secure connection management system for the Honua Server, addressing Issue #226. The solution encrypts database credentials at rest, enables runtime updates without restarts, and supports integration with external secret management systems.

## Architecture

### Core Components

1. **Encryption Service** (`IConnectionEncryptionService`)
   - AES-256-GCM encryption with envelope encryption pattern
   - Key versioning for rotation support
   - Master key derived from configuration with PBKDF2

2. **Secret Resolver** (`IConnectionSecretResolver`)
   - Pluggable architecture for multiple secret management providers
   - Environment variable resolver (development/simple deployments)
   - AWS Secrets Manager resolver (HTTP + SigV4, AOT-safe)
   - Azure Key Vault resolver (HTTP + OAuth, AOT-safe)
   - Extensible to custom providers

3. **Secure Connection Registry** (`ISecureConnectionRegistry`)
   - CRUD operations for encrypted connection configurations
   - Supports encrypted storage or secret references

4. **Connection Resolver** (`ISecureConnectionResolver`)
   - Runtime connection string resolution
   - Health checking and monitoring
   - Fallback mechanisms for reliability

## Database Schema

### New Tables

- `honua.data_connections`: Encrypted connection configurations with optional secret references
- `honua.encryption_keys`: Key versioning and rotation

### Security Features

- Connection strings encrypted with AES-GCM
- Mutual exclusion constraints (encrypted OR secret reference)
- SSL enforcement and certificate validation

## API Endpoints

### Admin Endpoints (`/api/v1/admin/connections`)

- `GET /`: List secure connections (metadata only)
- `GET /{id}`: Get connection details
- `POST /`: Create new secure connection
- `PUT /{id}`: Update existing connection
- `DELETE /{id}`: Delete connection
- `POST /{id}/test`: Test connection health

### Security Endpoints

- `POST /encryption/validate`: Validate encryption service
- `POST /encryption/rotate-key`: Rotate encryption keys

## Configuration

### Required Settings

```json
{
  "Security": {
    "ConnectionEncryption": {
      "MasterKey": "secure-32-character-minimum-key",
      "Salt": "base64-encoded-salt-value"
    }
  }
}
```

### Optional Settings

```json
{
  "Database": {
    "SecureConnection": {
      "Name": "primary-database"  // Use named secure connection instead of DefaultConnection
    }
  }
}
```

## Usage Patterns

### 1. Legacy Mode (Backward Compatible)
```csharp
// Continues to use DefaultConnection from configuration
// No changes required to existing code
services.AddPostgreSqlServices(configuration);
```

### 2. Secure Mode
```csharp
// Enable secure connection management
services.AddPostgreSqlServices(configuration);
services.AddSecureConnectionServices(configuration);

// Optionally replace default connection resolution
services.UseSecureConnectionProvider(configuration);
```

### 3. Creating Secure Connections

#### With Encrypted Storage
```csharp
var connection = DataConnection.CreateWithEncryptedCredentials(
    name: "production-db",
    host: "db.example.com",
    port: 5432,
    databaseName: "honua_prod",
    username: "app_user",
    encryptedConnectionString: await encryptionService.EncryptConnectionStringAsync(connectionString),
    encryptionKeyVersion: await encryptionService.GetCurrentKeyVersionAsync(),
    createdBy: "admin"
);

await registry.CreateConnectionAsync(connection);
```

#### With Secret Manager Integration
```csharp
var connection = DataConnection.CreateWithSecretReference(
    name: "production-db",
    host: "db.example.com",
    port: 5432,
    databaseName: "honua_prod",
    username: "app_user",
    secretRef: "aws:secretsmanager:prod-database-credentials",
    secretType: "aws",
    createdBy: "admin"
);

await registry.CreateConnectionAsync(connection);
```

## Security Features

### Defense in Depth

1. **Encryption at Rest**: All connection strings encrypted with AES-256-GCM
2. **Key Rotation**: Versioned encryption keys with backward compatibility
3. **SSL Enforcement**: Require encrypted connections to databases

### Security Boundaries

- **API Responses**: Never expose encrypted data or secrets
- **Error Messages**: Sanitized to prevent information leakage
- **Key Management**: Master keys never persisted in plaintext

## Testing

### Test Categories

- **Unit Tests**: Encryption/decryption, domain model validation
- **Integration Tests**: Database operations, API endpoints
- **Security Tests**: Cryptographic properties, access controls
- **Performance Tests**: Encryption overhead, large-scale operations

### Test Attributes

```csharp
[SecurityTest("Validates encryption round-trip integrity")]
[IntegrationTest]
[Endpoint("POST /api/v1/admin/connections")]
```

## Deployment Considerations

### Key Management

1. **Development**: Use test keys from configuration
2. **Production**: Integrate with proper key management (HSM, KMS)
3. **Backup**: Ensure encrypted connections can be restored

### Migration Strategy

1. **Phase 1**: Deploy secure connection infrastructure (backward compatible)
2. **Phase 2**: Create secure connections for critical databases
3. **Phase 3**: Migrate all connections to secure registry
4. **Phase 4**: Remove legacy DefaultConnection usage

### Monitoring

- Monitor encryption/decryption performance
- Alert on failed connection resolutions
- Health check integration for connection validation

## Extension Points

### Secret Management Providers

```csharp
// Add AWS Secrets Manager support
services.AddAwsSecretsManagerSupport(configuration);

// Add Azure Key Vault support
services.AddAzureKeyVaultSupport(configuration);

// Add HashiCorp Vault support (custom resolver required)
services.AddHashiCorpVaultSupport(configuration);
```

### Custom Secret Resolvers

```csharp
public class CustomSecretResolver : IConnectionSecretResolver
{
    public async Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken)
    {
        // Custom implementation
    }

    // ... other interface members
}

// Register custom resolver
services.AddSingleton<IConnectionSecretResolver, CustomSecretResolver>();
```

## Success Criteria

✅ **All credentials remain encrypted at rest**
- Connection strings encrypted with AES-256-GCM
- Master keys derived securely with PBKDF2
- Key versioning supports rotation

✅ **Admin APIs never expose secrets**
- API responses sanitized of sensitive data
- Separate models for requests/responses
- Comprehensive input validation

✅ **Connection changes take effect without restarts**
- Runtime connection resolution
- Health checking and fallback mechanisms
- Configuration changes reflected immediately

✅ **Support for secret manager integration**
- Pluggable secret resolver architecture
- Environment variable support (development)
- Built-in cloud secret managers (AWS, Azure)

✅ **Key rotation capabilities**
- Versioned encryption keys
- Backward compatibility for old encrypted data
- Administrative endpoints for key management

✅ **Comprehensive test coverage**
- Unit tests for cryptographic operations
- Integration tests for API functionality
- Security tests for access controls
- Performance tests for scalability

## Files Created/Modified

### Core Domain (`src/Honua.Core/Features/Security/`)
- `Abstractions/IConnectionEncryptionService.cs`
- `Abstractions/IConnectionSecretResolver.cs`
- `Abstractions/ISecureConnectionRegistry.cs`
- `Domain/DataConnection.cs`

### Postgres Implementation (`src/Honua.Postgres/Features/Security/`)
- `ConnectionEncryptionService.cs`
- `PostgresSecureConnectionRegistry.cs`
- `SecureConnectionResolver.cs`
- `SecureConnectionAwareDatabaseProvider.cs`
- `ConnectionSecretResolvers/NullSecretResolver.cs`
- `ConnectionSecretResolvers/CompositeSecretResolver.cs`
- `SecurityServiceCollectionExtensions.cs`

### Server API (`src/Honua.Server/Features/Admin/`)
- `SecureConnectionEndpoints.cs`
- `Models/SecureConnectionModels.cs`
- `Models/SecureConnectionJsonContext.cs`

### Database Schema
- `src/Honua.Server/Migrations/006_CreateSecureConnectionRegistry.sql`

### Tests
- `tests/Honua.Postgres.Tests/Features/Security/ConnectionEncryptionServiceTests.cs`
- `tests/Honua.Postgres.Tests/Features/Security/SecureConnectionRegistryTests.cs`
- `tests/Honua.Server.Tests/Features/Admin/SecureConnectionEndpointsTests.cs`
- `tests/Honua.TestKit/Attributes/SecurityTestAttribute.cs`

This implementation provides a robust, secure, and extensible foundation for managing database connections while maintaining backward compatibility and following security best practices.
