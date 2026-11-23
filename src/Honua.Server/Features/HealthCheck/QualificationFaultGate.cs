// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

// Decides once per process which fault applies. The wedge needs the shared database to stay
// one-shot; without it, or when the marker cannot be read, the process serves normally.
internal sealed partial class QualificationFaultGate
{
    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(2);

    private readonly QualificationFault _fault;
    private readonly IQualificationWedgeMarker? _marker;
    private readonly ILogger<QualificationFaultGate> _logger;
    private readonly CancellationToken _applicationStopping;
    private readonly Lazy<Task<QualificationFaultKind>> _active;

    public QualificationFaultGate(
        QualificationWedgeDecision decision,
        ILogger<QualificationFaultGate> logger)
    {
        _fault = decision.Fault;
        _marker = decision.Marker;
        _logger = logger;
        _applicationStopping = decision.ApplicationStopping;
        _active = new Lazy<Task<QualificationFaultKind>>(ResolveAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsConfigured => _fault.Kind != QualificationFaultKind.None;

    public Task<QualificationFaultKind> GetActiveAsync(CancellationToken cancellationToken = default)
        => _active.Value.WaitAsync(cancellationToken);

    private async Task<QualificationFaultKind> ResolveAsync()
    {
        if (_fault.Kind != QualificationFaultKind.Wedged)
        {
            FaultActive(_logger, _fault.Kind);
            return _fault.Kind;
        }

        return await ResolveWedgeAsync().ConfigureAwait(false);
    }

    private async Task<QualificationFaultKind> ResolveWedgeAsync()
    {
        if (_marker is null)
        {
            WedgeNeedsDatabase(_logger);
            return QualificationFaultKind.None;
        }

        try
        {
            // Each request can cancel its wait independently of the shared startup decision.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
            deadline.CancelAfter(MarkerTimeout);
            deadline.Token.ThrowIfCancellationRequested();
            if (!await _marker.ShouldWedgeAsync(_fault.Token!, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false))
            {
                WedgeAlreadyConsumed(_logger);
                return QualificationFaultKind.None;
            }
        }
        catch (OperationCanceledException) when (_applicationStopping.IsCancellationRequested)
        {
            return QualificationFaultKind.None;
        }
        // A qualification fault must never take down a serving process: an unreadable marker means no wedge.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WedgeMarkerUnavailable(_logger, exception);
            return QualificationFaultKind.None;
        }

        FaultActive(_logger, QualificationFaultKind.Wedged);
        return QualificationFaultKind.Wedged;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Qualification fault {Kind} is active in this process")]
    private static partial void FaultActive(ILogger logger, QualificationFaultKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Qualification wedge was consumed by an earlier rollout; serving normally")]
    private static partial void WedgeAlreadyConsumed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Qualification wedge requires the shared database; serving normally")]
    private static partial void WedgeNeedsDatabase(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Qualification wedge marker could not be read; serving normally")]
    private static partial void WedgeMarkerUnavailable(ILogger logger, Exception exception);
}

internal sealed record QualificationWedgeDecision(
    QualificationFault Fault,
    IQualificationWedgeMarker? Marker,
    CancellationToken ApplicationStopping = default);
