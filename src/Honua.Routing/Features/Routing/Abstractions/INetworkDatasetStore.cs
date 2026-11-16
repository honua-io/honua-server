// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

public interface INetworkDatasetStore
{
    Task<IReadOnlyList<NetworkDatasetRecord>> ListAsync(
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    Task<NetworkDatasetRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<NetworkDatasetRecord> RegisterAsync(
        NetworkDatasetRecord dataset,
        CancellationToken cancellationToken = default);

    Task<NetworkDatasetRecord?> UpdateAsync(
        NetworkDatasetRecord dataset,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class NetworkDatasetAlreadyExistsException : Exception
{
    public NetworkDatasetAlreadyExistsException(string id)
        : base($"A network dataset with id '{id}' is already registered.")
        => Id = id;

    public string Id { get; }
}
