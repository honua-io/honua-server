// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security.Admission;

/// <summary>Owns one charged invocation and its bounded authoritative cancellation/cleanup lifecycle.</summary>
internal sealed partial class AiExecutionAdmissionCoordinator(
    IServiceScopeFactory scopes,
    IOptions<AiExecutionAdmissionOptions> options,
    TimeProvider clock,
    ILogger<AiExecutionAdmissionCoordinator> logger) : IAiExecutionAdmission
{
    private readonly string _deploymentId = options.Value.DeploymentId;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<AiExecutionAdmissionCoordinator> _logger = logger;
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(2);

    public async Task<AiFinancialPriceBasis?> GetFinancialBasisAsync(AiExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.IsValid || context.DeploymentId != _deploymentId) throw new AiAdmissionException("invalid_context");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AcquireTimeout);
            await using var scope = scopes.CreateAsyncScope();
            var state = await scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>()
                .GetStateAsync(context.DeploymentId, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (state is null) throw new AiAdmissionException("uninitialized");
            if (!state.Policy.IsValid) throw new AiAdmissionException("invalid_policy");
            return state.Policy.Financial?.Basis;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AiAdmissionException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StoreFailed(_logger, ex.GetType().Name);
            throw new AiAdmissionException("unavailable");
        }
    }

    public Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, CancellationToken cancellationToken)
        => AcquireCoreAsync(context, null, cancellationToken);

    public Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, AiFinancialRequest request, CancellationToken cancellationToken)
        => AcquireCoreAsync(context, request, cancellationToken);

    private async Task<IAiExecutionPermit> AcquireCoreAsync(AiExecutionContext context, AiFinancialRequest? request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context is not { IsValid: true } || context.DeploymentId != _deploymentId)
            throw new AiAdmissionException("invalid_context");

        // Exactly one identity and one acquire. An ambiguous store result is never retried.
        var attemptId = Guid.NewGuid();
        AiAdmissionDecision decision;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AcquireTimeout);
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>();
            decision = await (request is null ? store.TryAcquireAsync(context, attemptId, timeout.Token)
                : store.TryAcquireAsync(context, attemptId, request, timeout.Token)).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StoreFailed(_logger, ex.GetType().Name);
            throw new AiAdmissionException("unavailable");
        }

        if (decision.Reservation is not { } reservation)
            throw new AiAdmissionException(decision.Code, decision.RetryAfterSeconds);
        if (decision.Code != "admitted" || reservation.DeploymentId != context.DeploymentId || reservation.AttemptId != attemptId
            || reservation.Generation == Guid.Empty || reservation.Epoch <= 0
            || reservation.LeaseExpiresUtc < reservation.DeadlineUtc
            || (reservation.Financial is { } financial && (request is null || financial.RequestDigest != request.RequestDigest
                || financial.BasisDigest.Length != 64 || !financial.BasisDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new AiAdmissionException("unavailable");
        if (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(reservation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new Permit(this, reservation, cancellationToken);
    }

    private async Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>()
                .IsCurrentAsync(reservation, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested)
                StoreFailed(_logger, ex.GetType().Name);
            return false;
        }
    }

    private async Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>()
                .CommitFinancialSendAsync(reservation, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StoreFailed(_logger, ex.GetType().Name);
            return false;
        }
    }

    private async Task RecordFinancialUsageAsync(AiAdmissionReservation reservation, AiFinancialUsage usage)
    {
        try
        {
            // Provider usage may arrive with caller cancellation/stop. Original debt can
            // still settle under its original basis using bounded independent cleanup.
            using var cleanup = new CancellationTokenSource(ReleaseTimeout);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>()
                .SettleFinancialAsync(reservation, usage, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StoreFailed(_logger, ex.GetType().Name);
            // Uncertain settlement never releases the full retained reservation.
        }
    }

    private async Task ReleaseAsync(AiAdmissionReservation reservation)
    {
        try
        {
            // Independent of caller/stop cancellation. Expiry recovers capacity if release fails.
            using var cleanup = new CancellationTokenSource(ReleaseTimeout);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>()
                .ReleaseAsync(reservation, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReleaseFailed(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(9540, LogLevel.Warning, "AI admission state could not be verified ({ExceptionType})")]
    private static partial void StoreFailed(ILogger logger, string exceptionType);

    [LoggerMessage(9541, LogLevel.Warning, "AI admission reservation cleanup failed ({ExceptionType}); fixed lease expiry remains in effect")]
    private static partial void ReleaseFailed(ILogger logger, string exceptionType);

    private sealed class Permit : IAiExecutionPermit
    {
        private readonly AiExecutionAdmissionCoordinator _owner;
        private readonly AiAdmissionReservation _reservation;
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _operation;
        private readonly CancellationTokenSource _pollStop = new();
        private readonly Task _poll;
        private readonly object _cancelLock = new();
        private readonly CancellationToken _token;
        private Task _cancel = Task.CompletedTask;
        private int _begun;
        private int _sent;
        private int _disposed;
        private int _invalidated;

        public Permit(AiExecutionAdmissionCoordinator owner, AiAdmissionReservation reservation, CancellationToken caller)
        {
            _owner = owner;
            _reservation = reservation;
            _caller = caller;
            // Early local expiry is conservative. Authoritative polling/rechecks prevent clock skew extending a grant.
            var remaining = reservation.DeadlineUtc - owner._clock.GetUtcNow();
            remaining = TimeSpan.FromSeconds(Math.Clamp(remaining.TotalSeconds, 0, AiAdmissionPolicy.MaximumOperationSeconds));
            _deadline = new CancellationTokenSource(remaining, owner._clock);
            _operation = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token);
            _token = _operation.Token;
            _poll = PollAsync();
        }

        public CancellationToken Token => _token;
        public bool HasFinancialReservation => _reservation.Financial is not null;
        public Task RecordFinancialUsageAsync(AiFinancialUsage usage, CancellationToken cancellationToken)
            => HasFinancialReservation ? _owner.RecordFinancialUsageAsync(_reservation, usage) : Task.CompletedTask;

        public async Task BeginInvocationAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _begun, 1, 0) != 0)
                throw new AiAdmissionException("duplicate_invocation");
            await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task AuthorizeSendAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _begun) != 1 || Interlocked.CompareExchange(ref _sent, 1, 0) != 0)
                throw new AiAdmissionException("duplicate_invocation");
            await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (HasFinancialReservation && !await _owner.CommitFinancialSendAsync(_reservation, cancellationToken).ConfigureAwait(false))
                throw new AiAdmissionException("financial_stop");
            // A committed but interrupted claim remains full debt and cannot be reused.
            await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task EnsureCurrentAsync(CancellationToken cancellationToken)
        {
            _caller.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0 || Token.IsCancellationRequested)
                throw new AiAdmissionException("invalidated");
            var current = await _owner.IsCurrentAsync(_reservation, cancellationToken).ConfigureAwait(false);
            // A true reply cannot revive a grant whose caller, deadline or owner changed while awaiting it.
            _caller.ThrowIfCancellationRequested();
            if (!current || Volatile.Read(ref _disposed) != 0 || Token.IsCancellationRequested)
            {
                Invalidate();
                throw new AiAdmissionException("invalidated");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private void Invalidate()
        {
            lock (_cancelLock)
            {
                if (_invalidated == 0)
                {
                    _invalidated = 1;
                    _cancel = _operation.CancelAsync();
                }
            }
        }

        private async Task PollAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AiAdmissionPolicy.StopPollMilliseconds), _owner._clock);
            try
            {
                while (await timer.WaitForNextTickAsync(_pollStop.Token).ConfigureAwait(false))
                {
                    if (Token.IsCancellationRequested
                        || !await _owner.IsCurrentAsync(_reservation, _pollStop.Token).ConfigureAwait(false))
                    {
                        Invalidate();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_pollStop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StoreFailed(_owner._logger, ex.GetType().Name);
                Invalidate();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Invalidate();
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _pollStop.CancelAsync().WaitAsync(cleanup.Token).ConfigureAwait(false);
                await _poll.WaitAsync(cleanup.Token).ConfigureAwait(false);
                Task cancelled;
                lock (_cancelLock) cancelled = _cancel;
                await cancelled.WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReleaseFailed(_owner._logger, ex.GetType().Name);
            }
            finally
            {
                await _owner.ReleaseAsync(_reservation).ConfigureAwait(false);
                _pollStop.Dispose();
                _operation.Dispose();
                _deadline.Dispose();
            }
        }
    }
}
