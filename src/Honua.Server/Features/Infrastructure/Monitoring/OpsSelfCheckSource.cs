// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Identity.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Configuration;

namespace Honua.Infrastructure.Monitoring;

internal sealed record OpsKeyRingCertificateLifetime(DateTimeOffset ExpiresAt);

internal sealed record OpsSelfCheckEnvironment(DateTimeOffset? CertificateExpiresAt, string CurrentVersion)
{
    public static string ReadVersion() => typeof(OpsSelfCheckSource).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}

internal sealed class OpsSelfCheckSource(
    IHttpClientFactory clients,
    Func<CancellationToken, Task<IReadOnlyList<OidcProviderRegistration>>> providers,
    OpsSelfCheckEnvironment environment) : IOpsSelfCheckSource
{
    internal const string HttpClientName = "honua-self-checks";
    internal const int MaximumResponseBytes = 65536;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const string ReleasesEndpoint = "https://api.github.com/repos/honua-io/honua-server/releases/latest";

    public DateTimeOffset? CertificateExpiresAt => environment.CertificateExpiresAt;
    public string CurrentVersion => environment.CurrentVersion;

    public async Task<IReadOnlyList<OpsIdentityProviderHealth>> ReadIdentityProvidersAsync(CancellationToken cancellationToken)
    {
        var registrations = (await providers(cancellationToken).ConfigureAwait(false))
            .Where(provider => provider.Enabled).ToArray();
        var results = new OpsIdentityProviderHealth[registrations.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, registrations.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (index, token) =>
            {
                var provider = registrations[index];
                var healthy = await ProbeProviderAsync(provider, token).ConfigureAwait(false);
                var id = Guid.TryParse(provider.Key, out var parsed) ? parsed
                    : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(provider.Key)).AsSpan(0, 16));
                results[index] = new(id, healthy);
            }).ConfigureAwait(false);
        return results;
    }

    private async Task<bool> ProbeProviderAsync(OidcProviderRegistration provider, CancellationToken cancellationToken)
    {
        if (!provider.IsValid || string.IsNullOrEmpty(provider.Authority))
        {
            return false;
        }
        var configuration = new OidcProviderConfiguration
        {
            Name = provider.DisplayName, ProviderType = provider.Type, Authority = provider.Authority,
            ClientId = provider.ClientId ?? string.Empty, Enabled = provider.Enabled
        };
        var result = await OidcProviderDiscoveryProbe.TestAsync(configuration, clients, cancellationToken).ConfigureAwait(false);
        return result.IsReachable;
    }

    public async Task<OpsPublishedRelease?> ReadLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var document = await ReadDocumentAsync(new Uri(ReleasesEndpoint), timeout.Token).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("html_url", out var notes) || notes.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(notes.GetString(), UriKind.Absolute, out var uri)
                || !IsReleaseNotesUri(uri))
            {
                throw new JsonException("Invalid public release metadata.");
            }
            return new(tag.GetString()!, uri);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Public release check timed out.");
        }
    }

    private async Task<JsonDocument?> ReadDocumentAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Honua-Server-SelfChecks/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && uri.AbsoluteUri == ReleasesEndpoint)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false),
            new JsonDocumentOptions { MaxDepth = 16 });
    }

    internal static bool IsReleaseNotesUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "github.com" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath.StartsWith("/honua-io/honua-server/releases/tag/", StringComparison.Ordinal);
}
