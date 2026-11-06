// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Monitoring;

internal sealed class OpsFindingsCollectionLedger
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccessfulAt = new(StringComparer.Ordinal);

    public DateTimeOffset? GetLastSuccessfulAt(string sourceId)
        => _lastSuccessfulAt.TryGetValue(sourceId, out var at) ? at : null;

    // An older concurrent result never moves the collection clock backward.
    public void RecordSuccess(string sourceId, DateTimeOffset observedAt)
        => _lastSuccessfulAt.AddOrUpdate(
            sourceId,
            observedAt,
            (_, current) => observedAt > current ? observedAt : current);
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
    private bool _failed;

    public async Task<OpsFindingsStoreRead<T>> ReadAsync<T>(
        string componentId,
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        _expected.Add(componentId);
        if (_failed)
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
            _failed = true;
            return default;
        }
    }

    // A read the pass left unread is published as partial, not complete.
    public void ExpectUncollected(string componentId) => _expected.Add(componentId);

    // When no read succeeded the clocks stay at the last successful collection, never the evaluation time.
    public EvidenceSourceEnvelope Publish(OpsFindingsCollectionLedger ledger, TimeSpan maximumAge)
    {
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
