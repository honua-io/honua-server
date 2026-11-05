// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Logging;

// NOTE: namespace deliberately omits the "Azure" segment (it uses Honua.Licensing, not
// Honua.Cloud.Azure.Features.Licensing) to match the existing Honua.Azure convention (Honua.Alerts,
// Honua.FileStorage, Honua.ControlPlane). A Honua.Azure namespace would shadow the global Azure
// SDK namespace for callers that reference Azure.* by its fully-qualified name.
namespace Honua.Licensing;

internal sealed class AzureKeyVaultLicenseContentResolver : ILicenseContentSecretResolver
{
    private const string KeyVaultPrefix = "azure:keyvault:";

    private readonly Func<Uri, SecretClient> _clientFactory;
    private readonly ILogger<AzureKeyVaultLicenseContentResolver> _logger;

    public AzureKeyVaultLicenseContentResolver(ILogger<AzureKeyVaultLicenseContentResolver> logger)
        : this(logger, clientFactory: null)
    {
    }

    internal AzureKeyVaultLicenseContentResolver(
        ILogger<AzureKeyVaultLicenseContentResolver> logger,
        Func<Uri, SecretClient>? clientFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clientFactory = clientFactory ?? (static uri => new SecretClient(uri, new DefaultAzureCredential()));
    }

    public bool CanResolve(string? secretReference)
        => TryParseReference(secretReference, out _, out _, out _);

    public async Task<string?> ResolveLicenseContentAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseReference(secretReference, out var vaultUri, out var secretName, out var secretVersion))
        {
            return null;
        }

        try
        {
            var client = _clientFactory(vaultUri);
            Response<KeyVaultSecret> response = await client
                .GetSecretAsync(secretName, secretVersion, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var value = response.Value?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                AzureLicenseLog.SecretEmpty(_logger);
                return null;
            }

            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail safe: the license pipeline rejects paid-tier startup when the secret cannot be
            // fetched (missing RBAC grant, wrong vault, deleted secret, unreachable endpoint, etc.).
            AzureLicenseLog.SecretFetchFailed(_logger, ex.GetType().Name);
            return null;
        }
    }

    // The vault endpoint is taken from the URI authority, because splitting on the last slash folds the secrets segment into the base and every fetch fails.
    private static bool TryParseReference(
        string? secretReference,
        out Uri vaultUri,
        out string secretName,
        out string? secretVersion)
    {
        vaultUri = null!;
        secretName = string.Empty;
        secretVersion = null;

        if (string.IsNullOrWhiteSpace(secretReference) ||
            !secretReference.StartsWith(KeyVaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = secretReference[KeyVaultPrefix.Length..].Trim();
        if (remainder.Length == 0 ||
            !Uri.TryCreate(remainder, UriKind.Absolute, out var parsedUri) ||
            parsedUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // Vault endpoint = scheme + authority only (e.g. https://myvault.vault.azure.net/). The secret
        // name and optional version come from the path segments, NOT from a trailing-slash split.
        var vaultEndpoint = new Uri(parsedUri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        var segments = parsedUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Canonical identifier: /secrets/<name>[/<version>]
        if (segments.Length >= 2 &&
            string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(segments[1]))
            {
                return false;
            }

            vaultUri = vaultEndpoint;
            secretName = segments[1];
            secretVersion = segments.Length >= 3 && !string.IsNullOrWhiteSpace(segments[2])
                ? segments[2]
                : null;
            return true;
        }

        // Shorthand: https://<vault>.vault.azure.net/<secret>
        if (segments.Length == 1 && !string.IsNullOrWhiteSpace(segments[0]))
        {
            vaultUri = vaultEndpoint;
            secretName = segments[0];
            return true;
        }

        return false;
    }
}
