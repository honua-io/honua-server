// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    private async Task PostAsync(string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (!string.IsNullOrEmpty(options.EventToken))
        {
            request.Headers.TryAddWithoutValidation("X-Honua-ControlPlane-Token", options.EventToken);
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
    /// Builds options from the Lambda environment: <c>AWS_LWA_PORT</c>/<c>PORT</c> (default 8080) for
    /// the loopback address and <c>HONUA_CONTROL_PLANE_EVENT_TOKEN</c> for the shared secret.
    /// </summary>
    public static ReconcileForwarderOptions FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var port = FirstNonEmpty(env, "AWS_LWA_PORT", "PORT") ?? "8080";
        return new ReconcileForwarderOptions
        {
            BaseAddress = $"http://127.0.0.1:{port}",
            EventToken = FirstNonEmpty(env, "HONUA_CONTROL_PLANE_EVENT_TOKEN"),
        };
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string?> env, params string[] keys)
        => keys
            .Select(key => env.TryGetValue(key, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
