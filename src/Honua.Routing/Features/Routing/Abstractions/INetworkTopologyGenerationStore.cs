// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

public interface INetworkTopologyGenerationStore
{
    Task<IReadOnlyList<NetworkTopologyGeneration>> ListAsync(
        string datasetId,
        CancellationToken cancellationToken = default);

    Task<NetworkTopologyGeneration?> GetAsync(
        string datasetId,
        long generation,
        CancellationToken cancellationToken = default);

    Task<NetworkTopologyGeneration> AllocateDraftAsync(
        string datasetId,
        CancellationToken cancellationToken = default);
}

public sealed class NetworkTopologyActiveGenerationMissingException : Exception
{
    public NetworkTopologyActiveGenerationMissingException(string datasetId)
        : base($"Network dataset '{datasetId}' has no active topology generation to seed a draft from.")
        => DatasetId = datasetId;

    public string DatasetId { get; }
}
