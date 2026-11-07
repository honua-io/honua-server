// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Infrastructure;

public class DuckDBOptions
{
    public const string SectionName = "DuckDB";

    [Required]
    public string ConnectionString { get; set; } = ":memory:";

    public bool Enabled { get; set; }

    [Range(1, 100)]
    public int MaxConnections { get; set; } = 10;

    [Range(1, 300)]
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}
