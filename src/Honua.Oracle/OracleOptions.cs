// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Oracle;

/// <summary>
/// Configuration options for the Oracle spatial feature provider (#1252).
/// </summary>
/// <remarks>
/// <para>Bound from the <c>Oracle</c> configuration section. The provider is read-only in this
/// slice: it serves standard <c>SDO_GEOMETRY</c> spatial tables and refuses ArcSDE
/// <c>ST_Geometry</c> or versioned-table sources at first execution.</para>
/// <para><strong>AOT note:</strong> <c>Oracle.ManagedDataAccess.Core</c> uses internal reflection
/// and is not Native AOT-compatible. Deployments that publish with Native AOT must exclude this
/// provider; trimmed builds may also drop reachable members the driver relies on. Other
/// relational providers carry similar partial limitations and the same operator guidance.</para>
/// </remarks>
public sealed class OracleOptions
{
    /// <summary>
    /// Default Oracle connection string used by the provider when a layer's
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> is not resolvable.
    /// Operators wiring per-layer secure connections should leave this unset.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Oracle command timeout in seconds for read operations. Defaults to 60.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 60;
}
