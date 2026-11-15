// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2RelationshipEditPolicy
{
    public const string ReadOnlyReason =
        "Composite relationship ownership edits are not supported; this resource is read-only.";

    public static bool RequiresReadOnly(MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Relationships.Any(static relationship => relationship.Composite);
    }
}
