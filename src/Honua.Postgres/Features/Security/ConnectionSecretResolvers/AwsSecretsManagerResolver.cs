// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Security.ConnectionSecretResolvers;

internal sealed class AwsSecretsManagerResolver : IConnectionSecretResolver, IDisposable
{
    private const string ProviderType = "aws";
    private const string SecretsManagerPrefix = "aws:secretsmanager:";
    private const string SecretsManagerTarget = "secretsmanager.GetSecretValue";
    private const string SecretsClientName = "AwsSecretsManager";
    private const string MetadataClientName = "AwsMetadata";
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(2);
    private static readonly Action<ILogger, Exception?> _logImdsTokenFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(6403, "AwsImdsTokenRequestFailed"), "AWS IMDS token request failed");

    private readonly HttpClient _secretsClient;
    private readonly HttpClient _metadataClient;
    private readonly ILogger<AwsSecretsManagerResolver> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _credentialsLock = new(1, 1);
    private bool _disposed;

    private AwsCredentials? _cachedCredentials;

    public AwsSecretsManagerResolver(IHttpClientFactory httpClientFactory, ILogger<AwsSecretsManagerResolver> logger)
    {
        _secretsClient = httpClientFactory.CreateClient(SecretsClientName);
        _metadataClient = httpClientFactory.CreateClient(MetadataClientName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        if (!secretRef.StartsWith(SecretsManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid AWS secret reference. Expected '{SecretsManagerPrefix}<secret-id>'.", nameof(secretRef));
        }

        if (TryGetCached(secretRef, out var cachedValue))
        {
            return cachedValue;
        }

        var secretIdWithOptions = secretRef[SecretsManagerPrefix.Length..];
        if (string.IsNullOrWhiteSpace(secretIdWithOptions))
        {
            throw new ArgumentException("AWS secret reference must include a secret identifier.", nameof(secretRef));
        }

        var (secretId, versionStage, versionId) = ParseSecretOptions(secretIdWithOptions);
        var region = ResolveRegion(secretId);
        var credentials = await GetCredentialsAsync(cancellationToken).ConfigureAwait(false);

        var endpoint = new Uri($"https://secretsmanager.{region}.amazonaws.com/");
        var payload = BuildPayload(secretId, versionStage, versionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.1");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", SecretsManagerTarget);

        using var response = await SendSignedRequestAsync(request, endpoint, region, credentials, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"AWS Secrets Manager request failed ({(int)response.StatusCode}): {body}");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var secretValue = ExtractSecretValue(responseContent);
        CacheValue(secretRef, secretValue);
        return secretValue;
    }

    public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return Task.FromResult(false);
        }

        if (!secretRef.StartsWith(SecretsManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        var secretIdWithOptions = secretRef[SecretsManagerPrefix.Length..];
        if (string.IsNullOrWhiteSpace(secretIdWithOptions))
        {
            return Task.FromResult(false);
        }

        var secretId = ParseSecretOptions(secretIdWithOptions).SecretId;
        var region = TryResolveRegion(secretId);
        if (string.IsNullOrWhiteSpace(region))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public string[] GetSupportedProviders() => new[] { ProviderType };

    private static byte[] BuildPayload(string secretId, string? versionStage, string? versionId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("SecretId", secretId);
        if (!string.IsNullOrWhiteSpace(versionStage))
        {
            writer.WriteString("VersionStage", versionStage);
        }

        if (!string.IsNullOrWhiteSpace(versionId))
        {
            writer.WriteString("VersionId", versionId);
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private async Task<HttpResponseMessage> SendSignedRequestAsync(
        HttpRequestMessage request,
        Uri endpoint,
        string region,
        AwsCredentials credentials,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        request.Headers.Host = endpoint.Host;
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

        var payloadHash = HashSha256(await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        if (!string.IsNullOrWhiteSpace(credentials.SessionToken))
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        }

        var canonicalRequest = BuildCanonicalRequest(request, payloadHash);
        var credentialScope = $"{dateStamp}/{region}/secretsmanager/aws4_request";
        var stringToSign = BuildStringToSign(amzDate, credentialScope, canonicalRequest);
        var signature = ComputeSignature(credentials.SecretAccessKey, dateStamp, region, stringToSign);
        var signedHeaders = GetSignedHeaders(request);

        var authorization = $"AWS4-HMAC-SHA256 Credential={credentials.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return await _secretsClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildCanonicalRequest(HttpRequestMessage request, string payloadHash)
    {
        var canonicalHeaders = GetCanonicalHeaders(request, out var signedHeaders);
        var path = request.RequestUri?.AbsolutePath ?? "/";
        var method = request.Method.Method;
        return string.Join('\n',
            method,
            path,
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash);
    }

    private static string GetCanonicalHeaders(HttpRequestMessage request, out string signedHeaders)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var header in request.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value).Trim();
        }

        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value).Trim();
            }
        }

        var builder = new StringBuilder();
        foreach (var (key, value) in headers)
        {
            builder.Append(key).Append(':').Append(value).Append('\n');
        }

        signedHeaders = string.Join(";", headers.Keys);
        return builder.ToString();
    }

    private static string GetSignedHeaders(HttpRequestMessage request)
    {
        GetCanonicalHeaders(request, out var signedHeaders);
        return signedHeaders;
    }

    private static string BuildStringToSign(string amzDate, string credentialScope, string canonicalRequest)
    {
        var hashedRequest = HashSha256(Encoding.UTF8.GetBytes(canonicalRequest));
        return string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            hashedRequest);
    }

    private static string ComputeSignature(string secretKey, string dateStamp, string region, string stringToSign)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, "secretsmanager");
        var kSigning = HmacSha256(kService, "aws4_request");

        var signatureBytes = HmacSha256(kSigning, stringToSign);
        return Convert.ToHexString(signatureBytes).ToLowerInvariant();
    }

    private static string HashSha256(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string ExtractSecretValue(string responseContent)
    {
        using var document = JsonDocument.Parse(responseContent);
        var root = document.RootElement;

        if (root.TryGetProperty("SecretString", out var secretStringElement) &&
            secretStringElement.ValueKind == JsonValueKind.String)
        {
            return SecretValueExtractor.ExtractValue(secretStringElement.GetString()!);
        }

        if (root.TryGetProperty("SecretBinary", out var secretBinaryElement) &&
            secretBinaryElement.ValueKind == JsonValueKind.String)
        {
            var decoded = Convert.FromBase64String(secretBinaryElement.GetString()!);
            return SecretValueExtractor.ExtractValue(Encoding.UTF8.GetString(decoded));
        }

        throw new InvalidOperationException("AWS secret response did not contain a SecretString or SecretBinary value.");
    }

    private static (string SecretId, string? VersionStage, string? VersionId) ParseSecretOptions(string secretIdWithOptions)
    {
        var questionIndex = secretIdWithOptions.IndexOf('?', StringComparison.Ordinal);
        if (questionIndex < 0)
        {
            return (secretIdWithOptions, null, null);
        }

        var secretId = secretIdWithOptions[..questionIndex];
        var query = secretIdWithOptions[(questionIndex + 1)..];
        string? versionStage = null;
        string? versionId = null;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kvp = part.Split('=', 2);
            if (kvp.Length != 2)
            {
                continue;
            }

            var key = kvp[0];
            var value = Uri.UnescapeDataString(kvp[1]);
            if (key.Equals("versionStage", StringComparison.OrdinalIgnoreCase))
            {
                versionStage = value;
            }
            else if (key.Equals("versionId", StringComparison.OrdinalIgnoreCase))
            {
                versionId = value;
            }
        }

        return (secretId, versionStage, versionId);
    }

    private static string ResolveRegion(string secretId)
    {
        var region = TryResolveRegion(secretId);
        if (string.IsNullOrWhiteSpace(region))
        {
            throw new InvalidOperationException("AWS region is required. Set AWS_REGION/AWS_DEFAULT_REGION or use a secret ARN.");
        }

        return region;
    }

    private static string? TryResolveRegion(string secretId)
    {
        if (secretId.StartsWith("arn:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = secretId.Split(':', 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && parts[2].Equals("secretsmanager", StringComparison.OrdinalIgnoreCase))
            {
                return parts[3];
            }
        }

        var region = Environment.GetEnvironmentVariable("AWS_REGION");
        if (string.IsNullOrWhiteSpace(region))
        {
            region = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        }

        return region;
    }

    private async Task<AwsCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedCredentials(out var cached))
        {
            return cached;
        }

        await _credentialsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedCredentials(out cached))
            {
                return cached;
            }

            if (TryGetEnvCredentials(out var credentials))
            {
                _cachedCredentials = credentials;
                return credentials;
            }

            credentials = await TryGetEcsCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (credentials != null)
            {
                _cachedCredentials = credentials;
                return credentials;
            }

            credentials = await TryGetImdsCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (credentials != null)
            {
                _cachedCredentials = credentials;
                return credentials;
            }

            throw new InvalidOperationException("AWS credentials are not configured. Set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY or provide a role credential source.");
        }
        finally
        {
            _credentialsLock.Release();
        }
    }

    private bool TryGetCachedCredentials(out AwsCredentials credentials)
    {
        if (_cachedCredentials is { } cached &&
            cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            credentials = cached;
            return true;
        }

        credentials = null!;
        return false;
    }

    private static bool TryGetEnvCredentials(out AwsCredentials credentials)
    {
        var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")
            ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        var sessionToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            credentials = null!;
            return false;
        }

        credentials = new AwsCredentials(accessKey, secretKey, sessionToken, DateTimeOffset.MaxValue);
        return true;
    }

    private async Task<AwsCredentials?> TryGetEcsCredentialsAsync(CancellationToken cancellationToken)
    {
        var relativeUri = Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI");
        var fullUri = Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI");

        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(relativeUri))
        {
            endpoint = new Uri($"http://169.254.170.2{relativeUri}");
        }
        else if (!string.IsNullOrWhiteSpace(fullUri))
        {
            endpoint = new Uri(fullUri);
        }

        if (endpoint == null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var authToken = Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN");
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_metadataTimeout);

        using var response = await _metadataClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return ParseCredentials(content);
    }

    private async Task<AwsCredentials?> TryGetImdsCredentialsAsync(CancellationToken cancellationToken)
    {
        var token = await GetImdsTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/latest/meta-data/iam/security-credentials/");
        listRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_metadataTimeout);

        using var listResponse = await _metadataClient.SendAsync(listRequest, timeout.Token).ConfigureAwait(false);
        if (!listResponse.IsSuccessStatusCode)
        {
            return null;
        }

        var roleName = (await listResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return null;
        }

        using var credsRequest = new HttpRequestMessage(HttpMethod.Get, $"http://169.254.169.254/latest/meta-data/iam/security-credentials/{roleName}");
        credsRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);

        using var credsResponse = await _metadataClient.SendAsync(credsRequest, timeout.Token).ConfigureAwait(false);
        if (!credsResponse.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await credsResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return ParseCredentials(content);
    }

    private async Task<string?> GetImdsTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token");
        request.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "21600");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_metadataTimeout);

        try
        {
            using var response = await _metadataClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logImdsTokenFailed(_logger, ex);
            return null;
        }
    }

    private static AwsCredentials? ParseCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("AccessKeyId", out var accessKeyElement) ||
            !root.TryGetProperty("SecretAccessKey", out var secretKeyElement))
        {
            return null;
        }

        var accessKey = accessKeyElement.GetString();
        var secretKey = secretKeyElement.GetString();
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            return null;
        }

        var sessionToken = root.TryGetProperty("Token", out var tokenElement)
            ? tokenElement.GetString()
            : null;

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        if (root.TryGetProperty("Expiration", out var expirationElement) &&
            DateTimeOffset.TryParse(expirationElement.GetString(), out var parsedExpiration))
        {
            expiresAt = parsedExpiration;
        }

        return new AwsCredentials(accessKey, secretKey, sessionToken, expiresAt);
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
        var expiresAt = DateTimeOffset.UtcNow.Add(_cacheTtl);
        _cache[secretRef] = new CacheEntry(value, expiresAt);
    }

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt);

    private sealed record AwsCredentials(
        string AccessKeyId,
        string SecretAccessKey,
        string? SessionToken,
        DateTimeOffset ExpiresAt);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _credentialsLock.Dispose();
        _disposed = true;
    }
}
