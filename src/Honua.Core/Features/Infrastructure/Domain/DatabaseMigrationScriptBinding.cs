// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>Exact resource and rendered SQL identity for a configured physical binding.</summary>
public sealed record DatabaseMigrationScriptBinding
{
    /// <summary>Unchanged canonical journal identity.</summary>
    public required string ScriptName { get; init; }
    /// <summary>Configured metadata schema.</summary>
    public required string ConfiguredSchema { get; init; }
    /// <summary>Existing physical authority, or the fresh-install target.</summary>
    public required string PhysicalSchema { get; init; }
    /// <summary>Fully qualified attachment authority.</summary>
    public required string AttachmentsTable { get; init; }
    /// <summary>Fully qualified cleanup authority in the same physical schema.</summary>
    public required string ImportCleanupTable { get; init; }
    /// <summary>SHA256 of the immutable embedded resource bytes.</summary>
    public required string ResourceSha256 { get; init; }
    /// <summary>Versioned preamble resource identity, only when its SQL is included.</summary>
    public string? PreambleResourceName { get; init; }
    /// <summary>SHA256 of the exact immutable preamble resource bytes, when included.</summary>
    public string? PreambleResourceSha256 { get; init; }
    /// <summary>SHA256 of the exact rendered SQL text encoded as UTF8.</summary>
    public required string RenderedSqlSha256 { get; init; }
}
