// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using Honua.Infrastructure.Licensing;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Licensing;

/// <summary>
/// Resolves a <c>Licensing:LicenseContentSecretRef</c> of the form
/// <c>azure:keyvault:https://&lt;vault&gt;.vault.azure.net/&lt;secret&gt;[/&lt;version&gt;]</c> into the
/// signed license envelope JSON, using Entra managed identity. The resolved value is treated as the
/// inline license envelope by <c>FileBackedLicenseService</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reuses the proven light HTTP + IMDS-token approach from
/// <c>Honua.Postgres/.../ConnectionSecretResolvers/AzureKeyVaultResolver.cs</c> (no Azure SDK
/// dependency for the call itself), but this is a SEPARATE license-content resolver — it implements
/// <see cref="ILicenseContentSecretResolver"/>, not <c>IConnectionSecretResolver</c>, and must not
/// be conflated with database connection-secret resolution. It lives in <c>Honua.Azure</c> to keep
/// the licensing seam cloud-neutral.
/// </para>
/// <para>
/// PROVISIONAL draft (#1745) pending the canonical resolver seam in honua-server#1742. Token
/// acquisition uses the Azure Instance Metadata Service (IMDS) managed-identity endpoint;
/// <c>AZURE_CLIENT_ID</c> selects a user-assigned identity. Errors are thrown to the caller, which
/// fails safe to Community licensing.
/// </para>
/// </remarks>
internal sealed class AzureKeyVaultLicenseContentResolver : ILicenseContentSecretResolver
{
    private const string Prefix = "azure:keyvault:";
    private const string KeyVaultApiVersion = "7.4";
    private const string TokenResource = "https://vault.azure.net";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureKeyVaultLicenseContentResolver> _logger;

    public AzureKeyVaultLicenseContentResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<AzureKeyVaultLicenseContentResolver> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool CanResolve(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef)
            || !secretRef.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryParse(secretRef, out _, out _);
    }

    /// <inheritdoc />
    public async Task<string> ResolveLicenseContentAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        if (!TryParse(secretRef, out var secretUrl, out var vaultHost))
        {
            throw new ArgumentException(
                $"Invalid Azure Key Vault license reference. Expected '{Prefix}https://<vault>.vault.azure.net/<secret>[/<version>]'.",
                nameof(secretRef));
        }

        var token = await GetManagedIdentityTokenAsync(cancellationToken).ConfigureAwait(false);

        var client = _httpClientFactory.CreateClient("AzureKeyVaultLicense");
        using var request = new HttpRequestMessage(HttpMethod.Get, secretUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure Key Vault license request to '{vaultHost}' failed with status code {(int)response.StatusCode}.");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ExtractSecretValue(content);
    }

    // Parses azure:keyvault:https://<vault>.vault.azure.net/<secret>[/<version>] into the Key Vault
    // GET-secret URL (with api-version) and the vault host (for diagnostics).
    private static bool TryParse(string secretRef, out Uri secretUrl, out string vaultHost)
    {
        secretUrl = null!;
        vaultHost = string.Empty;

        var remainder = secretRef[Prefix.Length..].Trim();
        if (!Uri.TryCreate(remainder, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // Require a .vault.azure.net (or sovereign-cloud .vault.*) host and a non-empty secret path.
        if (!parsed.Host.Contains(".vault.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = parsed.AbsolutePath.Trim('/');
        if (path.Length == 0)
        {
            return false;
        }

        // Normalize to the canonical /secrets/<name>[/<version>] route. Accept both a bare
        // <secret>[/<version>] and an already-qualified /secrets/<secret> path.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string secretName;
        string? version = null;
        if (segments.Length >= 2 && string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
        {
            secretName = segments[1];
            version = segments.Length >= 3 ? segments[2] : null;
        }
        else
        {
            secretName = segments[0];
            version = segments.Length >= 2 ? segments[1] : null;
        }

        var escapedName = Uri.EscapeDataString(secretName);
        var versionSegment = string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : $"/{Uri.EscapeDataString(version)}";

        var url = $"https://{parsed.Host}/secrets/{escapedName}{versionSegment}?api-version={KeyVaultApiVersion}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var built))
        {
            return false;
        }

        secretUrl = built;
        vaultHost = parsed.Host;
        return true;
    }

    private async Task<string> GetManagedIdentityTokenAsync(CancellationToken cancellationToken)
    {
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var query = $"api-version=2018-02-01&resource={Uri.EscapeDataString(TokenResource)}";
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            query += $"&client_id={Uri.EscapeDataString(clientId)}";
        }

        var uri = $"http://169.254.169.254/metadata/identity/oauth2/token?{query}";
        var metadataClient = _httpClientFactory.CreateClient("AzureManagedIdentityLicense");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Metadata", "true");

        using var response = await metadataClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure managed-identity token request failed with status code {(int)response.StatusCode}.");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        var accessToken = document.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Azure managed-identity token response did not include access_token.");
        }

        return accessToken!;
    }

    private static string ExtractSecretValue(string content)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("value", out var valueElement)
            || valueElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Azure Key Vault license response did not include a string value.");
        }

        return valueElement.GetString()!;
    }
}
