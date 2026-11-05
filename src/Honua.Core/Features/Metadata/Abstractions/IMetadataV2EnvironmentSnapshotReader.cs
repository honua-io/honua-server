// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

public interface IMetadataV2EnvironmentSnapshotReader
{
    ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
        string environment,
        CancellationToken cancellationToken = default);

    ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
        string environment,
        long revision,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
        IReadOnlyList<string> environments,
        CancellationToken cancellationToken = default);
}
