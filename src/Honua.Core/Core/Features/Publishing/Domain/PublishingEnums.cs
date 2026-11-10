// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

public enum PublishSourceKind
{
    ResultPackage,

    WorkspaceArtifact,

    FeatureLayer,

    RasterDataset
}

public enum PublishTargetKind
{
    FeatureService,

    TileService,

    MapService,

    StaticExport,

    SceneService
}

public enum PublishIntentStatus
{
    Draft,

    Validated,

    AwaitingApproval,

    Approved,

    Executing,

    Completed,

    Rejected,

    Failed,

    Cancelled
}

public enum PublishedServiceStatus
{
    Provisioning,

    Active,

    Suspended,

    RefreshFailed,

    Decommissioned
}

public enum RefreshMode
{
    Manual,

    Scheduled
}

public enum RefreshScope
{
    Full,

    Incremental
}

public enum RefreshOutcome
{
    Succeeded,

    PartialSuccess,

    Failed,

    Cancelled
}
