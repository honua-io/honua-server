// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Redshift;

/// <summary>
/// Configuration options for the read-only Amazon Redshift spatial feature provider (#1712).
/// </summary>
/// <remarks>
/// Bound from the <c>Redshift</c> configuration section. The provider is read-only; write paths
/// are deliberately not exposed. Redshift speaks the PostgreSQL wire protocol, so connectivity
/// uses Npgsql, but spatial behavior relies on Redshift's native <c>GEOMETRY</c>/<c>GEOGRAPHY</c>
/// types and spatial SQL functions rather than PostGIS.
/// </remarks>
public sealed class RedshiftOptions
{
    /// <summary>
    /// Default Redshift connection string used by the provider when a layer's
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> is not resolvable.
    /// Operators wiring per-layer secure connections should leave this unset.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Redshift command timeout in seconds for read operations. Defaults to 60.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Whether the Redshift provider is registered in the runtime. Defaults to true.
    /// Set to false to skip registration without removing the configuration section.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
