// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;

namespace Honua.ControlPlane.Lambda;

/// <summary>
/// Forwards a single reconcile / backstop request to the in-process Honua.Server host running under
/// the AWS Lambda Web Adapter, over the loopback HTTP port the adapter exposes. This is the seam that
/// lets the thin event entrypoint reuse the server's fully-wired reconcile graph without re-composing
/// the deep reconciler + store + backend dependency tree in this project.
/// </summary>
internal sealed class ReconcileForwarder(HttpClient httpClient, ReconcileForwarderOptions options)
{
    private const string BatchEventPath = "/internal/control-plane/events/batch-job-state-change";
    private const string BackstopPath = "/internal/control-plane/backstop/sweep";
    private const string ScheduledTickPath = "/internal/control-plane/scheduled-tick";
    private const string EventTokenHeader = "X-Honua-ControlPlane-Token";
    private const string ScheduledTickTokenHeader = "X-Honua-Control-Plane-Token";

    /// <summary>
    /// Posts a Batch state-change reconcile for one provider job id. No-op when the id is null/blank
    /// (a malformed or terminal event resolves to no active job).
    /// </summary>
    public async Task ReconcileBatchEventAsync(string? providerOperationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerOperationId))
        {
            return;
        }

        var uri = $"{options.BaseAddress.TrimEnd('/')}{BatchEventPath}?providerOperationId={Uri.EscapeDataString(providerOperationId)}";
        await PostAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Posts a single backstop sweep request.</summary>
    public async Task RunBackstopAsync(CancellationToken cancellationToken)
    {
        var uri = $"{options.BaseAddress.TrimEnd('/')}{BackstopPath}";
        await PostAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts one periodic tick of <paramref name="kind"/> to the scheduled-tick route, which
    /// validates the kind and runs it through the server's scheduled-tick dispatcher.
    /// </summary>
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

/// <summary>Configuration for <see cref="ReconcileForwarder"/>, sourced from Lambda env vars.</summary>
internal sealed class ReconcileForwarderOptions
{
    /// <summary>Loopback base address of the LWA-fronted host. Defaults to the adapter's port.</summary>
    public required string BaseAddress { get; init; }

    /// <summary>Optional shared secret sent as the control-plane event token header.</summary>
    public string? EventToken { get; init; }

    /// <summary>
    /// Shared secret for the scheduled-tick route. The bootstrap and the co-located server read the
    /// same Lambda environment, so this is the server's own <c>ControlPlane__ScheduledTickToken</c>.
    /// </summary>
    public string? ScheduledTickToken { get; init; }

    /// <summary>
    /// Builds options from the Lambda environment: <c>AWS_LWA_PORT</c>/<c>PORT</c> (default 8080) for
    /// the loopback address, <c>ControlPlane__EventToken</c> / <c>HONUA_CONTROL_PLANE_EVENT_TOKEN</c> for the event/backstop secret and
    /// <c>ControlPlane__ScheduledTickToken</c> for the scheduled-tick secret.
    /// </summary>
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
