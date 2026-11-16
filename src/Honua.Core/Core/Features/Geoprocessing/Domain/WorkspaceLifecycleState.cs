// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public enum WorkspaceLifecycleState
{
    Active,

    Expired,

    Archived,

    Deleted
}

public enum ArtifactLifecycleState
{
    Pending,

    Available,

    Promoted,

    Expired,

    Deleted
}
