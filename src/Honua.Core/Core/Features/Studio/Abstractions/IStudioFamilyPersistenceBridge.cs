// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

public interface IStudioFamilyPersistenceBridge
{
    StudioPackageFamily Family { get; }

    string Format { get; }

    bool PublishSupported { get; }

    IReadOnlyList<StudioPackageOperation> SupportedOperations { get; }

    IReadOnlyList<string> Limitations { get; }

    Task<IReadOnlyList<StudioContentItemSummary>> ListItemSummariesAsync(CancellationToken cancellationToken = default);

    Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default);

    Task<StudioContentVersion> SaveVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default);

    Task<StudioPublicationRequest> PublishAsync(StudioPublicationRequest request, CancellationToken cancellationToken = default);
}

public static class StudioFamilyPersistenceBridgeDefaults
{
    public const int MaxEnumeratedItems = 1_000;
}
