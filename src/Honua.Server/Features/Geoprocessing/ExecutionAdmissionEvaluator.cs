// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Default implementation of <see cref="IExecutionAdmissionEvaluator"/>.
/// Gates admission on backpressure, concurrency, rate, and cost controls.
/// Evaluation order short-circuits on the first failing dimension. Backpressure
/// is checked first to avoid unnecessary partition-scoped work when the system
/// is globally overloaded.
/// </summary>
internal sealed class ExecutionAdmissionEvaluator : IExecutionAdmissionEvaluator
{
    /// <summary>
    /// Key used in <see cref="ExecutionJobSpec.Parameters"/> to persist the cost
    /// weight at admission time. Observed by the evaluator when summing
    /// active cost weight for subsequent requests.
    /// </summary>
    public const string CostWeightParameterKey = "admission.costWeight";

    /// <summary>
    /// Key used in <see cref="ExecutionJobSpec.Parameters"/> to persist the
    /// partition key at admission time so active-cost summation stays scoped.
    /// </summary>
    public const string PartitionKeyParameterKey = "admission.partitionKey";

    private const string GlobalPartitionSentinel = "__global__";
    private const string AnonymousPrincipalSentinel = "__anonymous__";

    private readonly IExecutionJobStore? _jobStore;
    private readonly IOptionsMonitor<ExecutionAdmissionOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExecutionAdmissionEvaluator> _logger;

    private readonly ConcurrentDictionary<string, RateBucket> _rateBuckets = new();

    private readonly Counter<long> _evaluatedCounter;

    public ExecutionAdmissionEvaluator(
        IOptionsMonitor<ExecutionAdmissionOptions> options,
        TimeProvider timeProvider,
        ILogger<ExecutionAdmissionEvaluator> logger,
        IExecutionJobStore? jobStore = null)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _jobStore = jobStore;

        _evaluatedCounter = HonuaTelemetry.Meter.CreateCounter<long>(
            "execution.admission.evaluated",
            unit: "{evaluation}",
            description: "Count of admission evaluations tagged with outcome and dimension.");
    }

    public async Task<ExecutionAdmissionDecision> EvaluateAsync(
        ExecutionAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.CurrentValue;

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.ExecutionAdmission, ActivityKind.Internal);
        var partitionTag = request.PartitionKey ?? GlobalPartitionSentinel;
        var principalTag = request.PrincipalId ?? AnonymousPrincipalSentinel;
        activity?.SetTag("honua.admission.job_kind", request.JobKind.ToString());
        activity?.SetTag("honua.admission.partition_key", partitionTag);
        activity?.SetTag("honua.admission.principal_id", principalTag);

        if (!options.Enabled)
        {
            var emptySnapshot = new ExecutionAdmissionSnapshot();
            RecordActivity(activity, ExecutionAdmissionOutcome.Admitted, null);
            RecordMetric(ExecutionAdmissionOutcome.Admitted, dimension: null, request.JobKind);
            ExecutionAdmissionLog.Admitted(_logger, request.JobKind.ToString(), partitionTag, principalTag, 0, 0);
            return ExecutionAdmissionDecision.Admitted(emptySnapshot);
        }

        var activeGlobal = 0;
        var activeInPartition = 0;
        var activeCostWeightInPartition = 0.0;

        if (_jobStore != null)
        {
            var active = await _jobStore.ListActiveAsync(kind: null, cancellationToken)
                .ConfigureAwait(false);
            activeGlobal = active.Count;

            foreach (var job in active)
            {
                if (job.Spec.Kind != request.JobKind)
                {
                    continue;
                }

                if (!MatchesPartition(job, request.PartitionKey))
                {
                    continue;
                }

                activeInPartition++;
                activeCostWeightInPartition += ReadCostWeight(job);
            }
        }
        else
        {
            ExecutionAdmissionLog.JobStoreUnavailable(_logger);
        }

        var submissionsInWindow = PeekSubmissions(request, options);

        var snapshot = new ExecutionAdmissionSnapshot
        {
            ActiveJobsInPartition = activeInPartition,
            ActiveJobsGlobal = activeGlobal,
            SubmissionsInWindow = submissionsInWindow,
            ActiveCostWeightInPartition = activeCostWeightInPartition
        };

        ExecutionAdmissionLog.Snapshot(
            _logger, request.JobKind.ToString(), partitionTag,
            activeInPartition, activeGlobal, submissionsInWindow, activeCostWeightInPartition);

        // 1. Backpressure — cheapest short-circuit when the system is saturated.
        if (options.MaxConcurrentJobsGlobal > 0 && activeGlobal >= options.MaxConcurrentJobsGlobal)
        {
            return Deny(
                ExecutionAdmissionDimension.Backpressure,
                $"backpressure:global:{request.JobKind.ToString().ToLowerInvariant()}",
                $"Global active job limit reached ({activeGlobal}/{options.MaxConcurrentJobsGlobal}).",
                options.DefaultRetryAfterSeconds,
                snapshot, request, activity, partitionTag, principalTag);
        }

        // 2. Concurrency — per-partition active jobs for this kind.
        if (options.MaxConcurrentJobsPerPartition > 0 && activeInPartition >= options.MaxConcurrentJobsPerPartition)
        {
            return Deny(
                ExecutionAdmissionDimension.Concurrency,
                $"concurrency:{request.JobKind.ToString().ToLowerInvariant()}:per-partition",
                $"Partition active job limit reached ({activeInPartition}/{options.MaxConcurrentJobsPerPartition}).",
                options.DefaultRetryAfterSeconds,
                snapshot, request, activity, partitionTag, principalTag);
        }

        // 3. Rate — per-principal sliding-window submissions.
        // Claim a slot so concurrent requests from the same principal cannot both pass.
        if (options.MaxSubmissionsPerWindow > 0 && !string.IsNullOrWhiteSpace(request.PrincipalId))
        {
            var claimed = TryClaimSubmission(request, options, out var postClaimCount);
            if (!claimed)
            {
                var rateSnapshot = snapshot with { SubmissionsInWindow = postClaimCount };
                return Throttle(
                    ExecutionAdmissionDimension.Rate,
                    $"rate:{request.JobKind.ToString().ToLowerInvariant()}:per-principal",
                    $"Principal submission rate limit reached ({postClaimCount}/{options.MaxSubmissionsPerWindow} in {options.RateWindowSeconds}s).",
                    options.DefaultRetryAfterSeconds,
                    rateSnapshot, request, activity, partitionTag, principalTag);
            }

            snapshot = snapshot with { SubmissionsInWindow = postClaimCount };
        }

        // 4. Cost — aggregate cost weight within the partition.
        if (options.MaxCostWeightPerPartition > 0)
        {
            var requestCost = ResolveCostWeight(request);
            var projectedCost = activeCostWeightInPartition + requestCost;
            if (projectedCost > options.MaxCostWeightPerPartition)
            {
                return Deny(
                    ExecutionAdmissionDimension.Cost,
                    $"cost:{request.JobKind.ToString().ToLowerInvariant()}:per-partition",
                    $"Partition cost weight would exceed limit ({projectedCost:F2}/{options.MaxCostWeightPerPartition:F2}).",
                    options.DefaultRetryAfterSeconds,
                    snapshot, request, activity, partitionTag, principalTag);
            }
        }

        RecordActivity(activity, ExecutionAdmissionOutcome.Admitted, null);
        RecordMetric(ExecutionAdmissionOutcome.Admitted, dimension: null, request.JobKind);
        ExecutionAdmissionLog.Admitted(
            _logger, request.JobKind.ToString(), partitionTag, principalTag, activeInPartition, activeGlobal);

        return ExecutionAdmissionDecision.Admitted(snapshot);
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private ExecutionAdmissionDecision Deny(
        ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfter,
        ExecutionAdmissionSnapshot snapshot,
        ExecutionAdmissionRequest request,
        Activity? activity,
        string partitionTag,
        string principalTag)
    {
        RecordActivity(activity, ExecutionAdmissionOutcome.Denied, dimension);
        RecordMetric(ExecutionAdmissionOutcome.Denied, dimension, request.JobKind);
        ExecutionAdmissionLog.Denied(
            _logger, dimension.ToString(), policyRef, request.JobKind.ToString(), partitionTag, principalTag, reason);
        return ExecutionAdmissionDecision.Denied(dimension, policyRef, reason, retryAfter, snapshot);
    }

    private ExecutionAdmissionDecision Throttle(
        ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfter,
        ExecutionAdmissionSnapshot snapshot,
        ExecutionAdmissionRequest request,
        Activity? activity,
        string partitionTag,
        string principalTag)
    {
        RecordActivity(activity, ExecutionAdmissionOutcome.Throttled, dimension);
        RecordMetric(ExecutionAdmissionOutcome.Throttled, dimension, request.JobKind);
        ExecutionAdmissionLog.Throttled(
            _logger, dimension.ToString(), policyRef, request.JobKind.ToString(), partitionTag, principalTag, reason);
        return ExecutionAdmissionDecision.Throttled(dimension, policyRef, reason, retryAfter, snapshot);
    }

    private static void RecordActivity(
        Activity? activity, ExecutionAdmissionOutcome outcome, ExecutionAdmissionDimension? dimension)
    {
        if (activity == null)
        {
            return;
        }

        activity.SetTag("honua.admission.outcome", outcome.ToString());
        if (dimension.HasValue)
        {
            activity.SetTag("honua.admission.dimension", dimension.Value.ToString());
            activity.SetStatus(ActivityStatusCode.Error, outcome.ToString());
        }
    }

    private void RecordMetric(
        ExecutionAdmissionOutcome outcome, ExecutionAdmissionDimension? dimension, ExecutionJobKind jobKind)
    {
        _evaluatedCounter.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            new KeyValuePair<string, object?>("dimension", dimension?.ToString() ?? "None"),
            new KeyValuePair<string, object?>("job_kind", jobKind.ToString()));
    }

    private static bool MatchesPartition(ExecutionJobRecord job, string? partitionKey)
    {
        var jobPartition = ReadPartitionKey(job);
        if (partitionKey == null)
        {
            return jobPartition == null;
        }

        return string.Equals(jobPartition, partitionKey, StringComparison.Ordinal);
    }

    private static string? ReadPartitionKey(ExecutionJobRecord job)
    {
        if (job.Spec.Parameters.TryGetValue(PartitionKeyParameterKey, out var value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        return null;
    }

    private static double ReadCostWeight(ExecutionJobRecord job)
    {
        if (job.Spec.Parameters.TryGetValue(CostWeightParameterKey, out var value)
            && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return 1.0; // Assume a standard-cost job when unlabeled.
    }

    private static double ResolveCostWeight(ExecutionAdmissionRequest request)
    {
        if (request.EstimatedCostWeight is { } weight && weight > 0)
        {
            return weight;
        }

        return 1.0;
    }

    private int PeekSubmissions(ExecutionAdmissionRequest request, ExecutionAdmissionOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.PrincipalId) || options.MaxSubmissionsPerWindow <= 0)
        {
            return 0;
        }

        var key = BuildRateKey(request);
        if (!_rateBuckets.TryGetValue(key, out var bucket))
        {
            return 0;
        }

        return bucket.Count(_timeProvider.GetUtcNow(), TimeSpan.FromSeconds(options.RateWindowSeconds));
    }

    private bool TryClaimSubmission(
        ExecutionAdmissionRequest request, ExecutionAdmissionOptions options, out int count)
    {
        var key = BuildRateKey(request);
        var bucket = _rateBuckets.GetOrAdd(key, static _ => new RateBucket());
        return bucket.TryClaim(
            _timeProvider.GetUtcNow(),
            TimeSpan.FromSeconds(options.RateWindowSeconds),
            options.MaxSubmissionsPerWindow,
            out count);
    }

    private static string BuildRateKey(ExecutionAdmissionRequest request)
        => $"{request.PrincipalId ?? AnonymousPrincipalSentinel}:{request.JobKind}";

    /// <summary>
    /// Sliding-window counter. Tracks timestamps of recent submissions and admits
    /// up to <c>limit</c> claims within the window. Distributed coordination is a
    /// follow-on; see <see cref="ExecutionAdmissionOptions"/> default rationale.
    /// </summary>
    private sealed class RateBucket
    {
        private readonly object _sync = new();
        private readonly Queue<DateTimeOffset> _timestamps = new();

        public int Count(DateTimeOffset now, TimeSpan window)
        {
            lock (_sync)
            {
                Prune(now, window);
                return _timestamps.Count;
            }
        }

        public bool TryClaim(DateTimeOffset now, TimeSpan window, int limit, out int count)
        {
            lock (_sync)
            {
                Prune(now, window);
                if (_timestamps.Count >= limit)
                {
                    count = _timestamps.Count;
                    return false;
                }

                _timestamps.Enqueue(now);
                count = _timestamps.Count;
                return true;
            }
        }

        private void Prune(DateTimeOffset now, TimeSpan window)
        {
            var threshold = now - window;
            while (_timestamps.Count > 0 && _timestamps.Peek() <= threshold)
            {
                _timestamps.Dequeue();
            }
        }
    }
}
