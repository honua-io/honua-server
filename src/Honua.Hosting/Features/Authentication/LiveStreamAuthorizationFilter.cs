// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Honua.Infrastructure.Authentication;

internal sealed class LiveStreamAuthorizationFilter : IEndpointFilter
{
    internal static readonly TimeSpan RevalidationInterval = TimeSpan.FromSeconds(1);

    // Extra time a resource check gets on top of the credential check's interval.
    internal static readonly TimeSpan ResourceRevalidationBudget = TimeSpan.FromSeconds(3);

    internal const string AuthorizationEnded = "authorization-ended";

    // Coordinate revalidation failure with normal endpoint completion.
    private const int StreamRunning = 0;
    private const int StreamEnded = 1;
    private const int StreamCompleted = 2;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var anonymous = context.User.Identity?.IsAuthenticated != true;
        if (anonymous && context.GetEndpoint()?.Metadata.GetMetadata<AllowAnonymousLiveStreamMetadata>() is null)
        {
            return Results.Unauthorized();
        }

        // Authorization can synthesize a ticket named "context.User" for a
        // principal hydrated by credential middleware. Preserve its admission
        // handler separately from the canonical actor scheme.
        var stampedScheme = CanonicalSecurityActor.FindStampedValue(context.User, CanonicalSecurityActor.AuthenticationSchemeClaim);
        var ticketScheme = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Ticket?.AuthenticationScheme;
        var schemes = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        // Canonical actor bindings normalize names, whereas the handler registry is
        // case-sensitive. Resolve the registered name and reject ambiguous matches.
        var registered = (await schemes.GetAllSchemesAsync().ConfigureAwait(false)).ToArray();
        // A portal ticket may contain an API-key actor. Replay the transport handler
        // that admitted it; the actor stamp is only a fallback for synthetic tickets.
        var admittedScheme = context.Features.Get<LiveStreamAdmissionFeature>()?.AuthenticationScheme
            ?? (registered.Any(candidate => string.Equals(candidate.Name, ticketScheme, StringComparison.OrdinalIgnoreCase))
            ? ticketScheme : stampedScheme ?? context.User.Identity?.AuthenticationType);
        var matches = registered
            .Where(candidate => string.Equals(candidate.Name, admittedScheme, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 && stampedScheme is null)
        {
            // Tenantless principals may not receive a request-binding stamp. The
            // identity's handler type remains available behind a synthetic ticket.
            matches = registered.Where(candidate => string.Equals(candidate.Name,
                context.User.Identity?.AuthenticationType, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (!anonymous && matches.Length != 1)
        {
            return Results.Unauthorized();
        }
        var scheme = anonymous ? null : matches[0].Name;

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
        var outcome = StreamRunning;
        // A resource check reads storage, so it extends the deadline; stopping the stream still cancels it.
        var checkBudget = context.GetEndpoint()?.Metadata.GetMetadata<ILiveStreamResourceAuthorization>() is null
            ? RevalidationInterval
            : RevalidationInterval + ResourceRevalidationBudget;
        var previousPolicies = context.Features.Get<LiveStreamPolicyRefreshFeature>();
        using var admissionGate = new SemaphoreSlim(1, 1);
        var policies = new LiveStreamPolicyRefreshFeature();
        policies.RevalidateAsync = RefreshAdmissionAsync;
        context.Features.Set(policies);
        var registry = context.RequestServices.GetService<LiveStreamSecurityRegistry>();
        var credential = context.Features.Get<LiveStreamCredentialFeature>();
        var principal = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value;
        var keyId = context.User.FindFirst("api_key_id")?.Value;
        var identity = new LiveStreamIdentity(principal, keyId is null
            ? credential?.Credential : new LiveStreamCredential("AdminApiKey", keyId), credential?.BackingCredential);
        var expiresAt = credential?.ExpiresAt
            ?? context.Features.Get<LiveStreamAdmissionFeature>()?.ExpiresAt
            ?? context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        if (!anonymous && scheme == ClientCertificateAuthenticationDefaults.AuthenticationScheme
            && context.Items[ClientCertificateHttpContextItems.ExtractionResult] is ClientCertificateExtractionResult
            { Certificate: { } certificate })
        {
            var certificateExpiry = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            expiresAt = expiresAt is null || certificateExpiry < expiresAt ? certificateExpiry : expiresAt;
        }
        if (expiresAt is null && long.TryParse(context.User.FindFirst("exp")?.Value,
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= -62135596800 and <= 253402300799)
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        await using var registration = registry?.Register(identity, RefreshAuthorizationAsync, _ => EndAuthorizationAsync());
        var monitor = MonitorAsync();
        try
        {
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                await EndAuthorizationAsync().ConfigureAwait(false);
            }
            else if (registration is not null)
            {
                // Registration must precede the fresh check: an invalidation already
                // consumed before registration cannot notify this stream.
                await registration.RefreshAsync(false, monitorStop.Token).ConfigureAwait(false);
            }
            if (Volatile.Read(ref outcome) == StreamEnded)
            {
                return Results.Unauthorized();
            }
            var completed = await next(invocation).ConfigureAwait(false);
            // Record completion before stopping the monitor, so a late failed
            // check cannot change an endpoint outcome that already completed.
            Interlocked.CompareExchange(ref outcome, StreamCompleted, StreamRunning);
            // SSE handlers can swallow cancellation during the first write and
            // return an empty result. Preserve typed endpoint denials, but turn
            // that empty completion into an HTTP denial if streaming never began.
            if (completed is EmptyHttpResult && Volatile.Read(ref outcome) == StreamEnded
                && !context.Response.HasStarted)
            {
                return Results.Unauthorized();
            }
            return completed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // A stream that lost authorization before emitting anything carries the
            // outcome in its status code; an unstarted body has no frame to carry it.
            return Volatile.Read(ref outcome) == StreamEnded && !context.Response.HasStarted
                ? Results.Unauthorized()
                : Results.Empty;
        }
        finally
        {
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
            if (registration is not null)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
            context.Features.Set(previousPolicies);
            context.RequestAborted = originalAbort;
            context.Features.Set(socketFeature);
            // An unstarted response still belongs to the endpoint's typed result.
            // Writing a frame there would open a 200 stream body underneath a pending
            // denial, so only a response already streaming receives the terminal frame.
            if (Volatile.Read(ref outcome) == StreamEnded && !originalAbort.IsCancellationRequested
                && guardedFeature?.Socket is null && context.Response.HasStarted)
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

        Task RefreshAuthorizationAsync(CancellationToken cancellationToken) =>
            RefreshAdmissionAsync(static _ => Task.FromResult(true), cancellationToken);

        async Task RefreshAdmissionAsync(Func<CancellationToken, Task<bool>> pendingPolicy, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, monitorStop.Token);
            deadline.CancelAfter(checkBudget);
            var authorized = false;
            var entered = false;
            try
            {
                await admissionGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                authorized = anonymous || await RevalidateAsync(context, scheme!, deadline.Token).ConfigureAwait(false);
                if (authorized)
                {
                    await PortalAudienceSnapshot.RefreshAsync(context, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                authorized = authorized && await policies.RefreshAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false)
                    && await pendingPolicy(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                authorized = false;
            }
            finally
            {
                if (entered)
                {
                    admissionGate.Release();
                }
            }
            if (!monitorStop.IsCancellationRequested && !authorized)
            {
                await EndAuthorizationAsync().ConfigureAwait(false);
            }
        }

        async Task EndAuthorizationAsync()
        {
            if (Interlocked.CompareExchange(ref outcome, StreamEnded, StreamRunning) != StreamRunning)
            {
                return;
            }
            if (guardedFeature?.Socket is { } socket)
            {
                await socket.EndAuthorizationAsync().ConfigureAwait(false);
                // Cancelling a receive that is still pending aborts the managed socket, and
                // Kestrel's teardown can then drop the close frame before it leaves: the
                // client saw 1006 instead of 1008. Let the endpoint
                // observe the client's close reply first, bounded by one interval.
                await socket.WaitForCloseReplyAsync(monitorStop.Token).ConfigureAwait(false);
            }
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        async Task MonitorAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(RevalidationInterval);
                while (await timer.WaitForNextTickAsync(monitorStop.Token).ConfigureAwait(false))
                {
                    if (expiresAt <= DateTimeOffset.UtcNow)
                    {
                        await EndAuthorizationAsync().ConfigureAwait(false);
                        return;
                    }
                    if (registry is null)
                    {
                        await RefreshAuthorizationAsync(monitorStop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested)
            {
                // Endpoint completion or client disconnect.
            }
        }
    }

    private static async Task<bool> IsResourceStillAuthorizedAsync(
        HttpContext original, HttpContext check, ILiveStreamResourceAuthorization resource)
    {
        check.Request.RouteValues = new RouteValueDictionary(original.Request.RouteValues);
        CarryResolvedTenant(original, check);
        return await resource.IsAuthorizedAsync(check, check.RequestAborted).ConfigureAwait(false);
    }

    // The check runs in a fresh scope that tenant middleware never saw: carry the tenant the stream's request resolved.
    private static void CarryResolvedTenant(HttpContext original, HttpContext check)
    {
        if (original.RequestServices.GetService<ITenantContext>() is not RequestTenantContext resolved
            || check.RequestServices.GetService<ITenantContext>() is not RequestTenantContext fresh)
        {
            return;
        }

        if (resolved.IncludesLegacyUnassigned)
        {
            fresh.SetSingleTenantDefault(resolved.TenantId);
        }
        else
        {
            fresh.Set(resolved.TenantId, resolved.Source);
        }

        CanonicalSecurityActor.StampRequestBinding(check.User, resolved.TenantId);
    }

    internal static async Task<bool> RevalidateAsync(HttpContext original, string scheme, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = original.RequestServices.CreateAsyncScope();
            // An authentication handler caches its result for a request, so authenticating the original context again would keep an expired or revoked principal alive.
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
            if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true
                || !Claims(original.User).SequenceEqual(Claims(result.Principal)))
            {
                return false;
            }

            check.User = result.Principal;
            return original.GetEndpoint()?.Metadata.GetMetadata<ILiveStreamResourceAuthorization>() is not { } resource
                || await IsResourceStillAuthorizedAsync(original, check, resource).ConfigureAwait(false);
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
            .Where(claim => !CanonicalSecurityActor.IsRequestBindingProjection(claim))
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

    internal sealed class RetainedWebSocket(WebSocket inner) : WebSocket
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly TaskCompletionSource _receiveEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _authorizationEnded;
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override string? SubProtocol => inner.SubProtocol;
        public override WebSocketState State => inner.State;
        public override void Abort() => inner.Abort();
        // The filter owns final disposal so it can emit the authorization close frame.
        public override void Dispose() { }
        internal void Release() { inner.Dispose(); _send.Dispose(); }
        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (inner.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await inner.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _send.Release();
            }
        }
        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // A policy Close may have won while this caller waited for the writer.
                if (inner.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _send.Release();
            }
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                var result = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _receiveEnded.TrySetResult();
                }

                return result;
            }
            catch (Exception)
            {
                _receiveEnded.TrySetResult();
                throw;
            }
        }
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

        internal async Task WaitForCloseReplyAsync(CancellationToken cancellationToken)
        {
            // Only a sent close awaits a reply; a socket the client already closed or that
            // was aborted has nothing left in flight.
            if (inner.State != WebSocketState.CloseSent)
            {
                return;
            }

            try
            {
                await _receiveEnded.Task.WaitAsync(RevalidationInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The close frame has been sent; a client that never replies is cancelled next.
            }
        }
    }
}

// Server-created marker: validating an already admitted credential is not a new
// use of the token for replay-protection purposes. Clients cannot supply this feature.
internal sealed class LiveStreamRevalidationFeature { }
