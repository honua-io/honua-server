// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Operations.ServicePublish;

/// <summary>
/// Input payload for the <c>service.publish</c> operation. Carries the secure
/// connection identifier plus the table-to-layer publish parameters, mirroring the
/// fields the admin layer-publishing endpoint accepts.
/// </summary>
public sealed class ServicePublishInput
{
    /// <summary>
    /// Identifier of the secure connection whose connection string the publish runs
    /// against. May be a connection GUID or a connection name.
    /// </summary>
    public required string ConnectionId { get; init; }

    /// <summary>
    /// Schema containing the source table.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Display name for the published layer.
    /// </summary>
    public required string LayerName { get; init; }

    /// <summary>
    /// Optional layer description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Geometry column name.
    /// </summary>
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type (Point, Polygon, etc).
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial reference identifier.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Primary key field name.
    /// </summary>
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Selected attribute fields to publish (empty means include all).
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional service name (defaults to "default").
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Whether to enable the layer after publishing.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Success output for the <c>service.publish</c> operation: a projection of the
/// published layer summary returned by the canonical layer-publishing service.
/// </summary>
public sealed class ServicePublishOutput
{
    /// <summary>
    /// Identifier of the published layer.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Display name of the published layer.
    /// </summary>
    public required string LayerName { get; init; }

    /// <summary>
    /// Name of the service the layer was published into.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Source schema of the published table.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Source table of the published layer.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Geometry type of the published layer.
    /// </summary>
    public required string GeometryType { get; init; }

    /// <summary>
    /// Spatial reference identifier of the published layer.
    /// </summary>
    public int Srid { get; init; }

    /// <summary>
    /// Number of published attribute fields.
    /// </summary>
    public int FieldCount { get; init; }

    /// <summary>
    /// Whether the published layer is enabled for serving.
    /// </summary>
    public bool Enabled { get; init; }
}
