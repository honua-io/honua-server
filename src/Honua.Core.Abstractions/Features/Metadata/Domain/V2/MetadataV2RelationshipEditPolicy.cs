// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>Shared write constraint for relationship semantics retained from a source.</summary>
public static class MetadataV2RelationshipEditPolicy
{
    /// <summary>Reason writes are denied until composite ownership edits are supported.</summary>
    public const string ReadOnlyReason =
        "Composite relationship ownership edits are not supported; this resource is read-only.";

    /// <summary>Whether a resource carries composite semantics that require read-only access.</summary>
    /// <param name="resource">Resource whose relationships are being evaluated.</param>
    /// <returns>True when any relationship declares composite ownership.</returns>
    public static bool RequiresReadOnly(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Relationships.Any(static relationship => relationship.Composite);
    }
}
