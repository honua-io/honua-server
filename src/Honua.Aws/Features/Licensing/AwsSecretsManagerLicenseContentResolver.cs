// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Logging;

namespace Honua.Aws.Features.Licensing;

/// <summary>
/// Resolves a signed license envelope from AWS Secrets Manager. Confined to
/// <c>Honua.Aws</c> so the AWSSDK.SecretsManager surface stays out of the
/// cloud-neutral <c>Honua.Hosting</c> licensing pipeline (cloud-SDK isolation
/// contract, <c>CloudSdkIsolationTests</c>). Activated when
/// <c>Licensing:LicenseContentSecretRef=aws:secretsmanager:&lt;arn-or-name&gt;</c>
/// is configured; on AWS Lambda this lets the ~2KB envelope be delivered via a
/// Secrets Manager reference instead of an environment variable.
/// </summary>
internal sealed class AwsSecretsManagerLicenseContentResolver : ILicenseContentSecretResolver, IDisposable
{
    private const string SecretsManagerPrefix = "aws:secretsmanager:";

    private readonly Lazy<IAmazonSecretsManager> _client;
    private readonly ILogger<AwsSecretsManagerLicenseContentResolver> _logger;
    private bool _disposed;

    public AwsSecretsManagerLicenseContentResolver(
        ILogger<AwsSecretsManagerLicenseContentResolver> logger,
        IAmazonSecretsManager? client = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client is null
            ? new Lazy<IAmazonSecretsManager>(CreateClient)
            : new Lazy<IAmazonSecretsManager>(() => client);
    }

    /// <inheritdoc />
    public bool CanResolve(string? secretReference)
        => !string.IsNullOrWhiteSpace(secretReference)
           && secretReference.StartsWith(SecretsManagerPrefix, StringComparison.OrdinalIgnoreCase)
           && secretReference.Length > SecretsManagerPrefix.Length;

    /// <inheritdoc />
    public async Task<string?> ResolveLicenseContentAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        if (!CanResolve(secretReference))
        {
            return null;
        }

        var secretId = secretReference[SecretsManagerPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(secretId))
        {
            return null;
        }

        try
        {
            var response = await _client.Value
                .GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(response.SecretString))
            {
                return response.SecretString;
            }

            if (response.SecretBinary is { Length: > 0 } binary)
            {
                return System.Text.Encoding.UTF8.GetString(binary.ToArray());
            }

            AwsLicenseLog.SecretEmpty(_logger);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail safe: the license pipeline degrades to Community when the secret
            // cannot be fetched (missing IAM grant, wrong region, deleted secret, etc.).
            AwsLicenseLog.SecretFetchFailed(_logger, ex.GetType().Name);
            return null;
        }
    }

    private static AmazonSecretsManagerClient CreateClient()
    {
        // Resolve the region from AWS_REGION / AWS_DEFAULT_REGION (set on Lambda) so the
        // SDK does not have to probe IMDS; fall back to the SDK default chain otherwise.
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");

        return string.IsNullOrWhiteSpace(region)
            ? new AmazonSecretsManagerClient()
            : new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }

        _disposed = true;
    }
}
