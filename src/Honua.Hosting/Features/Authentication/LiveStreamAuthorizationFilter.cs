// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Security.Claims;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Revalidates long-lived transport credentials in a fresh authentication scope.
/// An authentication handler caches its result for a request, so authenticating the
/// original context again would keep an expired or revoked principal alive.
/// </summary>
internal sealed class LiveStreamAuthorizationFilter : IEndpointFilter
{
    internal static readonly TimeSpan RevalidationInterval = TimeSpan.FromSeconds(1);
    internal const string AuthorizationEnded = "authorization-ended";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        // Authorization can synthesize a ticket named "context.User" for a
        // principal hydrated by credential middleware. The framework-owned actor
        // stamp retains the actual handler name in that case.
        var stampedScheme = CanonicalSecurityActor.FindStampedValue(context.User, CanonicalSecurityActor.AuthenticationSchemeClaim);
        var admittedScheme = stampedScheme
            ?? context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Ticket?.AuthenticationScheme
            ?? context.User.Identity.AuthenticationType;
        var schemes = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        // Canonical actor bindings normalize names, whereas the handler registry is
        // case-sensitive. Resolve the registered name and reject ambiguous matches.
        var registered = (await schemes.GetAllSchemesAsync().ConfigureAwait(false)).ToArray();
        var matches = registered
            .Where(candidate => string.Equals(candidate.Name, admittedScheme, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 && stampedScheme is null)
        {
            // Tenantless principals may not receive a request-binding stamp. The
            // identity's handler type remains available behind a synthetic ticket.
            matches = registered.Where(candidate => string.Equals(candidate.Name,
                context.User.Identity.AuthenticationType, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (matches.Length != 1)
        {
            return Results.Unauthorized();
        }
        var scheme = matches[0].Name;

        var originalAbort = context.RequestAborted;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(originalAbort);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(originalAbort);
        var socketFeature = context.Features.Get<IHttpWebSocketFeature>();
        var guardedFeature = socketFeature is null ? null : new RetainedWebSocketFeature(socketFeature);
        if (guardedFeature is not null)
        {
            context.Features.Set<IHttpWebSocketFeature>(guardedFeature);
        }

        context.RequestAborted = lifetime.Token;
        var ended = false;
        var monitor = MonitorAsync();
        try
        {
            return await next(invocation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return Results.Empty;
        }
        finally
        {
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
            context.RequestAborted = originalAbort;
            context.Features.Set(socketFeature);
            if (ended && !originalAbort.IsCancellationRequested && guardedFeature?.Socket is null)
            {
                // The endpoint has stopped writing, so the terminal frame cannot split
                // an in-flight SSE event. Never include tenant, layer or cursor metadata.
                using var deadline = new CancellationTokenSource(RevalidationInterval);
                try
                {
                    await context.Response.WriteAsync(
                        "event: status\ndata: {\"status\":\"error\",\"code\":\"authorization-ended\"}\n\n",
                        deadline.Token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // A disconnected client cannot receive a terminal outcome.
                }
            }

            guardedFeature?.Socket?.Release();
        }

        async Task MonitorAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(RevalidationInterval);
                while (await timer.WaitForNextTickAsync(monitorStop.Token).ConfigureAwait(false))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(monitorStop.Token);
                    deadline.CancelAfter(RevalidationInterval);
                    if (await RevalidateAsync(context, scheme, deadline.Token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    ended = true;
                    if (guardedFeature?.Socket is { } socket)
                    {
                        await socket.EndAuthorizationAsync().ConfigureAwait(false);
                    }

                    await lifetime.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested)
            {
                // Endpoint completion or client disconnect.
            }
        }
    }

    internal static async Task<bool> RevalidateAsync(HttpContext original, string scheme, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = original.RequestServices.CreateAsyncScope();
            var check = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = cancellationToken };
            check.Request.Method = original.Request.Method;
            check.Request.Scheme = original.Request.Scheme;
            check.Request.Host = original.Request.Host;
            check.Request.PathBase = original.Request.PathBase;
            check.Request.Path = original.Request.Path;
            check.Request.QueryString = original.Request.QueryString;
            foreach (var header in original.Request.Headers)
            {
                check.Request.Headers[header.Key] = header.Value;
            }

            check.Connection.RemoteIpAddress = original.Connection.RemoteIpAddress;
            check.Connection.ClientCertificate = original.Connection.ClientCertificate;
            check.Features.Set(new LiveStreamRevalidationFeature());
            var result = await check.AuthenticateAsync(scheme).WaitAsync(cancellationToken).ConfigureAwait(false);
            return result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true
                && Claims(original.User).SequenceEqual(Claims(result.Principal));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Revocation-store outages and authentication timeouts fail closed.
            return false;
        }
    }

    private static IEnumerable<(string Type, string Value, string Issuer)> Claims(ClaimsPrincipal principal) =>
        principal.Claims
            // These are request binding projections added after authentication, not
            // issuer claims. The original request's resolved scope stays immutable.
            .Where(claim => !CanonicalSecurityActor.IsFrameworkOwnedClaim(claim)
                || claim.Type is not (CanonicalSecurityActor.CanonicalActorClaim
                    or CanonicalSecurityActor.EffectiveTenantClaim or CanonicalSecurityActor.ScopeCeilingClaim
                    or CanonicalSecurityActor.AuthenticationSchemeClaim or "honua:issuer"))
            .Select(claim => (claim.Type, claim.Value, claim.Issuer)).Order();

    private sealed class RetainedWebSocketFeature(IHttpWebSocketFeature inner) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;
        internal RetainedWebSocket? Socket { get; private set; }

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            Socket = new RetainedWebSocket(await inner.AcceptAsync(context).ConfigureAwait(false));
            return Socket;
        }
    }

    private sealed class RetainedWebSocket(WebSocket inner) : WebSocket
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private bool _authorizationEnded;
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override string? SubProtocol => inner.SubProtocol;
        public override WebSocketState State => inner.State;
        public override void Abort() => inner.Abort();
        // The filter owns final disposal so it can emit the authorization close frame.
        public override void Dispose() { }
        internal void Release() { inner.Dispose(); _send.Dispose(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            inner.CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            inner.ReceiveAsync(buffer, cancellationToken);
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_authorizationEnded)
                {
                    throw new WebSocketException(WebSocketError.InvalidState);
                }

                await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }
        }

        internal async Task EndAuthorizationAsync()
        {
            using var deadline = new CancellationTokenSource(RevalidationInterval);
            try
            {
                await _send.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    _authorizationEnded = true;
                    if (inner.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await inner.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, AuthorizationEnded, deadline.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _send.Release();
                }
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                inner.Abort();
            }
        }
    }
}

// Server-created marker: validating an already admitted credential is not a new
// use of the token for replay-protection purposes. Clients cannot supply this feature.
internal sealed class LiveStreamRevalidationFeature { }
