// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsFindingsCollectionLedger(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private static readonly TimeSpan RepeatedFailureLogInterval = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccessfulAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string FailureClass, DateTimeOffset LoggedAt)> _lastLoggedFailure = new(StringComparer.Ordinal);

    public DateTimeOffset? GetLastSuccessfulAt(string sourceId)
        => _lastSuccessfulAt.TryGetValue(sourceId, out var at) ? at : null;

    // An older concurrent result never moves the collection clock backward.
    public void RecordSuccess(string sourceId, DateTimeOffset observedAt)
        => _lastSuccessfulAt.AddOrUpdate(
            sourceId,
            observedAt,
            (_, current) => observedAt > current ? observedAt : current);

    // A collection with no failed read re-arms the failure log for its source.
    public void RecordNoFailure(string sourceId) => _lastLoggedFailure.TryRemove(sourceId, out _);

    // Exception text can carry connection details, so only the failure class is logged. Findings are
    // evaluated on every admin read and sample, so an unchanged failure is logged at most every five minutes.
    public void RecordFailure(string sourceId, OpsFindingsReadFailure failure, DateTimeOffset now)
    {
        var key = failure.ComponentId + ": " + failure.FailureClass;
        if (_lastLoggedFailure.TryGetValue(sourceId, out var last)
            && last.FailureClass == key && now - last.LoggedAt < RepeatedFailureLogInterval)
        {
            return;
        }
        _lastLoggedFailure[sourceId] = (key, now);
        ReadFailed(_logger, sourceId, failure.ComponentId, failure.FailureClass);
    }

    [LoggerMessage(EventId = 7463, Level = LogLevel.Warning,
        Message = "Ops findings source {SourceId} could not read {ComponentId}: {FailureClass}.")]
    private static partial void ReadFailed(ILogger logger, string sourceId, string componentId, string failureClass);
}

internal sealed record OpsFindingsReadFailure(string ComponentId, string FailureClass)
{
    public static OpsFindingsReadFailure From(string componentId, Exception exception)
        => new(componentId, DescribeFailure(exception));

    internal static string DescribeFailure(Exception exception)
    {
        var description = exception switch
        {
            HttpRequestException { StatusCode: { } status } => $"{exception.GetType().Name} status {(int)status}",
            HttpRequestException http => $"{exception.GetType().Name} {http.HttpRequestError}",
            _ => exception.GetType().Name
        };
        var cause = exception.GetBaseException();
        return cause switch
        {
            _ when ReferenceEquals(cause, exception) => description,
            SocketException socket => $"{description} ({nameof(SocketException)} {socket.SocketErrorCode})",
            _ => $"{description} ({cause.GetType().Name})"
        };
    }
}

internal readonly record struct OpsFindingsStoreRead<T>(bool Succeeded, T? Value);

internal sealed class OpsFindingsStoreCollection(
    string sourceId,
    string backendKind,
    string backendId,
    TimeProvider clock)
{
    private readonly List<string> _expected = [];
    private readonly List<string> _included = [];
    private DateTimeOffset? _observedAt;
    private OpsFindingsReadFailure? _failure;

    public async Task<OpsFindingsStoreRead<T>> ReadAsync<T>(
        string componentId,
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        _expected.Add(componentId);
        if (_failure is not null)
        {
            return default;
        }

        try
        {
            var value = await read(cancellationToken).ConfigureAwait(false);
            _included.Add(componentId);
            // The source is only as fresh as its earliest successful read in this pass.
            _observedAt ??= clock.GetUtcNow();
            return new OpsFindingsStoreRead<T>(true, value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failure = OpsFindingsReadFailure.From(componentId, ex);
            return default;
        }
    }

    private void RecordReadOutcome(OpsFindingsCollectionLedger ledger)
    {
        if (_failure is null)
        {
            ledger.RecordNoFailure(sourceId);
            return;
        }
        ledger.RecordFailure(sourceId, _failure, clock.GetUtcNow());
    }

    // A read the pass left unread is published as partial, not complete.
    public void ExpectUncollected(string componentId) => _expected.Add(componentId);

    // When no read succeeded the clocks stay at the last successful collection, never the evaluation time.
    public EvidenceSourceEnvelope Publish(OpsFindingsCollectionLedger ledger, TimeSpan maximumAge)
    {
        RecordReadOutcome(ledger);
        if (_observedAt is { } observedAt)
        {
            ledger.RecordSuccess(sourceId, observedAt);
            var expected = _expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var included = _included.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return included.Length == expected.Length
                ? EvidencePostureFactory.Complete(sourceId, backendKind, backendId, observedAt, maximumAge)
                : EvidencePostureFactory.Partial(
                    sourceId,
                    backendKind,
                    backendId,
                    observedAt,
                    maximumAge,
                    EvidencePostureVocabulary.ReasonCodes.PartialResult,
                    new EvidenceSourceCoverage
                    {
                        IncludedComponentIds = included,
                        ExpectedComponentIds = expected,
                    });
        }

        var lastSuccessfulAt = ledger.GetLastSuccessfulAt(sourceId);
        return EvidencePostureFactory.Unavailable(
            sourceId,
            backendKind,
            backendId,
            EvidencePostureVocabulary.ReasonCodes.SourceUnavailable,
            observedAt: lastSuccessfulAt,
            lastSuccessfulAt: lastSuccessfulAt,
            maximumAge: maximumAge);
    }
}
