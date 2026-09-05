// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Infrastructure.Licensing;

internal sealed partial class FileBackedLicenseService
{
    internal static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(1);
    private readonly object _runtimeLock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _expiryTimer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<CancellationTokenSource> _retiredCancellations = [];
    private CancellationTokenSource _operationCancellation = new();
    private Task? _revalidationTask;
    private bool _suppressExpiryWarnings;
    private bool _disposed;
    private readonly HashSet<(string? LicenseId, DateTimeOffset? Expiry, int Days)> _warnings = [];

    public bool IsBlocked => GetSnapshot() is { Edition: > HonuaEdition.Community, IsValid: false };

    public CancellationToken OperationCancellation
    {
        get
        {
            lock (_runtimeLock)
            {
                if (GetSnapshot().Edition == HonuaEdition.Community)
                {
                    return CancellationToken.None;
                }
                return _operationCancellation.Token;
            }
        }
    }

    private void EnsureStartupLicense()
    {
        var options = _options.Value;
        if (options.Edition is { } declared && !Enum.IsDefined(declared))
        {
            throw new InvalidOperationException("Licensing:Edition must be Community, Pro or Enterprise.");
        }
        if (options.Edition == HonuaEdition.Community)
        {
            return;
        }
        var snapshot = GetSnapshot();
        var paid = options.Edition > HonuaEdition.Community || snapshot.Edition > HonuaEdition.Community ||
            !string.IsNullOrWhiteSpace(options.LicensePath) ||
            !string.IsNullOrWhiteSpace(options.LicenseContent) ||
            !string.IsNullOrWhiteSpace(options.LicenseContentSecretRef);
        if (!paid || (snapshot.IsValid && snapshot.ValidationState == LicenseValidationState.Valid))
        {
            return;
        }
        var edition = options.Edition ?? (snapshot.Edition > HonuaEdition.Community ? snapshot.Edition : HonuaEdition.Pro);
        var state = snapshot.ValidationState switch
        {
            LicenseValidationState.NoLicenseConfigured or LicenseValidationState.MissingFile => "missing",
            LicenseValidationState.Expired => "expired",
            _ => "invalid"
        };
        throw new InvalidOperationException(
            $"Honua {edition} startup refused: license {state}. Install a valid {edition} license in the configured licensing source and restart. Community fallback is disabled.");
    }

    internal async Task RevalidateAsync(CancellationToken cancellationToken)
    {
        // Share the persistence owner's serialization: a reload cannot publish a stale source
        // over a concurrently committed uploaded renewal.
        await _uploadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GetSnapshot();
            await LoadConfiguredLicenseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    private async Task RunRevalidationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(RevalidationInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RevalidateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A source timeout must not permanently stop renewal polling. The independent
                    // expiry timer still revokes the last verified license at its signed deadline.
                    LicenseRuntimeLog.RevalidationFailed(_logger, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Hosted-service shutdown.
        }
    }

    private void CancelOperations()
    {
        try
        {
            _operationCancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation invokes every callback before throwing. Keep the timer/host alive
            // so workers can persist their failures even if a plugin callback misbehaves.
            LicenseRuntimeLog.CancellationCallbackFailed(_logger);
        }
    }

    private void LogExpiryWarning(LicenseSnapshot snapshot)
    {
        if (_suppressExpiryWarnings || snapshot.Edition == HonuaEdition.Community || !snapshot.IsValid || snapshot.ExpiresAt is null)
        {
            return;
        }
        var remaining = (snapshot.ExpiresAt.Value - _timeProvider.GetUtcNow()).TotalDays;
        foreach (var days in new[] { 30, 14, 7, 1 })
        {
            if (remaining > 0 && remaining <= days && _warnings.Add((snapshot.LicenseId, snapshot.ExpiresAt, days)))
            {
                LicenseRuntimeLog.ExpiryWarning(_logger, days, snapshot.ExpiresAt.Value);
            }
        }
    }
}
