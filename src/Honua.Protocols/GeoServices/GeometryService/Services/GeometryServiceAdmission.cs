// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Configuration;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// Bounds what Geometry Service operations hold at once in one process: how many run together
// and how many request bytes they are working on.
//
// Callers that are not signed in share half of each bound, so they cannot take the service
// from signed-in callers: their request body is limited to half of the byte bound, and the
// connection of a request that holds its place for twice the request timeout, response
// included, is closed. A request that does not fit waits briefly for a place, then gets the
// standard retry response.
internal sealed class GeometryServiceAdmission
{
    internal const int RetryAfterSeconds = 1;

    internal static readonly TimeSpan DefaultQueueWait = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan _longestHoldLimit = TimeSpan.FromDays(1);

    private readonly Lock _gate = new();
    private readonly Bound _bound;
    private readonly long _undeclaredBodyBytes;
    private readonly TimeSpan _queueWait;
    private readonly TimeSpan? _notSignedInHoldLimit;
    private readonly TimeProvider _time;

    private Bound _held;
    private Bound _heldNotSignedIn;
    private TaskCompletionSource _released = NewReleaseSignal();

    public GeometryServiceAdmission(IOptions<LimitsOptions> limits)
        : this(
            new Bound(
                DefaultMaxOperations(Environment.ProcessorCount),
                DefaultMaxRequestBytes(LargestAcceptedRequest(limits))),
            LargestAcceptedRequest(limits),
            new Timing(DefaultQueueWait, NotSignedInHoldLimit(limits.Value.Connections.RequestTimeout), TimeProvider.System))
    {
    }

    internal GeometryServiceAdmission(Bound bound, long undeclaredBodyBytes, Timing? timing = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bound.Operations, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(bound.RequestBytes, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(undeclaredBodyBytes);
        timing ??= new Timing(TimeSpan.Zero, null, TimeProvider.System);
        ArgumentOutOfRangeException.ThrowIfLessThan(timing.QueueWait, TimeSpan.Zero);

        _bound = bound;
        _undeclaredBodyBytes = undeclaredBodyBytes;
        _queueWait = timing.QueueWait;
        _notSignedInHoldLimit = timing.NotSignedInHoldLimit is { } limit && limit > TimeSpan.Zero
            ? (limit < _longestHoldLimit ? limit : _longestHoldLimit)
            : null;
        _time = timing.Time;
    }

    // Operations mostly wait on the database, so a small task still runs several at once.
    internal static int DefaultMaxOperations(int processorCount) => Math.Clamp(processorCount * 2, 8, 32);

    // The largest request the server accepts can always run alone; a quarter more is room for
    // the small requests that normally surround it.
    internal static long DefaultMaxRequestBytes(long largestAcceptedRequest)
        => largestAcceptedRequest > long.MaxValue - (largestAcceptedRequest / 4)
            ? long.MaxValue
            : largestAcceptedRequest + (largestAcceptedRequest / 4);

    // Time to compute and then to send the result: twice the request timeout, at most a day.
    internal static TimeSpan NotSignedInHoldLimit(TimeSpan requestTimeout)
        => requestTimeout <= TimeSpan.Zero
            ? TimeSpan.Zero
            : requestTimeout >= _longestHoldLimit / 2 ? _longestHoldLimit : requestTimeout * 2;

    private IResult BodyTooLarge(HttpContext context)
        => StandardErrorResponseFormatter.FormatError(context, new StandardErrorResponse(
            StatusCodes.Status413PayloadTooLarge,
            "Request payload exceeds maximum allowed size",
            $"Sign in to send more than {_bound.Half.RequestBytes:N0} bytes to this operation."));

    public static async ValueTask<object?> FilterAsync(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        // The service description computes nothing, so discovery is never refused.
        if (!RequestTelemetryClassifier.IsGeometryServiceOperation(context)
            || context.RequestServices.GetService<GeometryServiceAdmission>() is not { } admission)
        {
            return await next(invocation).ConfigureAwait(false);
        }

        var caller = GeometryServiceCallers.Of(context);
        if (caller == GeometryServiceCaller.NotSignedIn && !admission.TryLimitBody(context))
        {
            return admission.BodyTooLarge(context);
        }

        var place = await admission.EnterAsync(admission.MeasureRequest(context.Request), caller, context.RequestAborted)
            .ConfigureAwait(false);
        if (place is null)
        {
            // The same response every other shed request gets, in the caller's protocol.
            await BackpressureResponseWriter
                .WriteAsync(context, BackpressureKind.Saturated, RetryAfterSeconds, context.RequestAborted)
                .ConfigureAwait(false);
            return Results.Empty;
        }

        // Held until the response is written: the result is serialized after this filter returns.
        context.Response.RegisterForDispose(place);
        if (caller == GeometryServiceCaller.NotSignedIn && admission._notSignedInHoldLimit is { } holdLimit)
        {
            place.CloseAfter(context, holdLimit, admission._time);
        }

        return await next(invocation).ConfigureAwait(false);
    }

    internal Place? TryEnter(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryEnter(MeasureRequest(context.Request), GeometryServiceCallers.Of(context));
    }

    internal Place? TryEnter(long requestBytes, GeometryServiceCaller caller)
    {
        var request = Reservation(requestBytes, caller);
        lock (_gate)
        {
            return TryEnterLocked(request, caller);
        }
    }

    // Waits up to the queue wait for a place; null when none frees in time.
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The place is returned; the caller registers it for disposal with the response.")]
    internal async ValueTask<Place?> EnterAsync(long requestBytes, GeometryServiceCaller caller, CancellationToken cancellationToken)
    {
        var request = Reservation(requestBytes, caller);
        var deadline = _time.GetUtcNow() + _queueWait;
        while (true)
        {
            Task released;
            lock (_gate)
            {
                if (TryEnterLocked(request, caller) is { } place)
                {
                    return place;
                }

                released = _released.Task;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            try
            {
                await released.WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    private Place? TryEnterLocked(Bound request, GeometryServiceCaller caller)
    {
        var notSignedIn = caller == GeometryServiceCaller.NotSignedIn;
        var fits = notSignedIn
            ? _bound.Half.HasRoomFor(request, _heldNotSignedIn) && _bound.HasRoomFor(request, _held)
            : _bound.HasRoomForOrIsIdle(request, _held);
        if (!fits)
        {
            return null;
        }

        _held = _held.Plus(request);
        if (notSignedIn)
        {
            _heldNotSignedIn = _heldNotSignedIn.Plus(request);
        }

        return new Place(this, request, caller);
    }

    // A caller that is not signed in reserves at most half of the byte bound; its body is
    // limited to that in TryLimitBody.
    private Bound Reservation(long requestBytes, GeometryServiceCaller caller)
        => new(1, Math.Clamp(
            requestBytes,
            0,
            caller == GeometryServiceCaller.NotSignedIn ? _bound.Half.RequestBytes : long.MaxValue));

    // Limits the body of a request from a caller that is not signed in to half of the byte
    // bound. False when its declared length is already larger, or when its length is undeclared
    // and the limit cannot be applied.
    private bool TryLimitBody(HttpContext context)
    {
        var request = context.Request;
        var limit = _bound.Half.RequestBytes;
        if (request.ContentLength > limit)
        {
            return false;
        }

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return true;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not { IsReadOnly: false })
        {
            return request.ContentLength is not null;
        }

        if (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > limit)
        {
            feature.MaxRequestBodySize = limit;
        }

        return true;
    }

    private long MeasureRequest(HttpRequest request)
    {
        long queryBytes = request.QueryString.Value?.Length ?? 0;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return queryBytes;
        }

        // A body of undeclared length is counted as the largest the server accepts.
        return queryBytes + Math.Min(request.ContentLength ?? _undeclaredBodyBytes, _undeclaredBodyBytes);
    }

    private void Leave(Bound request, GeometryServiceCaller caller)
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            _held = _held.Minus(request);
            if (caller == GeometryServiceCaller.NotSignedIn)
            {
                _heldNotSignedIn = _heldNotSignedIn.Minus(request);
            }

            released = _released;
            _released = NewReleaseSignal();
        }

        released.TrySetResult();
    }

    private static TaskCompletionSource NewReleaseSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long LargestAcceptedRequest(IOptions<LimitsOptions> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return Math.Max(limits.Value.Edits.MaxPayloadSize, 1);
    }

    // Operations running together and the request bytes they are working on: a bound, or what is held.
    internal readonly record struct Bound(int Operations, long RequestBytes)
    {
        public Bound Half => new(Operations / 2, RequestBytes / 2);

        public bool HasRoomFor(Bound request, Bound held)
            => held.Operations < Operations && held.RequestBytes + request.RequestBytes <= RequestBytes;

        // A signed-in request larger than the byte bound still runs when nothing else is running:
        // the bound limits what runs together, not what one signed-in caller may send.
        public bool HasRoomForOrIsIdle(Bound request, Bound held)
            => held.Operations == 0 || HasRoomFor(request, held);

        public Bound Plus(Bound other) => new(Operations + other.Operations, RequestBytes + other.RequestBytes);

        public Bound Minus(Bound other) => new(Operations - other.Operations, RequestBytes - other.RequestBytes);
    }

    // How the timing of admission is set: the wait for a place, the hold limit for callers that
    // are not signed in (none when null), and the clock.
    internal sealed record Timing(TimeSpan QueueWait, TimeSpan? NotSignedInHoldLimit, TimeProvider Time);

    internal sealed class Place(GeometryServiceAdmission admission, Bound request, GeometryServiceCaller caller) : IDisposable
    {
        private const int Held = 0;
        private const int Left = 1;
        private const int Closing = 2;

        private int _state;
        private ITimer? _holdLimit;

        // Closes the connection once the place has been held this long, unless the request has
        // already ended. The place is freed when the request ends, as for any other request. The
        // connection's lifetime feature is kept, not the request context, which the server resets.
        public void CloseAfter(HttpContext context, TimeSpan limit, TimeProvider time)
        {
            if (context.Features.Get<IHttpRequestLifetimeFeature>() is not { } connection)
            {
                return;
            }

            _holdLimit = time.CreateTimer(
                static state =>
                {
                    var (place, heldConnection) = ((Place, IHttpRequestLifetimeFeature))state!;
                    if (Interlocked.CompareExchange(ref place._state, Closing, Held) == Held)
                    {
                        heldConnection.Abort();
                    }
                },
                (this, connection),
                limit,
                Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, Left) != Left)
            {
                _holdLimit?.Dispose();
                admission.Leave(request, caller);
            }
        }
    }
}
