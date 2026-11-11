// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Caching;

// Whether the work in the current scope must see the catalog revision committed before it started
// (a write, an admin operation, a background job) or may use a snapshot this node checked recently
// (a read). A scope with no registered answer requires the current revision.
public interface IMetadataGraphFreshnessRequirement
{
    bool RequireCurrentRevision { get; }
}
