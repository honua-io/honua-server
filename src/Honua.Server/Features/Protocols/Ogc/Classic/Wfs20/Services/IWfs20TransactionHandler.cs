// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Interface for handling WFS 2.0 transaction operations (Insert, Update, Delete).
/// </summary>
internal interface IWfs20TransactionHandler
{
    /// <summary>
    /// Processes a WFS 2.0 Transaction request.
    /// </summary>
    /// <param name="transactionRequest">XML document containing the transaction request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Transaction response containing operation results</returns>
    Task<Wfs20TransactionResponse> ProcessTransactionAsync(XDocument transactionRequest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes insert operations for features.
    /// </summary>
    /// <param name="insertElements">Collection of Insert elements from transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of insert operation results</returns>
    Task<IEnumerable<Wfs20OperationResult>> ExecuteInsertsAsync(IEnumerable<XElement> insertElements, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes update operations for features.
    /// </summary>
    /// <param name="updateElements">Collection of Update elements from transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of update operation results</returns>
    Task<IEnumerable<Wfs20OperationResult>> ExecuteUpdatesAsync(IEnumerable<XElement> updateElements, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes delete operations for features.
    /// </summary>
    /// <param name="deleteElements">Collection of Delete elements from transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of delete operation results</returns>
    Task<IEnumerable<Wfs20OperationResult>> ExecuteDeletesAsync(IEnumerable<XElement> deleteElements, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the response from a WFS 2.0 transaction operation.
/// </summary>
public readonly record struct Wfs20TransactionResponse
{
    /// <summary>
    /// Summary of the transaction operations.
    /// </summary>
    public required Wfs20TransactionSummary TransactionSummary { get; init; }

    /// <summary>
    /// Individual operation results.
    /// </summary>
    public required IReadOnlyCollection<Wfs20OperationResult> OperationResults { get; init; }

    /// <summary>
    /// Transaction ID for tracking.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Whether the transaction was successful.
    /// </summary>
    public required bool Success { get; init; }
}

/// <summary>
/// Summary statistics for a WFS 2.0 transaction.
/// </summary>
public readonly record struct Wfs20TransactionSummary
{
    /// <summary>
    /// Number of features inserted.
    /// </summary>
    public required int TotalInserted { get; init; }

    /// <summary>
    /// Number of features updated.
    /// </summary>
    public required int TotalUpdated { get; init; }

    /// <summary>
    /// Number of features deleted.
    /// </summary>
    public required int TotalDeleted { get; init; }
}

/// <summary>
/// Result of an individual WFS 2.0 operation within a transaction.
/// </summary>
public readonly record struct Wfs20OperationResult
{
    /// <summary>
    /// Type of operation performed.
    /// </summary>
    public required Wfs20OperationType OperationType { get; init; }

    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Feature ID that was affected by the operation.
    /// </summary>
    public required long? FeatureId { get; init; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public required string? ErrorMessage { get; init; }

    /// <summary>
    /// Feature type name.
    /// </summary>
    public required string FeatureTypeName { get; init; }
}

/// <summary>
/// Types of WFS 2.0 transaction operations.
/// </summary>
public enum Wfs20OperationType
{
    /// <summary>
    /// Insert operation.
    /// </summary>
    Insert,

    /// <summary>
    /// Update operation.
    /// </summary>
    Update,

    /// <summary>
    /// Delete operation.
    /// </summary>
    Delete
}
