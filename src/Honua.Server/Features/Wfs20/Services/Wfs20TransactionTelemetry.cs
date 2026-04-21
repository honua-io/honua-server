// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Enhanced telemetry and observability for WFS 2.0 transaction operations.
/// Provides detailed performance metrics and operational insights.
/// </summary>
internal sealed class Wfs20TransactionTelemetry : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("Honua.Wfs20.Transactions");
    private static readonly Meter Meter = new("Honua.Wfs20.Transactions", "1.0.0");

    // Counters
    private static readonly Counter<long> TransactionsProcessed = Meter.CreateCounter<long>(
        "wfs20_transactions_total",
        description: "Total number of WFS 2.0 transactions processed");

    private static readonly Counter<long> OperationsExecuted = Meter.CreateCounter<long>(
        "wfs20_operations_total",
        description: "Total number of WFS 2.0 operations executed");

    private static readonly Counter<long> TransactionErrors = Meter.CreateCounter<long>(
        "wfs20_transaction_errors_total",
        description: "Total number of WFS 2.0 transaction errors");

    // Histograms
    private static readonly Histogram<double> TransactionDuration = Meter.CreateHistogram<double>(
        "wfs20_transaction_duration_seconds",
        unit: "s",
        description: "Duration of WFS 2.0 transaction processing");

    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "wfs20_operation_duration_seconds",
        unit: "s",
        description: "Duration of individual WFS 2.0 operations");

    private static readonly Histogram<long> TransactionSize = Meter.CreateHistogram<long>(
        "wfs20_transaction_size_operations",
        unit: "operations",
        description: "Number of operations per WFS 2.0 transaction");

    private static int _activeTransactionCount;
    private readonly ILogger<Wfs20TransactionTelemetry> _logger;

    public Wfs20TransactionTelemetry(ILogger<Wfs20TransactionTelemetry> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Register gauge callback
        Meter.CreateObservableGauge("wfs20_active_transactions", () => _activeTransactionCount,
            description: "Current number of active WFS 2.0 transactions");
    }

    /// <summary>
    /// Creates a telemetry scope for tracking a complete WFS 2.0 transaction.
    /// </summary>
    /// <param name="transactionId">Unique transaction identifier</param>
    /// <param name="operationCount">Number of operations in the transaction</param>
    /// <returns>Disposable telemetry scope</returns>
    public TransactionTelemetryScope StartTransaction(string transactionId, int operationCount)
    {
        return new TransactionTelemetryScope(transactionId, operationCount, _logger);
    }

    /// <summary>
    /// Records telemetry for a completed WFS 2.0 transaction.
    /// </summary>
    /// <param name="response">Transaction response containing results</param>
    /// <param name="duration">Total transaction duration</param>
    public static void RecordTransaction(Wfs20TransactionResponse response, TimeSpan duration)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("transaction_id", response.TransactionId),
            new("success", response.Success.ToString().ToLowerInvariant()),
            new("inserted", response.TransactionSummary.TotalInserted.ToString()),
            new("updated", response.TransactionSummary.TotalUpdated.ToString()),
            new("deleted", response.TransactionSummary.TotalDeleted.ToString())
        };

        TransactionsProcessed.Add(1, tags);
        TransactionDuration.Record(duration.TotalSeconds, tags);

        var totalOperations = response.TransactionSummary.TotalInserted +
                             response.TransactionSummary.TotalUpdated +
                             response.TransactionSummary.TotalDeleted;

        TransactionSize.Record(totalOperations, tags);

        if (!response.Success)
        {
            TransactionErrors.Add(1, tags);
        }
    }

    /// <summary>
    /// Records telemetry for an individual operation within a transaction.
    /// </summary>
    /// <param name="operation">Operation result</param>
    /// <param name="duration">Operation duration</param>
    public static void RecordOperation(Wfs20OperationResult operation, TimeSpan duration)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("operation_type", operation.OperationType.ToString().ToLowerInvariant()),
            new("feature_type", operation.FeatureTypeName),
            new("success", operation.Success.ToString().ToLowerInvariant())
        };

        OperationsExecuted.Add(1, tags);
        OperationDuration.Record(duration.TotalSeconds, tags);
    }

    public void Dispose()
    {
        ActivitySource?.Dispose();
        Meter?.Dispose();
    }

    /// <summary>
    /// Telemetry scope for tracking the lifecycle of a WFS 2.0 transaction.
    /// </summary>
    public readonly struct TransactionTelemetryScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly Stopwatch _stopwatch;
        private readonly string _transactionId;
        private readonly int _operationCount;
        private readonly ILogger _logger;

        internal TransactionTelemetryScope(string transactionId, int operationCount, ILogger logger)
        {
            _transactionId = transactionId;
            _operationCount = operationCount;
            _logger = logger;
            _stopwatch = Stopwatch.StartNew();

            Interlocked.Increment(ref _activeTransactionCount);

            _activity = ActivitySource.StartActivity("wfs20.transaction");
            _activity?.SetTag("transaction.id", transactionId);
            _activity?.SetTag("transaction.operation_count", operationCount);

            Wfs20TransactionLog.TransactionStarted(logger, transactionId, operationCount);
        }

        /// <summary>
        /// Creates a nested scope for tracking an individual operation.
        /// </summary>
        /// <param name="operationType">Type of operation being executed</param>
        /// <param name="featureTypeName">Name of the feature type</param>
        /// <returns>Disposable operation telemetry scope</returns>
        public OperationTelemetryScope StartOperation(Wfs20OperationType operationType, string featureTypeName)
        {
            return new OperationTelemetryScope(operationType, featureTypeName, _logger);
        }

        /// <summary>
        /// Completes the transaction scope with the final result.
        /// </summary>
        /// <param name="response">Transaction response</param>
        public void Complete(Wfs20TransactionResponse response)
        {
            _stopwatch.Stop();

            _activity?.SetTag("transaction.success", response.Success);
            _activity?.SetTag("transaction.inserted", response.TransactionSummary.TotalInserted);
            _activity?.SetTag("transaction.updated", response.TransactionSummary.TotalUpdated);
            _activity?.SetTag("transaction.deleted", response.TransactionSummary.TotalDeleted);

            if (!response.Success)
            {
                _activity?.SetStatus(ActivityStatusCode.Error, "Transaction failed");
            }

            RecordTransaction(response, _stopwatch.Elapsed);

            Wfs20TransactionLog.TransactionCompleted(
                _logger,
                _transactionId,
                response.Success,
                response.TransactionSummary.TotalInserted,
                response.TransactionSummary.TotalUpdated,
                response.TransactionSummary.TotalDeleted,
                _stopwatch.Elapsed.TotalMilliseconds);
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref _activeTransactionCount);
            _activity?.Dispose();
        }
    }

    /// <summary>
    /// Telemetry scope for tracking individual operations within a transaction.
    /// </summary>
    public readonly struct OperationTelemetryScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly Stopwatch _stopwatch;
        private readonly Wfs20OperationType _operationType;
        private readonly string _featureTypeName;
        private readonly ILogger _logger;

        internal OperationTelemetryScope(Wfs20OperationType operationType, string featureTypeName, ILogger logger)
        {
            _operationType = operationType;
            _featureTypeName = featureTypeName;
            _logger = logger;
            _stopwatch = Stopwatch.StartNew();

            _activity = ActivitySource.StartActivity($"wfs20.operation.{operationType.ToString().ToLowerInvariant()}");
            _activity?.SetTag("operation.type", operationType.ToString());
            _activity?.SetTag("operation.feature_type", featureTypeName);
        }

        /// <summary>
        /// Completes the operation scope with the final result.
        /// </summary>
        /// <param name="result">Operation result</param>
        public void Complete(Wfs20OperationResult result)
        {
            _stopwatch.Stop();

            _activity?.SetTag("operation.success", result.Success);
            _activity?.SetTag("operation.feature_id", result.FeatureId);

            if (!result.Success)
            {
                _activity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage ?? "Operation failed");
            }

            RecordOperation(result, _stopwatch.Elapsed);

            if (result.Success)
            {
                Wfs20TransactionLog.OperationSucceeded(_logger, _operationType.ToString(), _featureTypeName, result.FeatureId, _stopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                Wfs20TransactionLog.OperationFailed(_logger, _operationType.ToString(), _featureTypeName, result.ErrorMessage ?? "Unknown error");
            }
        }

        public void Dispose()
        {
            _activity?.Dispose();
        }
    }
}

/// <summary>
/// High-performance logging for WFS 2.0 transaction operations.
/// </summary>
internal static partial class Wfs20TransactionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 transaction {TransactionId} started with {OperationCount} operations")]
    public static partial void TransactionStarted(ILogger logger, string transactionId, int operationCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 transaction {TransactionId} completed: Success={Success}, Inserted={Inserted}, Updated={Updated}, Deleted={Deleted}, Duration={Duration}ms")]
    public static partial void TransactionCompleted(ILogger logger, string transactionId, bool success, int inserted, int updated, int deleted, double duration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WFS 2.0 operation {OperationType} on {FeatureTypeName} succeeded: FeatureId={FeatureId}, Duration={Duration}ms")]
    public static partial void OperationSucceeded(ILogger logger, string operationType, string featureTypeName, long? featureId, double duration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 operation {OperationType} on {FeatureTypeName} failed: {ErrorMessage}")]
    public static partial void OperationFailed(ILogger logger, string operationType, string featureTypeName, string errorMessage);
}