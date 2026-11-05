// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;

namespace Honua.ControlPlane.Lambda;

internal sealed class ReconcileForwarder(HttpClient httpClient, ReconcileForwarderOptions options)
{
    private const string BatchEventPath = "/internal/control-plane/events/batch-job-state-change";
    private const string BackstopPath = "/internal/control-plane/backstop/sweep";
    private const string ScheduledTickPath = "/internal/control-plane/scheduled-tick";
    private const string EventTokenHeader = "X-Honua-ControlPlane-Token";
    private const string ScheduledTickTokenHeader = "X-Honua-Control-Plane-Token";

    public async Task ReconcileBatchEventAsync(string? providerOperationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerOperationId))
        {
            return;
        }

        var uri = $"{options.BaseAddress.TrimEnd('/')}{BatchEventPath}?providerOperationId={Uri.EscapeDataString(providerOperationId)}";
        await PostAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunBackstopAsync(CancellationToken cancellationToken)
    {
        var uri = $"{options.BaseAddress.TrimEnd('/')}{BackstopPath}";
        await PostAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunScheduledTickAsync(string kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var uri = $"{options.BaseAddress.TrimEnd('/')}{ScheduledTickPath}";
        var body = JsonSerializer.Serialize(
            new ScheduledTickEvent { Kind = kind },
            ControlPlaneLambdaJsonContext.Default.ScheduledTickEvent);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await PostAsync(uri, ScheduledTickTokenHeader, options.ScheduledTickToken, content, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task PostAsync(string uri, CancellationToken cancellationToken)
        => PostAsync(uri, EventTokenHeader, options.EventToken, content: null, cancellationToken);

    private async Task PostAsync(
        string uri,
        string tokenHeader,
        string? token,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation(tokenHeader, token);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

internal sealed class ReconcileForwarderOptions
{
    public required string BaseAddress { get; init; }

    public string? EventToken { get; init; }

    public string? ScheduledTickToken { get; init; }

    public static ReconcileForwarderOptions FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var port = FirstNonEmpty(env, "AWS_LWA_PORT", "PORT") ?? "8080";
        return new ReconcileForwarderOptions
        {
            BaseAddress = $"http://127.0.0.1:{port}",
            // Same precedence as the server's check (ControlPlane:EventToken, then the env var).
            EventToken = FirstNonEmpty(env, "ControlPlane__EventToken", "HONUA_CONTROL_PLANE_EVENT_TOKEN"),
            ScheduledTickToken = FirstNonEmpty(env, "ControlPlane__ScheduledTickToken"),
        };
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string?> env, params string[] keys)
        => keys
            .Select(key => env.TryGetValue(key, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
