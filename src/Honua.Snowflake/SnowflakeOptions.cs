// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Snowflake;

/// <summary>
/// Configuration options for the Snowflake spatial feature provider (#1713).
/// </summary>
/// <remarks>
/// <para>Bound from the <c>Snowflake</c> configuration section. The provider is read-only in this
/// slice: it serves Snowflake native <c>GEOGRAPHY</c>/<c>GEOMETRY</c> columns through the shared
/// <see cref="Honua.Core.Features.FeatureStore.Abstractions.IFeatureDataProvider"/> seam; write
/// paths are deliberately not exposed.</para>
/// <para><strong>AOT note:</strong> <c>Snowflake.Data</c> uses internal reflection and is not
/// Native AOT-compatible. Deployments that publish with Native AOT must exclude this provider;
/// trimmed builds may also drop reachable members the driver relies on. Other relational
/// providers carry similar partial limitations and the same operator guidance.</para>
/// <para>A full ADO.NET <see cref="ConnectionString"/> is the simplest way to configure the
/// provider. The discrete <see cref="Account"/>/<see cref="Warehouse"/>/<see cref="Database"/>/
/// <see cref="Schema"/>/<see cref="Role"/> properties are convenience fields the operator can use
/// to document the intended target; when <see cref="ConnectionString"/> is set it takes precedence
/// and the discrete fields are ignored.</para>
/// </remarks>
public sealed class SnowflakeOptions
{
    /// <summary>
    /// Whether the provider is registered. Defaults to <see langword="true"/>. Set to
    /// <see langword="false"/> to skip registration even when the assembly is referenced
    /// (for example, in Native AOT publishing profiles).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default Snowflake ADO.NET connection string used by the provider when a layer's
    /// <see cref="Honua.Core.Features.Security.Domain.DataConnection"/> is not resolvable.
    /// Operators wiring per-layer secure connections should leave this unset. When set, it
    /// takes precedence over the discrete account/warehouse/database/schema/role fields.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Snowflake account identifier (for example <c>xy12345.us-east-1</c>). Documentation field;
    /// only used when <see cref="ConnectionString"/> is unset and a connection string is composed
    /// from discrete fields.
    /// </summary>
    public string? Account { get; set; }

    /// <summary>
    /// Snowflake user name. Documentation field used only when composing a connection string from
    /// discrete fields.
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// Snowflake virtual warehouse used to run read queries. Documentation field used only when
    /// composing a connection string from discrete fields.
    /// </summary>
    public string? Warehouse { get; set; }

    /// <summary>
    /// Default Snowflake database. Documentation field used only when composing a connection string
    /// from discrete fields.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Default Snowflake schema. Documentation field used only when composing a connection string
    /// from discrete fields.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Snowflake role applied to the session. Documentation field used only when composing a
    /// connection string from discrete fields.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Snowflake command timeout in seconds for read operations. Defaults to 60.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 60;
}
