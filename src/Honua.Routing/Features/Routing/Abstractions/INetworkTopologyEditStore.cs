// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

public interface INetworkTopologyEditStore
{
    Task<NetworkTopologyEditResult> ApplyEditBatchAsync(
        string datasetId,
        long generation,
        long expectedRowVersion,
        string idempotencyKey,
        string contentHash,
        NetworkTopologyEditBatch batch,
        string actor,
        CancellationToken cancellationToken = default);
}

public sealed class NetworkTopologyGenerationNotFoundException : Exception
{
    public NetworkTopologyGenerationNotFoundException(string datasetId, long generation)
        : base($"Topology generation {generation} for dataset '{datasetId}' was not found.")
    {
        DatasetId = datasetId;
        Generation = generation;
    }

    public string DatasetId { get; }

    public long Generation { get; }
}

public enum NetworkTopologyEditConflictReason
{
    StaleRowVersion,

    GenerationNotEditable,

    IdempotencyKeyReused,
}

public sealed class NetworkTopologyEditConflictException : Exception
{
    public NetworkTopologyEditConflictException(NetworkTopologyEditConflictReason reason, string message)
        : base(message)
        => Reason = reason;

    public NetworkTopologyEditConflictReason Reason { get; }
}

public sealed class NetworkTopologyEditValidationException : Exception
{
    public NetworkTopologyEditValidationException(string message)
        : base(message)
    {
    }
}
