// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

public sealed class VersionLockedException : Exception
{
    public Guid VersionId { get; }

    public VersionLockedException(Guid versionId)
        : base($"A reconcile or post is already in progress for version {versionId}.")
        => VersionId = versionId;
}
