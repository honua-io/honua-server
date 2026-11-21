// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.AspNetCore.Authentication;

namespace Honua.Infrastructure.Authentication;

// One admitted stream's authorization: the admission check, event-driven refreshes, the expiry
// monitor and the single transition to an ended authorization.
internal sealed class LiveStreamAuthorizationSession : IDisposable
{
    // Coordinate revalidation failure with normal endpoint completion.
    private const int StreamRunning = 0;
    private const int StreamEnded = 1;
    private const int StreamCompleted = 2;

    private readonly HttpContext _context;
    private readonly string? _scheme;
    private readonly TimeSpan _checkBudget;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private int _outcome = StreamRunning;
    private bool _eventDriven;

    // A null scheme admits an anonymous stream, whose grants are re-read without reauthentication.
    internal LiveStreamAuthorizationSession(HttpContext context, string? scheme)
    {
        _context = context;
        _scheme = scheme;
        Lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        MonitorStop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        // A resource check reads storage, so it extends the deadline; stopping the stream still cancels it.
        _checkBudget = context.GetEndpoint()?.Metadata.GetMetadata<ILiveStreamResourceAuthorization>() is null
            ? LiveStreamAuthorizationFilter.RevalidationInterval
            : LiveStreamAuthorizationFilter.RevalidationInterval + LiveStreamAuthorizationFilter.ResourceRevalidationBudget;
        Policies = new LiveStreamPolicyRefreshFeature { RevalidateAsync = RefreshAdmissionAsync };
        ExpiresAt = ResolveExpiry(context, scheme);
    }

    internal CancellationTokenSource Lifetime { get; }

    internal CancellationTokenSource MonitorStop { get; }

    internal LiveStreamPolicyRefreshFeature Policies { get; }

    internal LiveStreamAuthorizationFilter.RetainedWebSocketFeature? GuardedFeature { get; init; }

    internal DateTimeOffset? ExpiresAt { get; }

    internal bool Ended => Volatile.Read(ref _outcome) == StreamEnded;

    // Record completion before stopping the monitor, so a late failed
    // check cannot change an endpoint outcome that already completed.
    internal void MarkCompleted() => Interlocked.CompareExchange(ref _outcome, StreamCompleted, StreamRunning);

    internal LiveStreamSecurityRegistry.Registration? Register(LiveStreamSecurityRegistry? registry)
    {
        _eventDriven = registry is not null;
        return registry?.Register(ResolveIdentity(_context), RefreshAuthorizationAsync, _ => EndAuthorizationAsync());
    }

    internal async Task AdmitAsync(LiveStreamSecurityRegistry.Registration? registration)
    {
        if (ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await EndAuthorizationAsync().ConfigureAwait(false);
        }
        else if (registration is not null)
        {
            // Registration must precede the fresh check: an invalidation already
            // consumed before registration cannot notify this stream.
            await registration.RefreshAsync(MonitorStop.Token).ConfigureAwait(false);
        }
    }

    internal Task RefreshAuthorizationAsync(CancellationToken cancellationToken) =>
        RefreshAdmissionAsync(static _ => Task.FromResult(true), cancellationToken);

    private async Task RefreshAdmissionAsync(Func<CancellationToken, Task<bool>> pendingPolicy, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, MonitorStop.Token);
        deadline.CancelAfter(_checkBudget);
        bool authorized;
        var entered = false;
        try
        {
            await _admissionGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            authorized = await IsStillAuthorizedAsync(pendingPolicy, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            authorized = false;
        }
        finally
        {
            if (entered)
            {
                _admissionGate.Release();
            }
        }

        if (!MonitorStop.IsCancellationRequested && !authorized)
        {
            await EndAuthorizationAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> IsStillAuthorizedAsync(Func<CancellationToken, Task<bool>> pendingPolicy, CancellationToken cancellationToken)
    {
        if (_scheme is not null
            && !await LiveStreamAuthorizationFilter.RevalidateAsync(_context, _scheme, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await PortalAudienceSnapshot.RefreshAsync(_context, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Policies.RefreshAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false)
            && await pendingPolicy(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task EndAuthorizationAsync()
    {
        if (Interlocked.CompareExchange(ref _outcome, StreamEnded, StreamRunning) != StreamRunning)
        {
            return;
        }

        if (GuardedFeature?.Socket is { } socket)
        {
            await socket.EndAuthorizationAsync().ConfigureAwait(false);
            // Cancelling a receive that is still pending aborts the managed socket, and
            // Kestrel's teardown can then drop the close frame before it leaves: the
            // client saw 1006 instead of 1008. Let the endpoint
            // observe the client's close reply first, bounded by one interval.
            await socket.WaitForCloseReplyAsync(MonitorStop.Token).ConfigureAwait(false);
        }

        await Lifetime.CancelAsync().ConfigureAwait(false);
    }

    // Expiry is enforced on every tick; reauthentication runs on the tick only for streams
    // that no security event refreshes.
    internal async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(LiveStreamAuthorizationFilter.RevalidationInterval);
            while (await timer.WaitForNextTickAsync(MonitorStop.Token).ConfigureAwait(false))
            {
                if (ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    await EndAuthorizationAsync().ConfigureAwait(false);
                    return;
                }

                if (!_eventDriven)
                {
                    await RefreshAuthorizationAsync(MonitorStop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (MonitorStop.IsCancellationRequested)
        {
            // Endpoint completion or client disconnect.
        }
    }

    private static LiveStreamIdentity ResolveIdentity(HttpContext context)
    {
        var credential = context.Features.Get<LiveStreamCredentialFeature>();
        var principal = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value;
        var keyId = context.User.FindFirst("api_key_id")?.Value;
        return new LiveStreamIdentity(principal, keyId is null
            ? credential?.Credential : new LiveStreamCredential("AdminApiKey", keyId), credential?.BackingCredential);
    }

    // The earliest bound the admitted credential carries: its own expiry, a client certificate's
    // NotAfter, or the principal's exp claim.
    private static DateTimeOffset? ResolveExpiry(HttpContext context, string? scheme)
    {
        var expiresAt = context.Features.Get<LiveStreamCredentialFeature>()?.ExpiresAt
            ?? context.Features.Get<LiveStreamAdmissionFeature>()?.ExpiresAt
            ?? context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        if (scheme == ClientCertificateAuthenticationDefaults.AuthenticationScheme
            && context.Items[ClientCertificateHttpContextItems.ExtractionResult] is ClientCertificateExtractionResult
            { Certificate: { } certificate })
        {
            var certificateExpiry = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            expiresAt = expiresAt is null || certificateExpiry < expiresAt ? certificateExpiry : expiresAt;
        }

        if (expiresAt is null && long.TryParse(context.User.FindFirst("exp")?.Value,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return expiresAt;
    }

    public void Dispose()
    {
        Lifetime.Dispose();
        MonitorStop.Dispose();
        _admissionGate.Dispose();
    }
}
