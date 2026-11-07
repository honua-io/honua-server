// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

public interface IStudioPublicationIndex
{
    Task<IReadOnlyList<StudioPublicationLineage>> ListAppServicesAsync(Guid itemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StudioPublicationLineage>> ListServiceAppsAsync(string serviceId, CancellationToken cancellationToken = default);
}

public interface IStudioPackageUnpublisher
{
    Task<StudioContentItemPointers> UnpublishAsync(StudioUnpublishCommand command, CancellationToken cancellationToken = default);
}
