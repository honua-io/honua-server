// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Capabilities;

// This shape is frozen: new fields must be additive init properties, and existing ones must not be reordered, renamed, or removed.
public sealed record CapabilityDescriptor
{
    // The capability id is the primary key other surfaces bind to and must not change once the descriptor is frozen.
    public required string Id { get; init; }

    public required string Category { get; init; }

    public required CapabilityKind Kind { get; init; }

    public required CapabilityMaturity Maturity { get; init; }

    public bool RequiresOptIn { get; init; } = true;

    public string? EntitlementKey { get; init; }

    public HonuaEdition? MinimumEdition { get; init; }

    public string? StandardName { get; init; }

    public string? McpToolName { get; init; }

    public bool IsDynamic { get; init; }

    public IReadOnlyList<string> ResourceUriTemplates { get; init; } = [];

    public string? PackageSchemaVersion { get; init; }

    public CapabilityImplementationStatus ImplementationStatus { get; init; }
        = CapabilityImplementationStatus.Served;

    public string? ConformanceMapping { get; init; }
}
