// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using Honua.Admin.Models;
using Microsoft.AspNetCore.Components.Forms;

namespace Honua.Admin.Services;

/// <summary>
/// Client for uploading and importing geospatial files.
/// </summary>
public interface IFileImportClient
{
    Task<ApiResult<FilePreviewResponse>> PreviewAsync(IBrowserFile file, CancellationToken cancellationToken = default);
    Task<ApiResult<FileImportResult>> UploadAsync(
        IBrowserFile file,
        string tableName,
        int? sourceSrid,
        int? targetSrid,
        bool overwriteExisting,
        CancellationToken cancellationToken = default);
}

internal sealed class FileImportClient : IFileImportClient
{
    private const long DefaultMaxFileSizeBytes = 100 * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public FileImportClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<FilePreviewResponse>> PreviewAsync(IBrowserFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(file.OpenReadStream(GetMaxFileSize(file), cancellationToken));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

        content.Add(fileContent, "file", file.Name);

        using var response = await _httpClient.PostAsync("import/preview", content, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<FilePreviewResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<FileImportResult>> UploadAsync(
        IBrowserFile file,
        string tableName,
        int? sourceSrid,
        int? targetSrid,
        bool overwriteExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(file.OpenReadStream(GetMaxFileSize(file), cancellationToken));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

        content.Add(fileContent, "File", file.Name);
        content.Add(new StringContent(tableName), "TableName");

        if (sourceSrid.HasValue)
        {
            content.Add(new StringContent(sourceSrid.Value.ToString(CultureInfo.InvariantCulture)), "SourceSrid");
        }

        if (targetSrid.HasValue)
        {
            content.Add(new StringContent(targetSrid.Value.ToString(CultureInfo.InvariantCulture)), "TargetSrid");
        }

        content.Add(new StringContent(overwriteExisting ? "true" : "false"), "OverwriteExisting");

        using var response = await _httpClient.PostAsync("import/upload", content, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<FileImportResult>(response, cancellationToken);
    }

    private static long GetMaxFileSize(IBrowserFile file)
        => file.Size > 0 ? Math.Max(file.Size, DefaultMaxFileSizeBytes) : DefaultMaxFileSizeBytes;

}
