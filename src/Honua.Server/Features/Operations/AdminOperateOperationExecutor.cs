// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>Thin executor that binds a release/operate operation to its existing Admin REST route.</summary>
internal sealed class AdminOperateOperationExecutor : IOperationExecutor
{
    public const string HttpClientName = "admin-operate-operation-loopback";
    private readonly AdminOperateOperationCatalog.Definition _definition;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _clock;

    public AdminOperateOperationExecutor(AdminOperateOperationCatalog.Definition definition, IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor, TimeProvider clock)
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
        var missing = RouteNames(_definition.Path)
            .Where(name => string.IsNullOrWhiteSpace(request.Parameters.GetValueOrDefault(name)))
            .Select(name => $"Required route parameter '{name}' is missing.").ToArray();
        return Task.FromResult(new OperationValidation
        {
            IsValid = missing.Length == 0,
            Status = missing.Length == 0 ? "valid" : "invalid",
            Messages = missing
        });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var current = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Admin operations require an active authenticated request.");
        var dryRun = request.DryRun && _definition.SupportsDryRun;
        var path = BindPath(request, dryRun ? _definition.DryRunPath! : _definition.Path);
        var method = dryRun ? _definition.DryRunMethod ?? _definition.Method : _definition.Method;
        var uri = new Uri($"{current.Request.Scheme}://{current.Request.Host}/api/v1/admin{AppendQuery(path, request)}");
        using var message = new HttpRequestMessage(method, uri);
        if (current.Request.Headers.Authorization is { Count: > 0 } authorization)
            message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization.ToString());

        if (method != HttpMethod.Get)
        {
            if (string.Equals(_definition.ContentType, "application/octet-stream", StringComparison.Ordinal))
            {
                message.Content = new StringContent(request.Parameters.GetValueOrDefault("body") ?? string.Empty, Encoding.UTF8, _definition.ContentType);
            }
            else
            {
                var routeNames = RouteNames(path);
                var body = request.Parameters.Where(pair => !routeNames.Contains(pair.Key))
                    .ToDictionary(static pair => pair.Key, static pair => ParseValue(pair.Value), StringComparer.Ordinal);
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
            Result = new OperationResultSummary
            {
                Summary = $"{_definition.Title} completed.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["response"] = payload }
            }
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult(new OperationStatus { OperationId = OperationId, HandleId = handle.HandleId, Status = handle.Status, Result = handle.Result });

    private static string BindPath(OperationRequest request, string path)
    {
        foreach (var name in RouteNames(path))
            path = path.Replace($"{{{name}}}", Uri.EscapeDataString(request.Parameters.GetValueOrDefault(name)
                ?? throw new ArgumentException($"Required route parameter '{name}' is missing.")), StringComparison.Ordinal);
        return path;
    }

    private string AppendQuery(string path, OperationRequest request)
    {
        if (_definition.Method != HttpMethod.Get) return path;
        var routeNames = RouteNames(_definition.Path);
        var query = request.Parameters.Where(pair => !routeNames.Contains(pair.Key) && pair.Value is not null)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}").ToArray();
        return query.Length == 0 ? path : $"{path}?{string.Join('&', query)}";
    }

    private static HashSet<string> RouteNames(string path) => path.Split('/')
        .Where(static segment => segment.StartsWith('{') && segment.EndsWith('}'))
        .Select(static segment => segment[1..^1]).ToHashSet(StringComparer.Ordinal);

    private static object? ParseValue(string? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException) { return value; }
    }
}
