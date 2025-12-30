// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Provides the current database schema context for request-scoped operations.
/// </summary>
public interface ISchemaContext
{
    /// <summary>
    /// Gets the current schema name to use as the search path for database operations.
    /// </summary>
    string? CurrentSchema { get; }
}
