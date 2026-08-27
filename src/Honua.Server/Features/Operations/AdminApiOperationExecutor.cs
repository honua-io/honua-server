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
        var missing = GetMissingRouteParameters(request);
        return Task.FromResult(new OperationValidation { IsValid = missing.Length == 0, Status = missing.Length == 0 ? "valid" : "invalid", Messages = missing });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var current = _httpContextAccessor.HttpContext ?? throw new InvalidOperationException("Admin operations require an active authenticated request.");
        var relativePath = BindPath(request, request.DryRun && _definition.SupportsDryRun ? _definition.DryRunPath : null);
        var uri = new Uri($"{current.Request.Scheme}://{current.Request.Host}/api/v1/admin{relativePath}");
        using var message = new HttpRequestMessage(_definition.Method, uri);
        if (current.Request.Headers.Authorization is { Count: > 0 } authorization)
            message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization.ToString());

        if (_definition.Method != HttpMethod.Get)
        {
            if (_definition.OpenApiOperationId == "importLayerSldStyle")
            {
                message.Content = new StringContent(request.Parameters.GetValueOrDefault("body") ?? string.Empty, Encoding.UTF8, "application/xml");
            }
            else
            {
                var routeNames = RouteNames();
                var body = request.Parameters.Where(pair => !routeNames.Contains(pair.Key)).ToDictionary(static pair => pair.Key, static pair => ParseValue(pair.Value), StringComparer.Ordinal);
                message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
        }

        using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return new OperationHandle
        {
            OperationId = OperationId,
            HandleId = $"op-{_clock.GetUtcNow().ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}"[..32],
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
    private static object? ParseValue(string? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException) { return value; }
    }
}
