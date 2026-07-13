// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace Honua.Core.Features.Federation.Services;

/// <summary>
/// Default <see cref="IFederatedQueryExecutor"/>. It plans the push-down with the offline
/// <see cref="IFederationQueryPlanner"/>, fetches candidate rows through the registered
/// <see cref="IFederatedSourceConnector"/> for the source kind, and refines/combines the rows
/// locally. Every remote fetch runs under a per-source timeout and a circuit breaker so a slow
/// or failing source fast-fails and cannot cascade into a total outage (issue #341).
/// </summary>
internal sealed class FederatedQueryExecutor : IFederatedQueryExecutor
{
    // PA-017: remote federated source calls had metrics and structured logging but no
    // distributed tracing span, so a slow/failing remote source was invisible in traces.
    private static readonly ActivitySource _activitySource = new("Honua.Federation");

    private readonly IFederationQueryPlanner _planner;
    private readonly Dictionary<FederatedSourceKind, IFederatedSourceConnector> _connectors;
    private readonly ResiliencePolicyOptions _resilienceOptions;
    private readonly FederationMetrics _metrics;
    private readonly ILogger<FederatedQueryExecutor> _logger;

    // Circuit-breaker state must persist across calls, so the per-source policy is cached.
    private readonly ConcurrentDictionary<string, IAsyncPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);

    public FederatedQueryExecutor(
        IFederationQueryPlanner planner,
        IEnumerable<IFederatedSourceConnector> connectors,
        ResiliencePolicyOptions resilienceOptions,
        ILogger<FederatedQueryExecutor> logger,
        FederationMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(resilienceOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _planner = planner;
        _resilienceOptions = resilienceOptions;
        _metrics = metrics ?? new FederationMetrics();
        _logger = logger;

        // Last writer wins when two connectors claim the same kind; this keeps registration
        // deterministic without throwing during container build.
        var map = new Dictionary<FederatedSourceKind, IFederatedSourceConnector>();
        foreach (var connector in connectors)
        {
            map[connector.Kind] = connector;
        }

        _connectors = map;
    }

    public async Task<FederatedQueryResult> ExecuteAsync(
        FederatedSourceDescriptor source,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var activity = _activitySource.StartActivity("federation.execute", ActivityKind.Client);
        activity?.SetTag("source.id", source.Id);
        activity?.SetTag("source.kind", source.Kind.ToString());

        if (!_connectors.TryGetValue(source.Kind, out var connector))
        {
            _metrics.RecordUnavailable(source, FederatedSourceUnavailableReason.NoConnector, TimeSpan.Zero);
            activity?.SetStatus(ActivityStatusCode.Error, "No connector registered for source kind.");
            throw new FederatedSourceUnavailableException(source.Id, FederatedSourceUnavailableReason.NoConnector);
        }

        var plan = _planner.Plan(source, in query, joinsLocalLayer: false);
        var request = new FederatedFetchRequest(source, query, plan);

        var stopwatch = Stopwatch.StartNew();
        ImmutableArray<Feature> candidates;
        try
        {
            candidates = await FetchWithResilienceAsync(source, connector, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FederatedSourceUnavailableException ex)
        {
            stopwatch.Stop();
            _metrics.RecordUnavailable(source, ex.Reason, stopwatch.Elapsed);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }

        stopwatch.Stop();

        var refinement = RefineLocally(query, plan, candidates);

        _metrics.RecordSuccess(source, stopwatch.Elapsed, refinement.Result.Items.Length);
        FederationExecutionLog.SourceExecuted(_logger, source.Id, stopwatch.Elapsed.TotalMilliseconds, refinement.Result.Items.Length);

        return new FederatedQueryResult
        {
            SourceId = source.Id,
            Plan = plan,
            Result = refinement.Result,
            Elapsed = stopwatch.Elapsed,
            UnappliedLocalRefinements = refinement.Unapplied,
        };
    }

    public async Task<FederatedCombinedResult> ExecuteAsync(
        ImmutableArray<FederatedSourceDescriptor> sources,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var enabledSources = sources.Where(source => source is not null && source.Enabled).ToArray();

        // Sources are independent of one another (each has its own connector, per-source
        // timeout, and circuit breaker via FetchWithResilienceAsync), so fetch them
        // concurrently instead of one at a time; a slow or broken source no longer delays
        // the others. Outcomes are folded back in original source order afterward so
        // feature concatenation order (when no OrderBy is requested) and failure ordering
        // stay identical to the previous sequential behavior.
        var outcomes = await Task.WhenAll(enabledSources.Select(async source =>
        {
            try
            {
                var result = await ExecuteAsync(source, query, cancellationToken).ConfigureAwait(false);
                return (Result: result, Failure: (FederatedSourceFailure?)null);
            }
            catch (FederatedSourceUnavailableException ex)
            {
                FederationExecutionLog.SourceUnavailable(_logger, source.Id, ex.Reason.ToString(), ex);
                return (Result: (FederatedQueryResult?)null, Failure: (FederatedSourceFailure?)new FederatedSourceFailure(source.Id, ex.Reason));
            }
        })).ConfigureAwait(false);

        var results = ImmutableArray.CreateBuilder<FederatedQueryResult>();
        var failures = ImmutableArray.CreateBuilder<FederatedSourceFailure>();
        foreach (var (result, failure) in outcomes)
        {
            if (result is not null)
            {
                results.Add(result);
            }
            else if (failure is { } sourceFailure)
            {
                failures.Add(sourceFailure);
            }
        }

        var combined = Combine(query, results);

        return new FederatedCombinedResult
        {
            Sources = results.ToImmutable(),
            Failures = failures.ToImmutable(),
            Combined = combined,
        };
    }

    private async Task<ImmutableArray<Feature>> FetchWithResilienceAsync(
        FederatedSourceDescriptor source,
        IFederatedSourceConnector connector,
        FederatedFetchRequest request,
        CancellationToken cancellationToken)
    {
        var policy = _policies.GetOrAdd(source.Id, BuildPolicy);

        try
        {
            return await policy.ExecuteAsync(
                async ct =>
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(source.RequestTimeout);

                    try
                    {
                        return await connector.FetchAsync(request, timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                    {
                        // The per-source timeout fired (not the caller). Surface a fault the
                        // resilience policy counts toward the circuit breaker.
                        throw new TimeoutException($"Federated source '{source.Id}' timed out after {source.RequestTimeout}.");
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            throw new FederatedSourceUnavailableException(source.Id, FederatedSourceUnavailableReason.CircuitOpen, ex);
        }
        catch (TimeoutException ex)
        {
            throw new FederatedSourceUnavailableException(source.Id, FederatedSourceUnavailableReason.TimedOut, ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional broad catch: this is the source-isolation boundary for federated
        // connectors. Any remaining connector/provider failure is translated into a typed
        // FederatedSourceUnavailableException (preserving the original as InnerException) so
        // one misbehaving source cannot surface a raw, connector-specific exception type to
        // callers or take down the overall federated query.
        catch (Exception ex)
        {
            throw new FederatedSourceUnavailableException(source.Id, FederatedSourceUnavailableReason.Faulted, ex);
        }
    }

    private IAsyncPolicy BuildPolicy(string sourceId) =>
        ResiliencePolicyFactory.CreateStandardPolicy(
            // Caller cancellation must propagate unchanged, so it is excluded from retry/break.
            Policy.Handle<Exception>(ex => ex is not OperationCanceledException),
            _resilienceOptions,
            onBreak: (_, breakDelay) => FederationExecutionLog.CircuitOpened(_logger, sourceId, breakDelay.TotalMilliseconds));

    private static LocalRefinementOutcome RefineLocally(
        in FeatureQuery query,
        FederationQueryPlan plan,
        ImmutableArray<Feature> candidates)
    {
        var features = candidates;
        var unapplied = ImmutableArray.CreateBuilder<FederationPredicateKind>();

        foreach (var decision in plan.Decisions)
        {
            if (decision.PushedDown)
            {
                continue;
            }

            switch (decision.Predicate)
            {
                case FederationPredicateKind.TemporalFilter when query.TemporalFilter is { } temporal:
                    features = FederationLocalRefinement.ApplyTemporalFilter(features, in temporal);
                    break;

                case FederationPredicateKind.OrderBy when query.OrderBy is { IsDefaultOrEmpty: false } orderBy:
                    features = FederationLocalRefinement.ApplyOrderBy(features, orderBy);
                    break;

                case FederationPredicateKind.Paging:
                    // Paging is applied after ordering/temporal below so it pages the refined set.
                    break;

                default:
                    // Attribute and exact spatial-relationship refinement are not implemented in
                    // this slice; record them so callers know the rows are an unrefined superset.
                    unapplied.Add(decision.Predicate);
                    break;
            }
        }

        var totalBeforePaging = features.Length;
        var pagedLocally = plan.Decisions.Any(static d => d.Predicate == FederationPredicateKind.Paging && !d.PushedDown);

        QueryResult<Feature> result;
        if (pagedLocally)
        {
            var skip = query.Offset is > 0 ? query.Offset.Value : 0;
            var page = FederationLocalRefinement.ApplyPaging(features, query.Offset, query.Limit);
            result = new QueryResult<Feature>
            {
                TotalCount = totalBeforePaging,
                Items = page,
                HasMoreResults = skip + page.Length < totalBeforePaging,
            };
        }
        else
        {
            result = new QueryResult<Feature>
            {
                TotalCount = totalBeforePaging,
                Items = features,
                HasMoreResults = false,
            };
        }

        return new LocalRefinementOutcome(result, unapplied.ToImmutable());
    }

    private static QueryResult<Feature> Combine(in FeatureQuery query, ImmutableArray<FederatedQueryResult>.Builder results)
    {
        if (results.Count == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        if (results.Count == 1)
        {
            return results[0].Result;
        }

        var merged = ImmutableArray.CreateBuilder<Feature>();
        long total = 0;
        foreach (var sourceResult in results)
        {
            merged.AddRange(sourceResult.Result.Items);
            total += sourceResult.Result.TotalCount;
        }

        var features = merged.ToImmutable();

        // Per-source ordering does not yield a global order, so re-apply it across the union.
        if (query.OrderBy is { IsDefaultOrEmpty: false } orderBy)
        {
            features = FederationLocalRefinement.ApplyOrderBy(features, orderBy);
        }

        // Cross-source paging is best-effort over the merged candidate rows in this slice.
        if (query.Offset is not null || query.Limit is not null)
        {
            var skip = query.Offset is > 0 ? query.Offset.Value : 0;
            var page = FederationLocalRefinement.ApplyPaging(features, query.Offset, query.Limit);
            return new QueryResult<Feature>
            {
                TotalCount = total,
                Items = page,
                HasMoreResults = skip + page.Length < features.Length,
            };
        }

        return new QueryResult<Feature>
        {
            TotalCount = total,
            Items = features,
            HasMoreResults = false,
        };
    }

    private readonly record struct LocalRefinementOutcome(
        QueryResult<Feature> Result,
        ImmutableArray<FederationPredicateKind> Unapplied);
}
