// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Process-lifetime record of when each findings store source last returned valid observations.
/// The findings engine is scoped, so without this a failed read in a fresh scope could not report
/// the last successful collection; with it an outage keeps publishing that instant instead of
/// refreshing it (#4840, same retention semantics as the alert-dispatch source #4375). The record
/// is replica-local, like the dispatcher heartbeat it mirrors.
/// </summary>
internal sealed class OpsFindingsCollectionLedger
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccessfulAt = new(StringComparer.Ordinal);

    /// <summary>Gets the last successful collection time for a source, or null when none succeeded.</summary>
    /// <param name="sourceId">The stable evidence source id.</param>
    /// <returns>The UTC collection time, or null.</returns>
    public DateTimeOffset? GetLastSuccessfulAt(string sourceId)
        => _lastSuccessfulAt.TryGetValue(sourceId, out var at) ? at : null;

    /// <summary>Records a successful collection; an older concurrent result never moves the clock back.</summary>
    /// <param name="sourceId">The stable evidence source id.</param>
    /// <param name="observedAt">The UTC time the collection returned.</param>
    public void RecordSuccess(string sourceId, DateTimeOffset observedAt)
        => _lastSuccessfulAt.AddOrUpdate(
            sourceId,
            observedAt,
            (_, current) => observedAt > current ? observedAt : current);
}

/// <summary>Outcome of one store read performed through <see cref="OpsFindingsStoreCollection"/>.</summary>
/// <typeparam name="T">The read's result type.</typeparam>
/// <param name="Succeeded">True when the store answered; a null <paramref name="Value"/> is then a real answer.</param>
/// <param name="Value">The store's answer, or default when the read failed or was skipped.</param>
internal readonly record struct OpsFindingsStoreRead<T>(bool Succeeded, T? Value);

/// <summary>
/// Tracks the reads one findings evaluation performs against one durable store, so the source's
/// posture is derived from what the store actually returned rather than from whether the store is
/// registered. Every intended read is an expected coverage component. After the first failure the
/// remaining reads are skipped (a failing backend is not polled once per target), and stay expected
/// but not included.
/// </summary>
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

    /// <summary>Performs one read of the store as a named coverage component.</summary>
    /// <typeparam name="T">The read's result type.</typeparam>
    /// <param name="componentId">Stable, secret-free id of what is being read.</param>
    /// <param name="read">The store call.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the store answered, and its answer.</returns>
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

    /// <summary>
    /// Records a coverage component the pass deliberately left unread, such as the remainder of a
    /// bounded paged read, so the source publishes <c>partial</c> rather than <c>complete</c>.
    /// </summary>
    /// <param name="componentId">Stable, secret-free id of what was not read.</param>
    public void ExpectUncollected(string componentId) => _expected.Add(componentId);

    /// <summary>
    /// Publishes the source envelope for this pass and advances the ledger only when the store
    /// returned valid observations. Every read succeeded: <c>complete</c>. Some reads succeeded
    /// before a later one failed: <c>partial</c>, with the missing components in coverage. No read
    /// succeeded (or none was attempted): <c>unavailable</c>, carrying the retained last successful
    /// collection as both clocks, never the evaluation time.
    /// </summary>
    /// <param name="ledger">The process-lifetime collection ledger.</param>
    /// <param name="maximumAge">The server-owned validity window.</param>
    /// <returns>The unvalidated source envelope.</returns>
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
