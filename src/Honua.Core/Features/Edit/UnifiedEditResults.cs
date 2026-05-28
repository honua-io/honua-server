// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Result of unified edit execution.
/// </summary>
public sealed record UnifiedEditResult
{
    /// <summary>
    /// Whether the edit execution succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Edit result if execution succeeded.
    /// </summary>
    public FeatureEditResult? Result { get; init; }

    /// <summary>
    /// The unified edit request that was executed.
    /// </summary>
    public UnifiedEditRequest? EditRequest { get; init; }

    /// <summary>
    /// The transaction that was executed.
    /// </summary>
    public EditTransaction? Transaction { get; init; }

    /// <summary>
    /// Protocol that initiated the edit.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Protocol-specific metadata for response formatting.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful edit result.
    /// </summary>
    /// <param name="result">Edit result.</param>
    /// <param name="editRequest">Unified edit request.</param>
    /// <param name="transaction">Edit transaction.</param>
    /// <param name="protocol">Source protocol.</param>
    /// <param name="metadata">Protocol metadata.</param>
    /// <returns>Successful result.</returns>
    public static UnifiedEditResult Success(
        FeatureEditResult result,
        UnifiedEditRequest editRequest,
        EditTransaction transaction,
        string protocol,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            Result = result,
            EditRequest = editRequest,
            Transaction = transaction,
            Protocol = protocol,
            Metadata = metadata
        };

    /// <summary>
    /// Creates a failed edit result.
    /// </summary>
    /// <param name="errorMessage">Error message.</param>
    /// <returns>Failed result.</returns>
    public static UnifiedEditResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of unified batch edit execution.
/// </summary>
public sealed record UnifiedBatchEditResult
{
    /// <summary>
    /// Whether the batch edit execution succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Results of individual edit operations.
    /// </summary>
    public ImmutableArray<FeatureEditResult>? Results { get; init; }

    /// <summary>
    /// The transaction that was executed.
    /// </summary>
    public EditTransaction? Transaction { get; init; }

    /// <summary>
    /// Whether all operations succeeded.
    /// </summary>
    public bool AllOperationsSucceeded { get; init; }

    /// <summary>
    /// Protocol that initiated the batch edit.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Protocol-specific metadata for response formatting.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful batch edit result.
    /// </summary>
    /// <param name="results">Edit results.</param>
    /// <param name="transaction">Edit transaction.</param>
    /// <param name="allSucceeded">Whether all operations succeeded.</param>
    /// <param name="protocol">Source protocol.</param>
    /// <param name="metadata">Protocol metadata.</param>
    /// <returns>Successful result.</returns>
    public static UnifiedBatchEditResult Success(
        ImmutableArray<FeatureEditResult> results,
        EditTransaction transaction,
        bool allSucceeded,
        string protocol,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            Results = results,
            Transaction = transaction,
            AllOperationsSucceeded = allSucceeded,
            Protocol = protocol,
            Metadata = metadata
        };

    /// <summary>
    /// Creates a failed batch edit result.
    /// </summary>
    /// <param name="errorMessage">Error message.</param>
    /// <returns>Failed result.</returns>
    public static UnifiedBatchEditResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}
