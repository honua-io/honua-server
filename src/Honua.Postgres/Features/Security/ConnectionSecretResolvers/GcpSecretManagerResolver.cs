// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Security.ConnectionSecretResolvers;

internal sealed class GcpSecretManagerResolver : IConnectionSecretResolver, IDisposable
{
    private const string ProviderType = "gcp";
    private const string SecretManagerPrefix = "gcp:secretmanager:";
    private const string MetadataTokenUri = "http://169.254.169.254/computeMetadata/v1/instance/service-accounts/default/token";
    private const string TokenScope = "https://www.googleapis.com/auth/cloud-platform";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";
    private const string ClientName = "GcpSecretManager";
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Action<ILogger, Exception?> _logMetadataTokenFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(6401, "GcpMetadataTokenRequestFailed"), "GCP metadata token request failed");

    private readonly HttpClient _httpClient;
    private readonly ILogger<GcpSecretManagerResolver> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private bool _disposed;

    private AccessTokenCache? _tokenCache;

    public GcpSecretManagerResolver(IHttpClientFactory httpClientFactory, ILogger<GcpSecretManagerResolver> logger)
    {
        _httpClient = httpClientFactory.CreateClient(ClientName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        if (!secretRef.StartsWith(SecretManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid GCP secret reference. Expected '{SecretManagerPrefix}<project>:<secret>[:<version>]'.", nameof(secretRef));
        }

        if (TryGetCached(secretRef, out var cached))
        {
            return cached;
        }

        var (projectId, secretId, version) = ParseSecretReference(secretRef);
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var url = $"https://secretmanager.googleapis.com/v1/projects/{projectId}/secrets/{secretId}/versions/{version}:access";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"GCP Secret Manager request failed ({(int)response.StatusCode}): {body}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var secretValue = ExtractSecretValue(content);
        CacheValue(secretRef, secretValue);
        return secretValue;
    }

    public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef) ||
            !secretRef.StartsWith(SecretManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        try
        {
            var parsed = ParseSecretReference(secretRef);
            return Task.FromResult(!string.IsNullOrWhiteSpace(parsed.ProjectId) && !string.IsNullOrWhiteSpace(parsed.SecretId));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(false);
        }
    }

    public string[] GetSupportedProviders() => new[] { ProviderType };

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var token))
        {
            return token;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedToken(out token))
            {
                return token;
            }

            var metadataToken = await RequestMetadataTokenAsync(cancellationToken).ConfigureAwait(false);
            if (metadataToken != null)
            {
                UpdateTokenCache(metadataToken);
                return metadataToken.AccessToken;
            }

            var serviceAccountToken = await RequestServiceAccountTokenAsync(cancellationToken).ConfigureAwait(false);
            if (serviceAccountToken != null)
            {
                UpdateTokenCache(serviceAccountToken);
                return serviceAccountToken.AccessToken;
            }

            throw new InvalidOperationException("GCP credentials are not configured. Set GOOGLE_APPLICATION_CREDENTIALS or run on GCP with metadata server enabled.");
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<TokenResponse?> RequestMetadataTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MetadataTokenUri);
        request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseTokenResponse(content);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logMetadataTokenFailed(_logger, ex);
            return null;
        }
    }

    private async Task<TokenResponse?> RequestServiceAccountTokenAsync(CancellationToken cancellationToken)
    {
        var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (string.IsNullOrWhiteSpace(credentialsPath) || !File.Exists(credentialsPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(credentialsPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!TryGetProperty(root, "client_email", out var clientEmail) ||
            !TryGetProperty(root, "private_key", out var privateKey))
        {
            return null;
        }

        var tokenUri = TryGetProperty(root, "token_uri", out var parsedTokenUri)
            ? parsedTokenUri
            : DefaultTokenUri;

        var jwt = BuildJwt(clientEmail, privateKey, tokenUri);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = jwt
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(form)
        };

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseTokenResponse(content);
    }

    private static string BuildJwt(string clientEmail, string privateKeyPem, string tokenUri)
    {
        var now = DateTimeOffset.UtcNow;
        var issuedAt = now.ToUnixTimeSeconds();
        var expiresAt = now.AddMinutes(55).ToUnixTimeSeconds();

        var headerBytes = BuildJwtHeader();
        var payloadBytes = BuildJwtPayload(clientEmail, tokenUri, issuedAt, expiresAt);

        var header = Base64UrlEncode(headerBytes);
        var payload = Base64UrlEncode(payloadBytes);
        var unsignedToken = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsignedToken), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static byte[] BuildJwtHeader()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("alg", "RS256");
        writer.WriteString("typ", "JWT");
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildJwtPayload(string clientEmail, string tokenUri, long issuedAt, long expiresAt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("iss", clientEmail);
        writer.WriteString("scope", TokenScope);
        writer.WriteString("aud", tokenUri);
        writer.WriteNumber("iat", issuedAt);
        writer.WriteNumber("exp", expiresAt);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data)
    {
        var base64 = Convert.ToBase64String(data);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static TokenResponse ParseTokenResponse(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
            ? seconds
            : 3600;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("GCP token response did not include access_token.");
        }

        return new TokenResponse(accessToken!, expiresIn);
    }

    private bool TryGetCachedToken(out string token)
    {
        if (_tokenCache is { } cache &&
            cache.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            token = cache.AccessToken;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private void UpdateTokenCache(TokenResponse response)
    {
        _tokenCache = new AccessTokenCache(
            response.AccessToken,
            DateTimeOffset.UtcNow.AddSeconds(response.ExpiresInSeconds));
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out string value)
    {
        if (root.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = element.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string ExtractSecretValue(string content)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("data", out var dataElement) ||
            dataElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("GCP Secret Manager response did not include payload data.");
        }

        var decoded = Convert.FromBase64String(dataElement.GetString()!);
        return SecretValueExtractor.ExtractValue(Encoding.UTF8.GetString(decoded));
    }

    private static (string ProjectId, string SecretId, string Version) ParseSecretReference(string secretRef)
    {
        var trimmed = secretRef[SecretManagerPrefix.Length..];
        var parts = trimmed.Split(':', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException($"Invalid GCP secret reference. Expected '{SecretManagerPrefix}<project>:<secret>[:<version>]'.", nameof(secretRef));
        }

        var projectId = parts.Length > 1 ? parts[0] : ResolveDefaultProject();
        var secretId = parts.Length > 1 ? parts[1] : parts[0];
        var version = parts.Length == 3 ? parts[2] : "latest";

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(secretId))
        {
            throw new ArgumentException($"Invalid GCP secret reference. Expected '{SecretManagerPrefix}<project>:<secret>[:<version>]'.", nameof(secretRef));
        }

        return (projectId, secretId, version);
    }

    private static string ResolveDefaultProject()
    {
        return Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
               ?? Environment.GetEnvironmentVariable("GOOGLE_PROJECT")
               ?? string.Empty;
    }

    private bool TryGetCached(string secretRef, out string value)
    {
        if (_cache.TryGetValue(secretRef, out var entry) &&
            entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            value = entry.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private void CacheValue(string secretRef, string value)
    {
        _cache[secretRef] = new CacheEntry(value, DateTimeOffset.UtcNow.Add(_cacheTtl));
    }

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(string AccessToken, int ExpiresInSeconds);

    private sealed record AccessTokenCache(string AccessToken, DateTimeOffset ExpiresAt);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _tokenLock.Dispose();
        _disposed = true;
    }
}
