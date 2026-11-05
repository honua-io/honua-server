// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Infrastructure.MultiTenancy;

public sealed class TenantSchemaOptions
{
    public const string SectionName = "MultiTenancy:SchemaRouting";

    public bool Enabled { get; set; }

    // The prefix must itself be a valid SQL identifier.
    public string SchemaPrefix { get; set; } = "tenant_";

    public Dictionary<string, string> SchemaMap { get; set; } = new(System.StringComparer.Ordinal);

    // Duplicate assignments of one tenant to different schemas are rejected.
    public TenantSchemaMapping[] SchemaMappings { get; set; } = [];

    // Changing the escaping mode does not migrate database schemas, and names over 63 bytes are rejected.
    public bool UseEncodedSchemaNames { get; set; }

    public string[] UnroutedTenantIds { get; set; } = ["public"];
}

public sealed class TenantSchemaMapping
{
    public string TenantId { get; set; } = string.Empty;

    public string SchemaName { get; set; } = string.Empty;
}
