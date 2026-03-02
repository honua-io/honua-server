// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Sdk.Admin.Exceptions;
using Honua.Sdk.Admin.Models;

namespace Honua.Sdk.Admin;

/// <summary>
/// HTTP client implementation for the Honua Admin REST API.
/// </summary>
public sealed class HonuaAdminClient : IHonuaAdminClient
{
    private const string ApiPrefix = "/api/v1/admin";

    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="HonuaAdminClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client configured with base address and auth handlers.</param>
    public HonuaAdminClient(HttpClient httpClient)
    {
        _http = httpClient;
    }

    // ── Services ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceSummary>> ListServicesAsync(CancellationToken ct = default)
    {
        var data = await GetAsync<ServiceSummary[]>(
            $"{ApiPrefix}/services/",
            HonuaAdminJsonContext.Default.ApiResponseServiceSummaryArray,
            ct).ConfigureAwait(false);
        return data ?? [];
    }

    /// <inheritdoc />
    public async Task<ServiceSettingsResponse> GetServiceSettingsAsync(string serviceName, CancellationToken ct = default)
    {
        var data = await GetAsync<ServiceSettingsResponse>(
            $"{ApiPrefix}/services/{Uri.EscapeDataString(serviceName)}/settings",
            HonuaAdminJsonContext.Default.ApiResponseServiceSettingsResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null service settings.", "GetServiceSettings");
    }

    /// <inheritdoc />
    public async Task<ServiceSettingsResponse> UpdateProtocolsAsync(string serviceName, IReadOnlyList<string> protocols, CancellationToken ct = default)
    {
        var body = new UpdateProtocolsRequest { EnabledProtocols = [.. protocols] };
        var data = await PutAsync<ServiceSettingsResponse>(
            $"{ApiPrefix}/services/{Uri.EscapeDataString(serviceName)}/protocols",
            body,
            HonuaAdminJsonContext.Default.UpdateProtocolsRequest,
            HonuaAdminJsonContext.Default.ApiResponseServiceSettingsResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateProtocols");
    }

    /// <inheritdoc />
    public async Task<ServiceSettingsResponse> UpdateMapServerSettingsAsync(string serviceName, UpdateMapServerSettingsRequest request, CancellationToken ct = default)
    {
        var data = await PutAsync<ServiceSettingsResponse>(
            $"{ApiPrefix}/services/{Uri.EscapeDataString(serviceName)}/mapserver",
            request,
            HonuaAdminJsonContext.Default.UpdateMapServerSettingsRequest,
            HonuaAdminJsonContext.Default.ApiResponseServiceSettingsResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateMapServerSettings");
    }

    /// <inheritdoc />
    public async Task<ServiceSettingsResponse> UpdateAccessPolicyAsync(string serviceName, UpdateAccessPolicyRequest request, CancellationToken ct = default)
    {
        var data = await PutAsync<ServiceSettingsResponse>(
            $"{ApiPrefix}/services/{Uri.EscapeDataString(serviceName)}/access-policy",
            request,
            HonuaAdminJsonContext.Default.UpdateAccessPolicyRequest,
            HonuaAdminJsonContext.Default.ApiResponseServiceSettingsResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateAccessPolicy");
    }

    /// <inheritdoc />
    public async Task<ServiceSettingsResponse> UpdateTimeInfoAsync(string serviceName, UpdateTimeInfoRequest request, CancellationToken ct = default)
    {
        var data = await PutAsync<ServiceSettingsResponse>(
            $"{ApiPrefix}/services/{Uri.EscapeDataString(serviceName)}/timeinfo",
            request,
            HonuaAdminJsonContext.Default.UpdateTimeInfoRequest,
            HonuaAdminJsonContext.Default.ApiResponseServiceSettingsResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateTimeInfo");
    }

    /// <inheritdoc />
    public async Task<LayerMetadataResponse> UpdateLayerMetadataAsync(string serviceName, int layerId, UpdateLayerMetadataRequest request, CancellationToken ct = default)
    {
        var data = await PutAsync<LayerMetadataResponse>(
            $"{ApiPrefix}/services/{Uri.EscapeDataString(serviceName)}/layers/{layerId}/metadata",
            request,
            HonuaAdminJsonContext.Default.UpdateLayerMetadataRequest,
            HonuaAdminJsonContext.Default.ApiResponseLayerMetadataResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateLayerMetadata");
    }

    // ── Metadata Resources ───────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetadataResource>> ListMetadataResourcesAsync(string? kind = null, string? ns = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("kind", kind), ("namespace", ns));
        var data = await GetAsync<MetadataResource[]>(
            $"{ApiPrefix}/metadata/resources{query}",
            HonuaAdminJsonContext.Default.ApiResponseMetadataResourceArray,
            ct).ConfigureAwait(false);
        return data ?? [];
    }

    /// <inheritdoc />
    public async Task<(MetadataResource Resource, string? ETag)> GetMetadataResourceAsync(string kind, string ns, string name, CancellationToken ct = default)
    {
        var url = $"{ApiPrefix}/metadata/resources/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize(body, HonuaAdminJsonContext.Default.ApiResponseMetadataResource);
        var resource = envelope?.Data ?? throw new HonuaAdminOperationException("Server returned null metadata resource.", "GetMetadataResource");

        string? etag = null;
        if (response.Headers.ETag is not null)
        {
            etag = response.Headers.ETag.Tag;
        }

        return (resource, etag);
    }

    /// <inheritdoc />
    public async Task<MetadataResource> CreateMetadataResourceAsync(MetadataResource resource, CancellationToken ct = default)
    {
        var data = await PostAsync<MetadataResource>(
            $"{ApiPrefix}/metadata/resources",
            resource,
            HonuaAdminJsonContext.Default.MetadataResource,
            HonuaAdminJsonContext.Default.ApiResponseMetadataResource,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "CreateMetadataResource");
    }

    /// <inheritdoc />
    public async Task<MetadataResource> UpdateMetadataResourceAsync(string kind, string ns, string name, MetadataResource resource, string? ifMatch = null, CancellationToken ct = default)
    {
        var url = $"{ApiPrefix}/metadata/resources/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";
        using var content = JsonContent.Create(resource, HonuaAdminJsonContext.Default.MetadataResource);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };

        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize(body, HonuaAdminJsonContext.Default.ApiResponseMetadataResource);
        return envelope?.Data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateMetadataResource");
    }

    /// <inheritdoc />
    public async Task DeleteMetadataResourceAsync(string kind, string ns, string name, string? ifMatch = null, CancellationToken ct = default)
    {
        var url = $"{ApiPrefix}/metadata/resources/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);

        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);
    }

    // ── Manifests ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AdminVersionResponse> GetVersionAsync(CancellationToken ct = default)
    {
        var data = await GetAsync<AdminVersionResponse>(
            $"{ApiPrefix}/version",
            HonuaAdminJsonContext.Default.ApiResponseAdminVersionResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null version response.", "GetVersion");
    }

    /// <inheritdoc />
    public async Task<AdminCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var data = await GetAsync<AdminCapabilitiesResponse>(
            $"{ApiPrefix}/capabilities",
            HonuaAdminJsonContext.Default.ApiResponseAdminCapabilitiesResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null capabilities response.", "GetCapabilities");
    }

    /// <inheritdoc />
    public async Task<MetadataManifest> GetManifestAsync(string? ns = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("namespace", ns));
        var data = await GetAsync<MetadataManifest>(
            $"{ApiPrefix}/manifest{query}",
            HonuaAdminJsonContext.Default.ApiResponseMetadataManifest,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null manifest.", "GetManifest");
    }

    /// <inheritdoc />
    public async Task<ManifestApplyResult> ApplyManifestAsync(ManifestApplyRequest request, CancellationToken ct = default)
    {
        var data = await PostAsync<ManifestApplyResult>(
            $"{ApiPrefix}/manifest/apply",
            request,
            HonuaAdminJsonContext.Default.ManifestApplyRequest,
            HonuaAdminJsonContext.Default.ApiResponseManifestApplyResult,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null apply result.", "ApplyManifest");
    }

    // ── Connections ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecureConnectionSummary>> ListConnectionsAsync(CancellationToken ct = default)
    {
        var data = await GetAsync<SecureConnectionSummary[]>(
            $"{ApiPrefix}/connections/",
            HonuaAdminJsonContext.Default.ApiResponseSecureConnectionSummaryArray,
            ct).ConfigureAwait(false);
        return data ?? [];
    }

    /// <inheritdoc />
    public async Task<SecureConnectionDetail> GetConnectionAsync(string id, CancellationToken ct = default)
    {
        var data = await GetAsync<SecureConnectionDetail>(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(id)}",
            HonuaAdminJsonContext.Default.ApiResponseSecureConnectionDetail,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null connection.", "GetConnection");
    }

    /// <inheritdoc />
    public async Task<SecureConnectionSummary> CreateConnectionAsync(CreateSecureConnectionRequest request, CancellationToken ct = default)
    {
        var data = await PostAsync<SecureConnectionSummary>(
            $"{ApiPrefix}/connections/",
            request,
            HonuaAdminJsonContext.Default.CreateSecureConnectionRequest,
            HonuaAdminJsonContext.Default.ApiResponseSecureConnectionSummary,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "CreateConnection");
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestDraftConnectionAsync(CreateSecureConnectionRequest request, CancellationToken ct = default)
    {
        var data = await PostAsync<ConnectionTestResult>(
            $"{ApiPrefix}/connections/test",
            request,
            HonuaAdminJsonContext.Default.CreateSecureConnectionRequest,
            HonuaAdminJsonContext.Default.ApiResponseConnectionTestResult,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null test result.", "TestDraftConnection");
    }

    /// <inheritdoc />
    public async Task<SecureConnectionSummary> UpdateConnectionAsync(string id, UpdateSecureConnectionRequest request, CancellationToken ct = default)
    {
        var data = await PutAsync<SecureConnectionSummary>(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(id)}",
            request,
            HonuaAdminJsonContext.Default.UpdateSecureConnectionRequest,
            HonuaAdminJsonContext.Default.ApiResponseSecureConnectionSummary,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateConnection");
    }

    /// <inheritdoc />
    public async Task<ConnectionTestResult> TestConnectionAsync(string id, CancellationToken ct = default)
    {
        var data = await PostAsync<ConnectionTestResult>(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(id)}/test",
            (object?)null,
            HonuaAdminJsonContext.Default.ApiResponseConnectionTestResult,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null test result.", "TestConnection");
    }

    /// <inheritdoc />
    public async Task DeleteConnectionAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EncryptionValidationResult> ValidateEncryptionAsync(CancellationToken ct = default)
    {
        var data = await PostAsync<EncryptionValidationResult>(
            $"{ApiPrefix}/connections/encryption/validate",
            (object?)null,
            HonuaAdminJsonContext.Default.ApiResponseEncryptionValidationResult,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null validation result.", "ValidateEncryption");
    }

    /// <inheritdoc />
    public async Task<KeyRotationResult> RotateEncryptionKeyAsync(CancellationToken ct = default)
    {
        var data = await PostAsync<KeyRotationResult>(
            $"{ApiPrefix}/connections/encryption/rotate-key",
            (object?)null,
            HonuaAdminJsonContext.Default.ApiResponseKeyRotationResult,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null rotation result.", "RotateEncryptionKey");
    }

    // ── Layers ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<PublishedLayerSummary>> ListLayersAsync(string connectionId, string? serviceName = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("serviceName", serviceName));
        var data = await GetAsync<PublishedLayerSummary[]>(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(connectionId)}/layers/{query}",
            HonuaAdminJsonContext.Default.ApiResponsePublishedLayerSummaryArray,
            ct).ConfigureAwait(false);
        return data ?? [];
    }

    /// <inheritdoc />
    public async Task<PublishedLayerSummary> PublishLayerAsync(string connectionId, PublishLayerRequest request, CancellationToken ct = default)
    {
        var data = await PostAsync<PublishedLayerSummary>(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(connectionId)}/layers/",
            request,
            HonuaAdminJsonContext.Default.PublishLayerRequest,
            HonuaAdminJsonContext.Default.ApiResponsePublishedLayerSummary,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "PublishLayer");
    }

    /// <inheritdoc />
    public async Task<PublishedLayerSummary> SetLayerEnabledAsync(string connectionId, int layerId, bool enabled, string? serviceName = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("serviceName", serviceName));
        var body = new LayerEnabledRequest { Enabled = enabled };
        var data = await PutAsync<PublishedLayerSummary>(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(connectionId)}/layers/{layerId}/enabled{query}",
            body,
            HonuaAdminJsonContext.Default.LayerEnabledRequest,
            HonuaAdminJsonContext.Default.ApiResponsePublishedLayerSummary,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "SetLayerEnabled");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(string connectionId, bool enabled, string? serviceName = null, CancellationToken ct = default)
    {
        var query = BuildQuery(("serviceName", serviceName));
        var body = new LayerEnabledRequest { Enabled = enabled };
        var data = await PutAsync<PublishedLayerSummary[]>(
            $"{ApiPrefix}/connections/{Uri.EscapeDataString(connectionId)}/layers/enabled{query}",
            body,
            HonuaAdminJsonContext.Default.LayerEnabledRequest,
            HonuaAdminJsonContext.Default.ApiResponsePublishedLayerSummaryArray,
            ct).ConfigureAwait(false);
        return data ?? [];
    }

    // ── Discovery ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TableDiscoveryResponse> DiscoverTablesAsync(string connectionId, CancellationToken ct = default)
    {
        var url = $"{ApiPrefix}/connections/{Uri.EscapeDataString(connectionId)}/tables";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);

        // Table discovery returns TableDiscoveryResponse directly (not wrapped in ApiResponse)
        var result = JsonSerializer.Deserialize(body, HonuaAdminJsonContext.Default.TableDiscoveryResponse);
        return result ?? throw new HonuaAdminOperationException("Server returned null discovery response.", "DiscoverTables");
    }

    // ── Styles ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<LayerStyleResponse> GetLayerStyleAsync(int layerId, CancellationToken ct = default)
    {
        var data = await GetAsync<LayerStyleResponse>(
            $"{ApiPrefix}/metadata/layers/{layerId}/style",
            HonuaAdminJsonContext.Default.ApiResponseLayerStyleResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null style response.", "GetLayerStyle");
    }

    /// <inheritdoc />
    public async Task<LayerStyleResponse> UpdateLayerStyleAsync(int layerId, LayerStyleUpdateRequest request, CancellationToken ct = default)
    {
        var data = await PutAsync<LayerStyleResponse>(
            $"{ApiPrefix}/metadata/layers/{layerId}/style",
            request,
            HonuaAdminJsonContext.Default.LayerStyleUpdateRequest,
            HonuaAdminJsonContext.Default.ApiResponseLayerStyleResponse,
            ct).ConfigureAwait(false);
        return data ?? throw new HonuaAdminOperationException("Server returned null response.", "UpdateLayerStyle");
    }

    // ── Config ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<JsonElement> GetConfigAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{ApiPrefix}/config", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(
        string url,
        JsonTypeInfo<ApiResponse<T>> typeInfo,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize(body, typeInfo);
        return envelope is not null ? envelope.Data : default;
    }

    private async Task<T?> PostAsync<T>(
        string url,
        object? requestBody,
        JsonTypeInfo<ApiResponse<T>> responseTypeInfo,
        CancellationToken ct)
    {
        HttpContent content;
        if (requestBody is not null)
        {
            content = new StringContent(
                JsonSerializer.Serialize(requestBody, HonuaAdminJsonContext.Default.Options),
                Encoding.UTF8,
                "application/json");
        }
        else
        {
            content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using (content)
        {
            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, body).ConfigureAwait(false);

            var envelope = JsonSerializer.Deserialize(body, responseTypeInfo);
            return envelope is not null ? envelope.Data : default;
        }
    }

    private async Task<TResponse?> PostAsync<TResponse>(
        string url,
        object requestBody,
        JsonTypeInfo requestTypeInfo,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken ct)
    {
        using var content = JsonContent.Create(requestBody, requestTypeInfo);
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize(body, responseTypeInfo);
        return envelope is not null ? envelope.Data : default;
    }

    private async Task<TResponse?> PutAsync<TResponse>(
        string url,
        object requestBody,
        JsonTypeInfo requestTypeInfo,
        JsonTypeInfo<ApiResponse<TResponse>> responseTypeInfo,
        CancellationToken ct)
    {
        using var content = JsonContent.Create(requestBody, requestTypeInfo);
        using var response = await _http.PutAsync(url, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, body).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize(body, responseTypeInfo);
        return envelope is not null ? envelope.Data : default;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = TryExtractErrorMessage(body) ?? response.ReasonPhrase ?? "Request failed";
        throw new HonuaAdminApiException(response.StatusCode, message, body);
    }

    private static string? TryExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }

            // ProblemDetails format
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString();
            }

            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                return title.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON, that's fine
        }

        return null;
    }

    private static string BuildQuery(params ReadOnlySpan<(string Key, string? Value)> parameters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in parameters)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count > 0 ? $"?{string.Join("&", parts)}" : string.Empty;
    }
}
