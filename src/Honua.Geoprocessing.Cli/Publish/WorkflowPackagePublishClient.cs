// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Geoprocessing.Cli.Publish;

internal sealed class GpPublishHttpException(string message) : Exception(message);

internal sealed record GpPublishResult
{
    public required WorkflowPackage Package { get; init; }

    public WorkflowPackageVersion? Version { get; init; }

    public WorkflowPackageValidationResult? Validation { get; init; }

    public WorkflowDryRunResult? DryRun { get; init; }

    public WorkflowPublication? Publication { get; init; }
}

internal sealed class WorkflowPackagePublishClient
{
    private const string ApiKeyHeader = "X-API-Key";

    private readonly HttpClient _http;

    public WorkflowPackagePublishClient(HttpClient http, string? apiKey = null, string? bearerToken = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Remove(ApiKeyHeader);
            _http.DefaultRequestHeaders.Add(ApiKeyHeader, apiKey);
        }
    }

    internal const string BasePath = "/api/v1/console/workflow-packages";

    public async Task<GpPublishResult> SaveVersionAndPublishAsync(
        SaveWorkflowPackageRequestDto request,
        bool validate,
        bool dryRun,
        bool publish,
        PublishWorkflowPackageRequestDto? publishRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var package = await SavePackageAsync(request, cancellationToken).ConfigureAwait(false);
        var version = await CreateVersionAsync(package.PackageId, cancellationToken).ConfigureAwait(false);

        WorkflowPackageValidationResult? validation = null;
        if (validate)
        {
            validation = await ValidateVersionAsync(package.PackageId, version.Version, cancellationToken)
                .ConfigureAwait(false);
        }

        WorkflowDryRunResult? dryRunResult = null;
        if (dryRun)
        {
            dryRunResult = await DryRunVersionAsync(package.PackageId, version.Version, cancellationToken)
                .ConfigureAwait(false);
        }

        WorkflowPublication? publication = null;
        if (publish)
        {
            ArgumentNullException.ThrowIfNull(publishRequest);
            publication = await PublishVersionAsync(
                    package.PackageId,
                    version.Version,
                    publishRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new GpPublishResult
        {
            Package = package,
            Version = version,
            Validation = validation,
            DryRun = dryRunResult,
            Publication = publication
        };
    }

    public async Task<GpPublishResult> DryRunOnlyAsync(
        SaveWorkflowPackageRequestDto request,
        CancellationToken cancellationToken = default)
        => await SaveVersionAndPublishAsync(
                request,
                validate: true,
                dryRun: true,
                publish: false,
                publishRequest: null,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<WorkflowPackage> SavePackageAsync(
        SaveWorkflowPackageRequestDto request,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(request, options: GpPublishJson.Options);
        using var response = await _http.PostAsync(BasePath, content, cancellationToken).ConfigureAwait(false);
        return await ReadDataAsync<WorkflowPackage>(response, "save package", cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowPackageVersion> CreateVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        var path = $"{BasePath}/{Uri.EscapeDataString(packageId)}/versions";
        using var response = await _http.PostAsync(path, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadDataAsync<WorkflowPackageVersion>(response, "create version", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowPackageValidationResult> ValidateVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken)
    {
        var path = $"{BasePath}/{Uri.EscapeDataString(packageId)}/versions/{version}/validate";
        using var response = await _http.PostAsync(path, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadDataAsync<WorkflowPackageValidationResult>(response, "validate version", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowDryRunResult> DryRunVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken)
    {
        var path = $"{BasePath}/{Uri.EscapeDataString(packageId)}/versions/{version}/dry-run";
        using var response = await _http.PostAsync(path, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadDataAsync<WorkflowDryRunResult>(response, "dry-run version", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowPublication> PublishVersionAsync(
        string packageId,
        int version,
        PublishWorkflowPackageRequestDto request,
        CancellationToken cancellationToken)
    {
        var path = $"{BasePath}/{Uri.EscapeDataString(packageId)}/versions/{version}/publish";
        using var content = JsonContent.Create(request, options: GpPublishJson.Options);
        using var response = await _http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
        return await ReadDataAsync<WorkflowPublication>(response, "publish version", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<T> ReadDataAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = ExtractMessage(body) ?? body;
            throw new GpPublishHttpException(
                $"Failed to {operation}: {(int)response.StatusCode} {response.StatusCode}. {Trim(detail)}");
        }

        ApiResponseDto<T>? envelope;
        try
        {
            envelope = System.Text.Json.JsonSerializer.Deserialize<ApiResponseDto<T>>(body, GpPublishJson.Options);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new GpPublishHttpException($"Failed to {operation}: could not parse server response ({ex.Message}).");
        }

        if (envelope is null || !envelope.Success || envelope.Data is null)
        {
            throw new GpPublishHttpException(
                $"Failed to {operation}: server reported failure. {Trim(envelope?.Message)}");
        }

        return envelope.Data;
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var message) &&
                    message.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (root.TryGetProperty("detail", out var detail) &&
                    detail.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return detail.GetString();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON error body (e.g. a plain 401 challenge); fall through to raw text.
        }

        return null;
    }

    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = value.Trim();
        return collapsed.Length <= 400 ? collapsed : collapsed[..397] + "...";
    }

    internal static bool IsAuthFailure(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
