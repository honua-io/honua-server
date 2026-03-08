# Security Guide

This guide covers security best practices when developing mobile applications with the Honua Mobile SDK.

## Overview

Mobile applications handling geospatial data often contain sensitive information about locations, infrastructure, and field operations. Proper security implementation is crucial for protecting data and maintaining user privacy.

## API Security

### API Key Management

Never embed API keys directly in your code:

```csharp
// ❌ Bad: Hard-coded API key
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();
    builder.Services.AddHonuaMobile(options =>
    {
        options.ApiKey = "sk-1234567890abcdef"; // Exposed in compiled app
    });
    return builder.Build();
}

// ✅ Good: Use secure storage or configuration
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();
    builder.Services.AddHonuaMobile(options =>
    {
        options.ApiKey = GetSecureApiKey(); // Retrieved from secure storage
    });
    return builder.Build();
}

private static string GetSecureApiKey()
{
    // Retrieve from secure storage, environment, or remote config
    return SecureStorage.GetAsync("honua_api_key").Result ??
           throw new SecurityException("API key not configured");
}
```

### Secure API Key Storage

Implement secure key management:

```csharp
public class SecureApiKeyManager
{
    private const string ApiKeyAlias = "honua_api_key";

    public async Task<string> GetApiKeyAsync()
    {
        try
        {
            var apiKey = await SecureStorage.GetAsync(ApiKeyAlias);

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new SecurityException("API key not found in secure storage");
            }

            return apiKey;
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Failed to retrieve API key: {ex.Message}", ex);
        }
    }

    public async Task SetApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key cannot be null or empty");
        }

        try
        {
            await SecureStorage.SetAsync(ApiKeyAlias, apiKey);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Failed to store API key: {ex.Message}", ex);
        }
    }

    public async Task ClearApiKeyAsync()
    {
        try
        {
            SecureStorage.Remove(ApiKeyAlias);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Failed to clear API key: {ex.Message}", ex);
        }
    }
}
```

### Certificate Pinning

Implement certificate pinning for production:

```csharp
public class SecureHttpClientHandler : HttpClientHandler
{
    private readonly HashSet<string> _pinnedCertificates;

    public SecureHttpClientHandler(IEnumerable<string> pinnedCertificates)
    {
        _pinnedCertificates = new HashSet<string>(pinnedCertificates);

        ServerCertificateCustomValidationCallback = ValidateServerCertificate;
    }

    private bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2 certificate,
        X509Chain chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // Allow certificate validation to proceed normally in debug mode
#if DEBUG
        return true;
#endif

        // Check for basic SSL errors
        if (sslPolicyErrors != SslPolicyErrors.None)
        {
            return false;
        }

        // Validate certificate pinning
        var publicKeyHash = GetCertificateHash(certificate);
        return _pinnedCertificates.Contains(publicKeyHash);
    }

    private string GetCertificateHash(X509Certificate2 certificate)
    {
        var publicKey = certificate.GetPublicKey();
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(publicKey);
        return Convert.ToBase64String(hash);
    }
}
```

## Data Protection

### Sensitive Data Identification

Identify and protect sensitive data:

```csharp
public class SensitiveDataHandler
{
    private static readonly HashSet<string> SensitiveFields = new()
    {
        "social_security_number",
        "phone_number",
        "email",
        "address",
        "gps_coordinates", // If location data is sensitive
        "personal_notes"
    };

    public Feature SanitizeFeatureForLogging(Feature feature)
    {
        var sanitized = new Feature
        {
            Id = feature.Id,
            Geometry = feature.Geometry, // May need redaction based on requirements
            Attributes = new Dictionary<string, object>()
        };

        foreach (var attr in feature.Attributes)
        {
            if (SensitiveFields.Contains(attr.Key.ToLower()))
            {
                sanitized.Attributes[attr.Key] = "[REDACTED]";
            }
            else
            {
                sanitized.Attributes[attr.Key] = attr.Value;
            }
        }

        return sanitized;
    }
}
```

### Data Encryption

Encrypt sensitive data at rest:

```csharp
public class EncryptedDataStorage
{
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDataProtector _protector;

    public EncryptedDataStorage(IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _protector = _dataProtectionProvider.CreateProtector("HonuaFieldData");
    }

    public async Task SaveEncryptedFeatureAsync(Feature feature)
    {
        try
        {
            var json = JsonSerializer.Serialize(feature);
            var encryptedData = _protector.Protect(json);

            await File.WriteAllTextAsync(GetFeatureFilePath(feature.Id), encryptedData);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Failed to encrypt and save feature: {ex.Message}", ex);
        }
    }

    public async Task<Feature?> LoadEncryptedFeatureAsync(int featureId)
    {
        try
        {
            var filePath = GetFeatureFilePath(featureId);

            if (!File.Exists(filePath))
            {
                return null;
            }

            var encryptedData = await File.ReadAllTextAsync(filePath);
            var json = _protector.Unprotect(encryptedData);

            return JsonSerializer.Deserialize<Feature>(json);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Failed to decrypt feature: {ex.Message}", ex);
        }
    }

    private string GetFeatureFilePath(int featureId)
    {
        var appDataPath = FileSystem.AppDataDirectory;
        return Path.Combine(appDataPath, "encrypted", $"feature_{featureId}.enc");
    }
}
```

## Authentication & Authorization

### User Authentication

Implement secure user authentication:

```csharp
public class AuthenticationService
{
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _tokenExpiry;

    public async Task<bool> AuthenticateAsync(string username, string password)
    {
        try
        {
            var loginRequest = new LoginRequest
            {
                Username = username,
                Password = password // Should be hashed/encrypted in transit
            };

            var response = await _httpClient.PostAsJsonAsync("/auth/login", loginRequest);

            if (response.IsSuccessStatusCode)
            {
                var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();

                _accessToken = authResponse.AccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn);

                // Store refresh token securely
                await SecureStorage.SetAsync("refresh_token", authResponse.RefreshToken);

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Authentication failed: {ex.Message}", ex);
        }
    }

    public async Task<string> GetValidTokenAsync()
    {
        if (_accessToken == null || DateTime.UtcNow >= _tokenExpiry.AddMinutes(-5))
        {
            await RefreshTokenAsync();
        }

        return _accessToken ?? throw new SecurityException("No valid authentication token");
    }

    private async Task RefreshTokenAsync()
    {
        var refreshToken = await SecureStorage.GetAsync("refresh_token");

        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new SecurityException("No refresh token available");
        }

        var refreshRequest = new RefreshTokenRequest { RefreshToken = refreshToken };
        var response = await _httpClient.PostAsJsonAsync("/auth/refresh", refreshRequest);

        if (response.IsSuccessStatusCode)
        {
            var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();

            _accessToken = authResponse.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn);

            await SecureStorage.SetAsync("refresh_token", authResponse.RefreshToken);
        }
        else
        {
            // Refresh failed, user needs to log in again
            await LogoutAsync();
            throw new SecurityException("Token refresh failed, re-authentication required");
        }
    }

    public async Task LogoutAsync()
    {
        _accessToken = null;
        _tokenExpiry = DateTime.MinValue;

        SecureStorage.Remove("refresh_token");

        // Clear any cached sensitive data
        await ClearCachedDataAsync();
    }
}
```

### Role-Based Access Control

Implement role-based feature access:

```csharp
public class AuthorizationService
{
    private readonly AuthenticationService _authService;

    public AuthorizationService(AuthenticationService authService)
    {
        _authService = authService;
    }

    public async Task<bool> CanUserPerformActionAsync(string action, string resource)
    {
        try
        {
            var token = await _authService.GetValidTokenAsync();
            var userClaims = ExtractClaimsFromToken(token);

            return HasPermission(userClaims, action, resource);
        }
        catch
        {
            return false; // Deny access on error
        }
    }

    private bool HasPermission(IEnumerable<Claim> claims, string action, string resource)
    {
        var roles = claims.Where(c => c.Type == "role").Select(c => c.Value);

        // Define role permissions
        var permissions = new Dictionary<string, HashSet<string>>
        {
            ["admin"] = new() { "create", "read", "update", "delete" },
            ["inspector"] = new() { "create", "read", "update" },
            ["viewer"] = new() { "read" }
        };

        return roles.Any(role =>
            permissions.ContainsKey(role) &&
            permissions[role].Contains(action));
    }

    public async Task<T> SecureOperationAsync<T>(
        string requiredAction,
        string resource,
        Func<Task<T>> operation)
    {
        if (!await CanUserPerformActionAsync(requiredAction, resource))
        {
            throw new UnauthorizedAccessException(
                $"User not authorized to perform {requiredAction} on {resource}");
        }

        return await operation();
    }
}
```

## Secure Communication

### gRPC Security Configuration

Configure secure gRPC communication:

```csharp
public static class SecureGrpcChannelFactory
{
    public static GrpcChannel CreateSecureChannel(string address, string apiKey)
    {
        var handler = new SecureHttpClientHandler(GetPinnedCertificates());

        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpClient = httpClient,
            MaxReceiveMessageSize = 4 * 1024 * 1024, // 4MB limit
            MaxSendMessageSize = 4 * 1024 * 1024,
            LoggerFactory = CreateSecureLoggerFactory()
        });
    }

    private static string[] GetPinnedCertificates()
    {
        // Return production certificate hashes
        return new[]
        {
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", // Production cert hash
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB="  // Backup cert hash
        };
    }

    private static ILoggerFactory CreateSecureLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.AddFilter("Grpc", LogLevel.Warning); // Avoid logging sensitive data
        });
    }
}
```

### Request Signing

Implement request signing for critical operations:

```csharp
public class SignedRequestHandler : DelegatingHandler
{
    private readonly string _secretKey;

    public SignedRequestHandler(string secretKey)
    {
        _secretKey = secretKey ?? throw new ArgumentNullException(nameof(secretKey));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await SignRequestAsync(request);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task SignRequestAsync(HttpRequestMessage request)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N")[..16];

        string bodyHash = "";
        if (request.Content != null)
        {
            var body = await request.Content.ReadAsByteArrayAsync();
            using var sha256 = SHA256.Create();
            bodyHash = Convert.ToBase64String(sha256.ComputeHash(body));
        }

        var message = $"{request.Method}|{request.RequestUri?.PathAndQuery}|{timestamp}|{nonce}|{bodyHash}";
        var signature = ComputeHMACSHA256(message, _secretKey);

        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Nonce", nonce);
        request.Headers.Add("X-Signature", signature);
    }

    private static string ComputeHMACSHA256(string message, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);

        return Convert.ToBase64String(hashBytes);
    }
}
```

## Secure Storage

### File System Security

Implement secure file storage:

```csharp
public class SecureFileManager
{
    private readonly string _secureDirectory;

    public SecureFileManager()
    {
        _secureDirectory = Path.Combine(FileSystem.AppDataDirectory, "secure");
        Directory.CreateDirectory(_secureDirectory);

        // Set restrictive permissions (platform-specific)
        SetSecurePermissions(_secureDirectory);
    }

    public async Task WriteSecureFileAsync(string fileName, byte[] data)
    {
        var filePath = Path.Combine(_secureDirectory, SanitizeFileName(fileName));

        try
        {
            // Encrypt data before writing
            var encryptedData = EncryptData(data);
            await File.WriteAllBytesAsync(filePath, encryptedData);

            // Set file permissions
            SetSecureFilePermissions(filePath);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Failed to write secure file: {ex.Message}", ex);
        }
    }

    public async Task<byte[]> ReadSecureFileAsync(string fileName)
    {
        var filePath = Path.Combine(_secureDirectory, SanitizeFileName(fileName));

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Secure file not found: {fileName}");
        }

        try
        {
            var encryptedData = await File.ReadAllBytesAsync(filePath);
            return DecryptData(encryptedData);
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Failed to read secure file: {ex.Message}", ex);
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        // Remove or replace dangerous characters
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }

    private void SetSecurePermissions(string path)
    {
#if ANDROID
        // Android: Files in app data directory are already secured by app sandbox
#elif IOS
        // iOS: Set file protection attributes
        Foundation.NSFileManager.DefaultManager.SetAttributes(
            new Foundation.NSDictionary(Foundation.NSFileProtectionKey.Complete),
            path,
            out _
        );
#endif
    }

    private void SetSecureFilePermissions(string filePath)
    {
        // Platform-specific file permission setting
        // Implementation varies by platform
    }

    private byte[] EncryptData(byte[] data)
    {
        // Implement encryption using platform-provided APIs
        // This is a simplified example
        return data; // Replace with actual encryption
    }

    private byte[] DecryptData(byte[] encryptedData)
    {
        // Implement decryption
        return encryptedData; // Replace with actual decryption
    }
}
```

## Privacy Protection

### Location Privacy

Implement location privacy measures:

```csharp
public class PrivacyAwareLocationService
{
    public async Task<Location?> GetPrivacyAwareLocationAsync(bool allowPreciseLocation = false)
    {
        var location = await Geolocation.GetLocationAsync();

        if (location == null) return null;

        // Apply privacy protection based on user preferences
        if (!allowPreciseLocation)
        {
            return FuzzLocation(location);
        }

        return location;
    }

    private Location FuzzLocation(Location originalLocation)
    {
        // Add random noise to coordinates (typically 10-100 meters)
        var random = new Random();
        var latNoise = (random.NextDouble() - 0.5) * 0.001; // ~100m
        var lonNoise = (random.NextDouble() - 0.5) * 0.001;

        return new Location
        {
            Latitude = originalLocation.Latitude + latNoise,
            Longitude = originalLocation.Longitude + lonNoise,
            Accuracy = Math.Max(originalLocation.Accuracy ?? 0, 100), // Minimum 100m accuracy
            Timestamp = originalLocation.Timestamp
        };
    }

    public async Task<Feature> CreatePrivacyAwareFeatureAsync(
        Dictionary<string, object> attributes,
        bool includeLocation = true,
        bool allowPreciseLocation = false)
    {
        var feature = new Feature
        {
            Attributes = new Dictionary<string, object>(attributes)
        };

        if (includeLocation)
        {
            var location = await GetPrivacyAwareLocationAsync(allowPreciseLocation);
            if (location != null)
            {
                feature.Geometry = new Point(location.Longitude, location.Latitude);
            }
        }

        // Remove or anonymize PII
        RemovePersonallyIdentifiableInformation(feature.Attributes);

        return feature;
    }

    private void RemovePersonallyIdentifiableInformation(Dictionary<string, object> attributes)
    {
        var piiFields = new[] { "email", "phone", "ssn", "full_name" };

        foreach (var field in piiFields)
        {
            if (attributes.ContainsKey(field))
            {
                attributes[field] = "[ANONYMIZED]";
            }
        }
    }
}
```

## Security Auditing

### Audit Logging

Implement security audit logging:

```csharp
public class SecurityAuditLogger
{
    private readonly ILogger<SecurityAuditLogger> _logger;

    public SecurityAuditLogger(ILogger<SecurityAuditLogger> logger)
    {
        _logger = logger;
    }

    public void LogSecurityEvent(SecurityEvent securityEvent)
    {
        var auditEntry = new
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = securityEvent.EventType,
            UserId = securityEvent.UserId,
            Action = securityEvent.Action,
            Resource = securityEvent.Resource,
            Success = securityEvent.Success,
            IPAddress = securityEvent.IPAddress,
            UserAgent = securityEvent.UserAgent,
            Details = securityEvent.Details
        };

        _logger.LogInformation("Security event: {@AuditEntry}", auditEntry);

        // Send to security monitoring system if configured
        if (securityEvent.Severity >= SecurityEventSeverity.High)
        {
            _ = Task.Run(() => SendToSecurityMonitoringAsync(auditEntry));
        }
    }

    public void LogAuthenticationAttempt(string userId, bool success, string? failureReason = null)
    {
        LogSecurityEvent(new SecurityEvent
        {
            EventType = "Authentication",
            UserId = userId,
            Action = "Login",
            Success = success,
            Details = failureReason,
            Severity = success ? SecurityEventSeverity.Normal : SecurityEventSeverity.Medium
        });
    }

    public void LogDataAccess(string userId, string action, string resource)
    {
        LogSecurityEvent(new SecurityEvent
        {
            EventType = "DataAccess",
            UserId = userId,
            Action = action,
            Resource = resource,
            Success = true,
            Severity = SecurityEventSeverity.Low
        });
    }

    private async Task SendToSecurityMonitoringAsync(object auditEntry)
    {
        // Implement integration with security monitoring system
        // (SIEM, logging service, etc.)
    }
}

public class SecurityEvent
{
    public string EventType { get; set; } = "";
    public string? UserId { get; set; }
    public string? Action { get; set; }
    public string? Resource { get; set; }
    public bool Success { get; set; }
    public string? IPAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Details { get; set; }
    public SecurityEventSeverity Severity { get; set; } = SecurityEventSeverity.Normal;
}

public enum SecurityEventSeverity
{
    Low,
    Normal,
    Medium,
    High,
    Critical
}
```

## Security Testing

### Security Test Cases

Implement security-focused tests:

```csharp
[TestClass]
public class SecurityTests
{
    [TestMethod]
    public void ApiKey_ShouldNotBeHardCoded()
    {
        var assembly = typeof(App).Assembly;
        var assemblyCode = File.ReadAllText(assembly.Location);

        // This is a simplified check - use static analysis tools for thorough testing
        Assert.IsFalse(assemblyCode.Contains("sk-"), "API keys should not be hard-coded");
    }

    [TestMethod]
    public async Task SecureStorage_ShouldEncryptData()
    {
        var sensitiveData = "sensitive_information";
        var key = "test_key";

        await SecureStorage.SetAsync(key, sensitiveData);
        var retrieved = await SecureStorage.GetAsync(key);

        Assert.AreEqual(sensitiveData, retrieved);

        // Verify data is encrypted on disk (platform-specific test)
        // Implementation depends on platform access to underlying storage
    }

    [TestMethod]
    public void PasswordValidation_ShouldEnforceComplexity()
    {
        var validator = new PasswordValidator();

        Assert.IsFalse(validator.IsValid("password123"));     // Too simple
        Assert.IsFalse(validator.IsValid("pass"));            // Too short
        Assert.IsTrue(validator.IsValid("SecureP@ssw0rd!"));  // Complex enough
    }

    [TestMethod]
    public void LocationData_ShouldBeFuzzedWhenRequested()
    {
        var originalLocation = new Location(37.7749, -122.4194); // San Francisco
        var privacyService = new PrivacyAwareLocationService();

        var fuzzedLocation = privacyService.FuzzLocation(originalLocation);

        var distance = CalculateDistance(originalLocation, fuzzedLocation);

        Assert.IsTrue(distance > 10 && distance < 200,
            "Fuzzed location should be 10-200 meters from original");
    }
}
```

## Best Practices Summary

1. **Never hard-code secrets** in your application code
2. **Use secure storage** for sensitive data like API keys and tokens
3. **Implement proper authentication** and authorization
4. **Encrypt sensitive data** at rest and in transit
5. **Use certificate pinning** for production applications
6. **Implement audit logging** for security events
7. **Follow privacy-by-design** principles
8. **Regularly update dependencies** to patch security vulnerabilities
9. **Test security controls** thoroughly
10. **Monitor for security incidents** and respond appropriately

## Related Documentation

- [Mobile SDK Overview](../README.md)
- [Troubleshooting](troubleshooting.md)
- [Performance Guide](performance.md)
- [Offline Sync](offline-sync.md)