// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>Thin executor that binds an operation to its existing authenticated Admin REST route.</summary>
internal sealed class AdminApiOperationExecutor : IOperationExecutor
{
    public const string HttpClientName = "admin-operation-loopback";
    private readonly AdminApiOperationCatalog.Definition _definition;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _clock;

    public AdminApiOperationExecutor(AdminApiOperationCatalog.Definition definition, IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor, TimeProvider clock)
    {
        _definition = definition;
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _clock = clock;
    }

    public string OperationId => _definition.OperationId;

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(item => item.OperationId == OperationId);
        var missing = GetMissingRouteParameters(request).ToList();
        foreach (var parameter in descriptor.InputSchema.Where(static parameter => parameter.Required))
        {
            var present = parameter.Name switch
            {
                "connectionId" => !string.IsNullOrWhiteSpace(request.ConnectionId),
                "serviceName" when RouteNames().Contains("serviceName") => !string.IsNullOrWhiteSpace(request.ServiceName),
                "body" => request.Parameters.ContainsKey(parameter.Name),
                _ => request.Parameters.TryGetValue(parameter.Name, out var value) && !string.IsNullOrWhiteSpace(value)
            };
            if (!present) missing.Add($"Required parameter '{parameter.Name}' is missing.");
        }
        return Task.FromResult(new OperationValidation { IsValid = missing.Count == 0, Status = missing.Count == 0 ? "valid" : "invalid", Messages = missing.ToArray() });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var current = _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("Admin operations require an active authenticated request.");
        var relativePath = BindPath(request, request.DryRun && _definition.SupportsDryRun ? _definition.DryRunPath : null);
        var query = _definition.QueryParameters?.Where(name => request.Parameters.TryGetValue(name, out var value) && value is not null)
            .Select(name => $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(request.Parameters[name]!)}").ToArray();
        if (query is { Length: > 0 }) relativePath += "?" + string.Join("&", query);
        var uri = new Uri($"{current.Request.Scheme}://{current.Request.Host}/api/v1/admin{relativePath}");
        using var message = new HttpRequestMessage(_definition.Method, uri);
        if (current.Request.Headers.Authorization is { Count: > 0 } authorization)
            message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization.ToString());
        if (current.Request.Headers.TryGetValue("X-API-Key", out var apiKey))
            message.Headers.TryAddWithoutValidation("X-API-Key", apiKey.ToArray());

        if (_definition.Method != HttpMethod.Get)
        {
            if (_definition.OpenApiOperationId == "importLayerSldStyle")
            {
                message.Content = new StringContent(request.Parameters.GetValueOrDefault("body") ?? string.Empty, Encoding.UTF8, "application/xml");
            }
            else
            {
                string json;
                if (_definition.RawBody)
                {
                    json = request.Parameters.GetValueOrDefault("body") ?? "null";
                    using var _ = JsonDocument.Parse(json);
                }
                else
                {
                    var descriptor = AdminApiOperationCatalog.Descriptors.Single(item => item.OperationId == OperationId);
                    json = BuildBody(request, descriptor);
                }
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        }

        using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new OperationHandle
            {
                OperationId = OperationId,
                HandleId = NewHandleId(),
                Status = OperationHandleStatus.Failed,
                Reason = $"Admin API returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                Result = new OperationResultSummary { Summary = $"{_definition.Title} failed.", Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["statusCode"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), ["response"] = payload } }
            };
        }
        return new OperationHandle
        {
            OperationId = OperationId,
            HandleId = NewHandleId(),
            Status = OperationHandleStatus.Completed,
            Result = new OperationResultSummary { Summary = $"{_definition.Title} completed.", Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["response"] = payload } }
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default) => Task.FromResult(new OperationStatus { OperationId = OperationId, HandleId = handle.HandleId, Status = handle.Status, Result = handle.Result });

    private string BindPath(OperationRequest request, string? pathOverride)
    {
        var path = pathOverride ?? _definition.Path;
        foreach (var name in RouteNames(path))
        {
            var value = name switch { "connectionId" => request.ConnectionId, "serviceName" => request.ServiceName, _ => request.Parameters.GetValueOrDefault(name) };
            path = path.Replace($"{{{name}}}", Uri.EscapeDataString(value ?? throw new ArgumentException($"Required route parameter '{name}' is missing.")), StringComparison.Ordinal);
        }
        return path;
    }

    private string[] GetMissingRouteParameters(OperationRequest request) => RouteNames(_definition.Path).Where(name => string.IsNullOrWhiteSpace(name switch { "connectionId" => request.ConnectionId, "serviceName" => request.ServiceName, _ => request.Parameters.GetValueOrDefault(name) })).Select(name => $"Required route parameter '{name}' is missing.").ToArray();
    private HashSet<string> RouteNames() => RouteNames(_definition.Path);
    private static HashSet<string> RouteNames(string path) => path.Split('/').Where(static segment => segment.StartsWith('{') && segment.EndsWith('}')).Select(static segment => segment[1..^1]).ToHashSet(StringComparer.Ordinal);
    private string NewHandleId() => $"op-{_clock.GetUtcNow().ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}"[..32];

    private string BuildBody(OperationRequest request, OperationDescriptor descriptor)
    {
        var routeNames = RouteNames();
        var excluded = _definition.QueryParameters ?? new HashSet<string>();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var parameter in descriptor.InputSchema.Where(parameter => !routeNames.Contains(parameter.Name) && !excluded.Contains(parameter.Name)))
            {
                if (!request.Parameters.TryGetValue(parameter.Name, out var value)) continue;
                var name = request.DryRun && parameter.Name == "srid" ? "targetSrid" : parameter.Name;
                writer.WritePropertyName(name);
                WriteValue(writer, value, parameter.Schema.Type);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter writer, string? value, Honua.Core.Features.WorkflowPackages.Domain.WorkflowSchemaValueType type)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        if (type == Honua.Core.Features.WorkflowPackages.Domain.WorkflowSchemaValueType.Text)
        {
            writer.WriteStringValue(value);
            return;
        }
        using var document = JsonDocument.Parse(value);
        document.RootElement.WriteTo(writer);
    }
}
