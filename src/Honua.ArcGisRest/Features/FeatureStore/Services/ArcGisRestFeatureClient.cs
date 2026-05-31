// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.ArcGisRest.Features.FeatureStore.Models;

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// Outbound HTTP client used by the federated ArcGIS REST provider. Each method
/// issues a single <c>/query</c> request and returns the strongly typed wire model
/// without applying any business logic — translation between Honua's canonical
/// query/feature model and the Esri wire format happens in
/// <see cref="ArcGisRestFeatureStore"/>.
/// </summary>
/// <remarks>
/// The HttpClient itself is registered through <see cref="ArcGisRestServiceClientName"/>
/// in the DI composition root and managed by <see cref="System.Net.Http.IHttpClientFactory"/>.
/// </remarks>
internal interface IArcGisRestFeatureClient
{
    /// <summary>Fetches a page of features from the remote layer.</summary>
    Task<ArcGisRestQueryResponse> QueryAsync(string url, CancellationToken cancellationToken);

    /// <summary>Fetches a record-count from the remote layer.</summary>
    Task<ArcGisRestCountResponse> QueryCountAsync(string url, CancellationToken cancellationToken);

    /// <summary>Fetches the extent envelope from the remote layer.</summary>
    Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, CancellationToken cancellationToken);

    /// <summary>Fetches an object-id list from the remote layer.</summary>
    Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, CancellationToken cancellationToken);

    /// <summary>Fetches the layer metadata document for the remote layer.</summary>
    Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// HttpClient-backed implementation of <see cref="IArcGisRestFeatureClient"/>.
/// </summary>
internal sealed class ArcGisRestFeatureClient : IArcGisRestFeatureClient
{
    private readonly HttpClient _httpClient;

    public ArcGisRestFeatureClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Task<ArcGisRestQueryResponse> QueryAsync(string url, CancellationToken cancellationToken)
        => GetAsync(url, ArcGisRestJsonContext.Default.ArcGisRestQueryResponse, cancellationToken);

    public Task<ArcGisRestCountResponse> QueryCountAsync(string url, CancellationToken cancellationToken)
        => GetAsync(url, ArcGisRestJsonContext.Default.ArcGisRestCountResponse, cancellationToken);

    public Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, CancellationToken cancellationToken)
        => GetAsync(url, ArcGisRestJsonContext.Default.ArcGisRestExtentResponse, cancellationToken);

    public Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, CancellationToken cancellationToken)
        => GetAsync(url, ArcGisRestJsonContext.Default.ArcGisRestObjectIdsResponse, cancellationToken);

    public Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, CancellationToken cancellationToken)
        => GetAsync(url, ArcGisRestJsonContext.Default.ArcGisRestLayerResponse, cancellationToken);

    private async Task<T> GetAsync<T>(
        string url,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException(
            "ArcGIS REST service returned an empty response body.");
    }
}

/// <summary>
/// Well-known DI client name registered through <c>AddHttpClient</c>. Lets host
/// applications attach handlers (resilience, retry, telemetry) without taking a
/// hard dependency on this provider's internals.
/// </summary>
internal static class ArcGisRestServiceClientName
{
    /// <summary>Logical HttpClient name for federated ArcGIS REST outbound calls.</summary>
    public const string Default = "Honua.ArcGisRest.Federation";
}
