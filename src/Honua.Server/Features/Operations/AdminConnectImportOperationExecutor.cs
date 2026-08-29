// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal sealed class AdminConnectImportOperationExecutor(
    AdminConnectImportOperationCatalog.Definition definition,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider clock) : IOperationExecutor
{
    public const string HttpClientName = "admin-connect-import-operation-loopback";
    public string OperationId => definition.OperationId;

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = AdminConnectImportOperationCatalog.Descriptors.Single(item => item.OperationId == OperationId);
        var missing = descriptor.InputSchema.Where(static parameter => parameter.Required)
            .Where(parameter => !request.Parameters.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
            .Select(parameter => $"Required parameter '{parameter.Name}' is missing.").ToArray();
        return Task.FromResult(new OperationValidation { IsValid = missing.Length == 0, Status = missing.Length == 0 ? "valid" : "invalid", Messages = missing });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var current = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("Admin operations require an active authenticated request.");
        var path = BindPath(request, request.DryRun && definition.SupportsDryRun ? definition.DryRunPath! : definition.Path);
        var routeNames = RouteNames(path);
        var query = definition.Method == HttpMethod.Get
            ? request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null).Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}").ToArray()
            : [];
        var uri = new Uri($"{current.Request.Scheme}://{current.Request.Host}/api/v1/admin{path}{(query.Length == 0 ? string.Empty : "?" + string.Join('&', query))}");
        using var message = new HttpRequestMessage(definition.Method, uri);
        if (current.Request.Headers.Authorization is { Count: > 0 } authorization) message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization.ToString());
        CopyHeader(current, message, "X-API-Key");
        CopyHeader(current, message, "X-Honua-Tenant");
        if (definition.Method != HttpMethod.Get && definition.Method != HttpMethod.Delete)
            message.Content = definition.ContentType == "multipart/form-data" ? BuildMultipart(request, routeNames) : BuildJson(request, routeNames);

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var instanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}";
        var correlationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}";
        return new OperationHandle
        {
            OperationInstanceId = instanceId,
            OperationId = OperationId,
            CorrelationId = correlationId,
            Status = response.IsSuccessStatusCode ? OperationHandleStatus.Completed : OperationHandleStatus.Failed,
            CreatedAt = now,
            UpdatedAt = now,
            Reason = response.IsSuccessStatusCode ? null : $"Admin API returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
            Result = new OperationResultSummary
            {
                Summary = $"{definition.Title} {(response.IsSuccessStatusCode ? "completed" : "failed")}.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["statusCode"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), ["response"] = payload }
            }
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Task.FromResult(new OperationStatus { OperationInstanceId = handle.OperationInstanceId, OperationId = OperationId, CorrelationId = handle.CorrelationId, AuditId = handle.AuditId, ProposalId = handle.ProposalId, CreatedAt = handle.CreatedAt, UpdatedAt = handle.UpdatedAt, AuthorizationOutcome = handle.AuthorizationOutcome, PolicyDecision = handle.PolicyDecision, Status = handle.Status, Result = handle.Result, JobId = handle.JobId, ApprovalLane = handle.ApprovalLane, MetadataRevision = handle.MetadataRevision, Reason = handle.Reason, ResourceIds = handle.ResourceIds, EvidenceRefs = handle.EvidenceRefs });
    }

    private static string BindPath(OperationRequest request, string path)
    {
        foreach (var name in RouteNames(path)) path = path.Replace($"{{{name}}}", Uri.EscapeDataString(request.Parameters.GetValueOrDefault(name) ?? throw new ArgumentException($"Required route parameter '{name}' is missing.")), StringComparison.Ordinal);
        return path;
    }

    private static HashSet<string> RouteNames(string path) => path.Split('/').Where(static segment => segment.StartsWith('{') && segment.EndsWith('}')).Select(static segment => segment[1..^1]).ToHashSet(StringComparer.Ordinal);
    private static void CopyHeader(HttpContext current, HttpRequestMessage message, string name) { if (current.Request.Headers.TryGetValue(name, out var values) && values.Count > 0) message.Headers.TryAddWithoutValidation(name, values.ToArray()); }

    private static StringContent BuildJson(OperationRequest request, HashSet<string> routeNames)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var pair in request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null))
            {
                writer.WritePropertyName(pair.Key);
                try { using var value = JsonDocument.Parse(pair.Value!); value.RootElement.WriteTo(writer); }
                catch (JsonException) { writer.WriteStringValue(pair.Value); }
            }
            writer.WriteEndObject();
        }
        return new StringContent(Encoding.UTF8.GetString(buffer.WrittenSpan), Encoding.UTF8, "application/json");
    }

    private static MultipartFormDataContent BuildMultipart(OperationRequest request, HashSet<string> routeNames)
    {
        var content = new MultipartFormDataContent();
        foreach (var pair in request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null))
        {
            var part = new StringContent(pair.Value!, Encoding.UTF8);
            if (pair.Key == "file") content.Add(part, pair.Key, "upload");
            else content.Add(part, pair.Key);
        }
        return content;
    }
}
