// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

/// <summary>
/// Why a federated source could not be reached for a query.
/// </summary>
public enum FederatedSourceUnavailableReason
{
    /// <summary>The remote source returned an error or the transport faulted.</summary>
    Faulted,

    /// <summary>The remote call exceeded the source's configured request timeout.</summary>
    TimedOut,

    /// <summary>
    /// The circuit breaker for the source is open, so the call fast-failed without
    /// contacting the remote source. This bounds the blast radius of a failing source.
    /// </summary>
    CircuitOpen,

    /// <summary>No connector is registered for the source's transport kind.</summary>
    NoConnector,
}

/// <summary>
/// Raised when the federation executor cannot obtain results from a single federated source.
/// The <see cref="Reason"/> distinguishes a hard fault from a timeout or an open circuit so
/// callers can decide whether to surface a partial result, retry later, or fail the request.
/// </summary>
public sealed class FederatedSourceUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedSourceUnavailableException"/> class.
    /// </summary>
    /// <param name="sourceId">The identifier of the source that was unavailable.</param>
    /// <param name="reason">Why the source was unavailable.</param>
    /// <param name="innerException">The underlying transport or resilience exception, if any.</param>
    public FederatedSourceUnavailableException(
        string sourceId,
        FederatedSourceUnavailableReason reason,
        Exception? innerException = null)
        : base($"Federated source '{sourceId}' is unavailable ({reason}).", innerException)
    {
        SourceId = sourceId;
        Reason = reason;
    }

    /// <summary>
    /// Gets the identifier of the source that was unavailable.
    /// </summary>
    public string SourceId { get; }

    /// <summary>
    /// Gets the reason the source was unavailable.
    /// </summary>
    public FederatedSourceUnavailableReason Reason { get; }
}
