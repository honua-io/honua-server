// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

/// <summary>
/// Client for control-plane metadata admin endpoints.
/// </summary>
public interface IMetadataClient
{
    Task<ApiResult<AdminVersionResponse>> GetVersionAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<AdminCapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<MetadataManifest>> GetManifestAsync(string? @namespace = null, CancellationToken cancellationToken = default);
    Task<ApiResult<MetadataResource[]>> ListResourcesAsync(string? kind = null, string? @namespace = null, CancellationToken cancellationToken = default);
    Task<ApiResult<ManifestApplyResult>> ApplyManifestAsync(ManifestApplyRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<MetadataResourceWithEtag>> GetResourceAsync(string kind, string @namespace, string name, CancellationToken cancellationToken = default);
    Task<ApiResult<MetadataResource>> CreateResourceAsync(MetadataResource resource, CancellationToken cancellationToken = default);
    Task<ApiResult<MetadataResource>> UpdateResourceAsync(string kind, string @namespace, string name, MetadataResource resource, string ifMatch, CancellationToken cancellationToken = default);
    Task<ApiResult<bool>> DeleteResourceAsync(string kind, string @namespace, string name, string ifMatch, CancellationToken cancellationToken = default);
}

internal sealed class MetadataClient : IMetadataClient
{
    private readonly HttpClient _httpClient;

    public MetadataClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<AdminVersionResponse>> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("version", cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<AdminVersionResponse>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<AdminCapabilitiesResponse>> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("capabilities", cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<AdminCapabilitiesResponse>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<MetadataManifest>> GetManifestAsync(string? @namespace = null, CancellationToken cancellationToken = default)
    {
        var path = "manifest";
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            path += $"?namespace={Uri.EscapeDataString(@namespace)}";
        }

        using var response = await _httpClient.GetAsync(path, cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<MetadataManifest>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<MetadataResource[]>> ListResourcesAsync(
        string? kind = null,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query.Add($"kind={Uri.EscapeDataString(kind)}");
        }

        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            query.Add($"namespace={Uri.EscapeDataString(@namespace)}");
        }

        var path = query.Count == 0
            ? "metadata/resources"
            : $"metadata/resources?{string.Join("&", query)}";

        using var response = await _httpClient.GetAsync(path, cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<MetadataResource[]>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<ManifestApplyResult>> ApplyManifestAsync(
        ManifestApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _httpClient.PostAsJsonAsync(
            "manifest/apply",
            request,
            AdminJsonContext.Default.Options,
            cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<ManifestApplyResult>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<MetadataResourceWithEtag>> GetResourceAsync(
        string kind,
        string @namespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return ApiResult.Fail<MetadataResourceWithEtag>("Resource kind is required.");
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            return ApiResult.Fail<MetadataResourceWithEtag>("Resource namespace is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return ApiResult.Fail<MetadataResourceWithEtag>("Resource name is required.");
        }

        var path = $"metadata/resources/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(@namespace)}/{Uri.EscapeDataString(name)}";
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        var result = await ApiResponseReader.ReadWrappedAsync<MetadataResource>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
        if (!result.Success || result.Data is null)
        {
            return ApiResult.Fail<MetadataResourceWithEtag>(result.Message);
        }

        var etag = response.Headers.ETag?.Tag ?? response.Headers.ETag?.ToString();
        return ApiResult.Ok(new MetadataResourceWithEtag(result.Data, etag), result.Message);
    }

    public async Task<ApiResult<MetadataResource>> CreateResourceAsync(
        MetadataResource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        using var response = await _httpClient.PostAsJsonAsync(
            "metadata/resources",
            resource,
            AdminJsonContext.Default.Options,
            cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<MetadataResource>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<MetadataResource>> UpdateResourceAsync(
        string kind,
        string @namespace,
        string name,
        MetadataResource resource,
        string ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return ApiResult.Fail<MetadataResource>("If-Match is required.");
        }

        var path = $"metadata/resources/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(@namespace)}/{Uri.EscapeDataString(name)}";
        using var requestMessage = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(resource, options: AdminJsonContext.Default.Options)
        };
        requestMessage.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<MetadataResource>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<bool>> DeleteResourceAsync(
        string kind,
        string @namespace,
        string name,
        string ifMatch,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return ApiResult.Fail<bool>("If-Match is required.");
        }

        var path = $"metadata/resources/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(@namespace)}/{Uri.EscapeDataString(name)}";
        using var requestMessage = new HttpRequestMessage(HttpMethod.Delete, path);
        requestMessage.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        var result = await ApiResponseReader.ReadWrappedAsync<object>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
        return result.Success
            ? ApiResult.Ok(true, result.Message)
            : ApiResult.Fail<bool>(result.Message);
    }
}
