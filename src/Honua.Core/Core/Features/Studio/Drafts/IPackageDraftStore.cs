// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

public interface IPackageDraftStore
{
    Task SaveMapDraftAsync(MapPackage package, string ownerId, CancellationToken cancellationToken = default);

    Task SaveAppDraftAsync(AppPackage package, string ownerId, CancellationToken cancellationToken = default);

    Task<MapPackage?> GetMapDraftAsync(string mapPackageId, string ownerId, CancellationToken cancellationToken = default);

    Task<AppPackage?> GetAppDraftAsync(string appPackageId, string ownerId, CancellationToken cancellationToken = default);
}

public sealed record PackageDraftRetentionOptions
{
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);

    public int Capacity { get; init; } = 500;
}
