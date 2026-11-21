// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net.WebSockets;
using Honua.Core.Features.Realtime;

namespace Honua.Infrastructure.Realtime;

// Shared admission and terminal frames for long-lived streams registered with IRealtimeStreamLifecycle.
internal static class RealtimeStreamAdmission
{
    internal const string SseTransport = "sse";
    internal const string WebSocketTransport = "websocket";
    internal const int RetryAfterSeconds = 2;

    // RFC 6455 section 7.4.1 reserves 1012 for "service restart"; WebSocketCloseStatus has no member for it.
    internal const WebSocketCloseStatus ServiceRestart = (WebSocketCloseStatus)1012;
    internal const string ServiceRestartDescription = "Service restart.";

    // Null means the task is draining; the caller must answer with WriteDraining or Draining.
    // Hosts without the realtime lifecycle (tests, single-purpose hosts) get a session that never drains.
    internal static IRealtimeStreamSession? TryOpen(HttpContext context, RealtimeFamily family, string transport)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lifecycle = context.RequestServices?.GetService<IRealtimeStreamLifecycle>();
        return lifecycle is null
            ? UnmanagedSession.Instance
            : lifecycle.TryOpen(new RealtimeStreamRegistration(family, transport, context.Abort));
    }

    internal static IResult Draining(HttpContext context)
    {
        ApplyDrainingHeaders(context.Response);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    internal static void WriteDraining(HttpResponse response)
    {
        ApplyDrainingHeaders(response);
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    }

    // The terminal SSE frame: clients reconnect after the jittered delay, normally to another task.
    internal static async Task WriteSseRetryAsync(HttpResponse response, IRealtimeStreamSession session,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync(FormatSseRetry(session), cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string FormatSseRetry(IRealtimeStreamSession session)
        => "retry: " + session.RetryMilliseconds.ToString(CultureInfo.InvariantCulture) + "\n\n";

    private static void ApplyDrainingHeaders(HttpResponse response)
    {
        response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        response.Headers.CacheControl = "no-store";
    }

    private sealed class UnmanagedSession : IRealtimeStreamSession
    {
        internal static readonly UnmanagedSession Instance = new();

        public CancellationToken DrainingToken => CancellationToken.None;

        public int RetryMilliseconds => 1000;

        public void RecordDelivery(DateTimeOffset committedAt) { }

        public void RecordReset(string reason) { }

        public void RecordAuthorizationFilter() { }

        public void RecordRevocation() { }

        public void Dispose() { }
    }
}
