// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Operations;

internal sealed class AdminConnectImportOperationExecutor(
    AdminConnectImportOperationCatalog.Definition definition,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    IAdminApiKeyStore adminApiKeyStore,
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
        var messages = missing.ToList();
        if (definition.SideEffect != OperationSideEffectClass.ReadOnly &&
            request.Parameters.TryGetValue("password", out var password) &&
            !string.IsNullOrEmpty(password))
        {
            messages.Add("Inline passwords cannot be persisted for approval; use secretReference.");
        }
        if (request.Parameters.TryGetValue("file", out var encodedFile) && !string.IsNullOrWhiteSpace(encodedFile))
        {
            try { _ = Convert.FromBase64String(encodedFile); }
            catch (FormatException) { messages.Add("The file parameter must be base64-encoded binary content."); }
        }
        return Task.FromResult(new OperationValidation { IsValid = messages.Count == 0, Status = messages.Count == 0 ? "valid" : "invalid", Messages = messages });
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
        AdminApiKeyRecord? executionCredential = null;
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            var issued = await adminApiKeyStore.CreateAsync(
                $"approved-operation:{context.ApprovedProposalId}",
                ["admin:write"],
                clock.GetUtcNow().AddMinutes(5),
                context.PrincipalId,
                cancellationToken).ConfigureAwait(false);
            executionCredential = issued.Record;
            message.Headers.TryAddWithoutValidation("X-API-Key", issued.Key);
            if (!string.IsNullOrWhiteSpace(context.TenantId))
                message.Headers.TryAddWithoutValidation("X-Honua-Tenant", context.TenantId);
        }
        else
        {
            if (current.Request.Headers.Authorization is { Count: > 0 } authorization)
                message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization.ToString());
            CopyHeader(current, message, "X-API-Key");
            CopyHeader(current, message, "X-Honua-Tenant");
        }
        if (definition.Method != HttpMethod.Get && definition.Method != HttpMethod.Delete)
            message.Content = definition.ContentType == "multipart/form-data" ? BuildMultipart(request, routeNames) : BuildJson(request, routeNames, OperationId);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (executionCredential is not null)
                _ = await adminApiKeyStore.RevokeAsync(executionCredential.Id, CancellationToken.None).ConfigureAwait(false);
        }
        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var instanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}";
            var correlationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}";
            var queued = response.StatusCode == HttpStatusCode.Accepted;
            var resources = queued ? ReadQueuedResources(payload) : new Dictionary<string, string>(StringComparer.Ordinal);
            return new OperationHandle
            {
                OperationInstanceId = instanceId,
                OperationId = OperationId,
                CorrelationId = correlationId,
                Status = queued ? OperationHandleStatus.Queued : response.IsSuccessStatusCode ? OperationHandleStatus.Completed : OperationHandleStatus.Failed,
                JobId = resources.GetValueOrDefault("jobId"),
                ResourceIds = resources,
                CreatedAt = now,
                UpdatedAt = now,
                Reason = response.IsSuccessStatusCode ? null : $"Admin API returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                Result = new OperationResultSummary
                {
                    Summary = $"{definition.Title} {(queued ? "queued" : response.IsSuccessStatusCode ? "completed" : "failed")}.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["statusCode"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), ["response"] = payload }
                }
            };
        }
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

    internal static StringContent BuildJson(OperationRequest request, HashSet<string> routeNames, string operationId)
    {
        var schemas = AdminConnectImportOperationCatalog.Descriptors.Single(item => item.OperationId == operationId)
            .InputSchema.ToDictionary(static item => item.Name, static item => item.Schema.Type, StringComparer.Ordinal);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var pair in request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null))
            {
                writer.WritePropertyName(pair.Key);
                if (schemas.GetValueOrDefault(pair.Key) == Honua.Core.Features.WorkflowPackages.Domain.WorkflowSchemaValueType.Text)
                    writer.WriteStringValue(pair.Value);
                else
                {
                    try { using var value = JsonDocument.Parse(pair.Value!); value.RootElement.WriteTo(writer); }
                    catch (JsonException) { writer.WriteStringValue(pair.Value); }
                }
            }
            writer.WriteEndObject();
        }
        return new StringContent(Encoding.UTF8.GetString(buffer.WrittenSpan), Encoding.UTF8, "application/json");
    }

    internal static MultipartFormDataContent BuildMultipart(OperationRequest request, HashSet<string> routeNames)
    {
        var content = new MultipartFormDataContent();
        foreach (var pair in request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null))
        {
            HttpContent part = pair.Key == "file"
                ? new ByteArrayContent(Convert.FromBase64String(pair.Value!))
                : new StringContent(pair.Value!, Encoding.UTF8);
            if (pair.Key == "file")
                content.Add(part, pair.Key, request.Parameters.GetValueOrDefault("fileName") ?? "upload.bin");
            else if (pair.Key == "fileName") continue;
            else content.Add(part, pair.Key);
        }
        return content;
    }

    internal static Dictionary<string, string> ReadQueuedResources(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "jobId", "statusUrl", "cancelUrl" })
            if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                resources[name] = value.GetString()!;
        return resources;
    }
}
