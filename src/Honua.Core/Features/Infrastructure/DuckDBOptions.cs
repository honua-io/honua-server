// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Infrastructure;

/// <summary>
/// Configuration options for DuckDB integration.
/// </summary>
public class DuckDBOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "DuckDB";

    /// <summary>
    /// Connection string for DuckDB.
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = ":memory:";

    /// <summary>
    /// Whether to enable DuckDB integration.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum connections to DuckDB.
    /// </summary>
    [Range(1, 100)]
    public int MaxConnections { get; set; } = 10;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    [Range(1, 300)]
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}
