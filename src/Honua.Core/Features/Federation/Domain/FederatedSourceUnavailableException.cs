// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

public enum FederatedSourceUnavailableReason
{
    Faulted,

    TimedOut,

    CircuitOpen,

    NoConnector,
}

// The reason distinguishes a hard fault from a timeout or an open circuit.
public sealed class FederatedSourceUnavailableException : Exception
{
    public FederatedSourceUnavailableException(
        string sourceId,
        FederatedSourceUnavailableReason reason,
        Exception? innerException = null)
        : base($"Federated source '{sourceId}' is unavailable ({reason}).", innerException)
    {
        SourceId = sourceId;
        Reason = reason;
    }

    public string SourceId { get; }

    public FederatedSourceUnavailableReason Reason { get; }
}
