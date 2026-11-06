// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record DatabaseMigrationScriptBinding
{
    public required string ScriptName { get; init; }
    public required string ConfiguredSchema { get; init; }
    public required string PhysicalSchema { get; init; }
    public required string AttachmentsTable { get; init; }
    public required string ImportCleanupTable { get; init; }
    public required string ResourceSha256 { get; init; }
    public string? PreambleResourceName { get; init; }
    public string? PreambleResourceSha256 { get; init; }
    public required string RenderedSqlSha256 { get; init; }
}
