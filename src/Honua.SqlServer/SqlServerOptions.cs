// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.SqlServer;

/// <summary>
/// Configuration options for the SQL Server spatial feature provider.
/// </summary>
/// <remarks>
/// Bound from the <c>SqlServer</c> configuration section. The provider is read-only in this
/// slice; write paths are deliberately not exposed.
/// </remarks>
public sealed class SqlServerOptions
{
    /// <summary>
    /// Default SQL Server connection string used by the provider when a layer's
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> is not resolvable.
    /// Operators wiring per-layer secure connections should leave this unset.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// SQL Server command timeout in seconds for read operations. Defaults to 60.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 60;
}
