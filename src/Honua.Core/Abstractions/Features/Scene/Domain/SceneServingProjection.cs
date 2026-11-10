// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Scene.Domain;

public static class SceneServingProjection
{
    public static AccessPolicy? ToAccessPolicy(SceneDatasetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.IsPublic
            ? null
            : new AccessPolicy
            {
                AllowAnonymous = false,
                AllowedRoles = record.AllowedRoles?.ToArray(),
            };
    }
}
