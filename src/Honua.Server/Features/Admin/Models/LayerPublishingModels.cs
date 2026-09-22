// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload for publishing a PostGIS table as a layer.
/// </summary>
public sealed class PublishLayerRequest
{
    /// <summary>
    /// Create an independent editable managed copy. The connection must resolve
    /// to this server's managed database. Source IDs remain in honua_source_id;
    /// attachments and relationships require mapping to the copy's new IDs.
    /// </summary>
    public bool CreateEditableCopy { get; init; }

    /// <summary>
    /// Schema containing the source table.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Table { get; init; }

    /// <summary>
    /// Display name for the layer.
    /// </summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string LayerName { get; init; }

    /// <summary>
    /// Optional layer description.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; init; }

    /// <summary>Optional SPDX license expression or the literal <c>proprietary</c>.</summary>
    [StringLength(LayerSourceGovernance.MaxLicenseLength)]
    public string? License { get; init; }

    /// <summary>Optional attribution surfaced by public protocol metadata.</summary>
    [StringLength(LayerSourceGovernance.MaxAttributionLength)]
    public string? Attribution { get; init; }

    /// <summary>Optional data producer or source organization.</summary>
    [StringLength(LayerSourceGovernance.MaxPublisherLength)]
    public string? Publisher { get; init; }

    /// <summary>Optional absolute HTTP(S) URL for license documentation.</summary>
    [StringLength(LayerSourceGovernance.MaxUrlLength)]
    public string? LicenseUrl { get; init; }

    /// <summary>Optional absolute HTTP(S) URL for source documentation.</summary>
    [StringLength(LayerSourceGovernance.MaxUrlLength)]
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Geometry column name.
    /// </summary>
    [StringLength(64)]
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type (Point, Polygon, etc).
    /// </summary>
    [StringLength(64)]
    public string? GeometryType { get; init; }

    /// <summary>Whether the source geometries carry elevation (Z) ordinates.</summary>
    public bool HasZ { get; init; }

    /// <summary>Whether the source geometries carry measure (M) ordinates.</summary>
    public bool HasM { get; init; }

    /// <summary>
    /// Spatial reference identifier.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int? Srid { get; init; }

    /// <summary>
    /// Primary key field name.
    /// </summary>
    [StringLength(64)]
    public string? PrimaryKey { get; init; }

    /// <summary>Existing published UUID column containing edit-stable global IDs. Does not enable editing.</summary>
    [StringLength(63)]
    public string? GlobalIdField { get; init; }

    /// <summary>Expose the configured attachment store for this resource; independent of copy fidelity and edit grants.</summary>
    public bool SupportsAttachments { get; init; }

    /// <summary>
    /// Selected attribute fields to publish (empty means include all).
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Captured source value domains keyed by field name. Held to the import capture caps
    /// (<see cref="LayerPublishSourceMetadataBounds"/>); fields without an entry publish without a domain.
    /// </summary>
    public IReadOnlyDictionary<string, MetadataV2FieldDomain>? FieldDomains { get; init; }

    /// <summary>
    /// Captured source subtype set. Held to the import capture caps
    /// (<see cref="LayerPublishSourceMetadataBounds"/>); attached only when its subtype field is published.
    /// </summary>
    public MetadataV2Subtypes? Subtypes { get; init; }

    /// <summary>
    /// Captured source calculation, constraint and validation rules. Held to the import capture caps
    /// (<see cref="LayerPublishSourceMetadataBounds"/>); calculation rules on unpublished fields are dropped.
    /// </summary>
    public IReadOnlyList<MetadataV2AttributeRule>? AttributeRules { get; init; }

    /// <summary>
    /// Optional service name (defaults to "default").
    /// </summary>
    [StringLength(64)]
    public string? ServiceName { get; init; }

    /// <summary>
    /// Whether to enable the layer after publishing.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Where the layer's features live: <c>source</c> (default) serves the live source
    /// table read-only; <c>managed</c> copies the rows into the managed feature store, which
    /// accepts edits.
    /// </summary>
    [StringLength(16)]
    public string? StorageMode { get; init; }

    /// <summary>
    /// Capability tokens to declare on the feature publication. Omit for the read-only
    /// default <c>["Query","Extract"]</c>. <c>Create</c>, <c>Update</c>, <c>Delete</c> and
    /// <c>Editing</c> require <c>storageMode</c> <c>managed</c>.
    /// </summary>
    public IReadOnlyList<string>? Capabilities { get; init; }
}

/// <summary>
/// Request payload for validating a selected table before publishing it as a layer.
/// </summary>
public sealed class ValidateTablePublishRequest
{
    /// <summary>
    /// Schema containing the source table.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Schema { get; init; }

    /// <summary>
    /// Source table name.
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public required string Table { get; init; }

    /// <summary>
    /// Optional display name for the layer being prepared.
    /// </summary>
    [StringLength(128, MinimumLength = 1)]
    public string? LayerName { get; init; }

    /// <summary>
    /// Optional target service name.
    /// </summary>
    [StringLength(64)]
    public string? ServiceName { get; init; }

    /// <summary>
    /// Requested target spatial reference identifier.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Requested geometry column name.
    /// </summary>
    [StringLength(64)]
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Requested primary key field name.
    /// </summary>
    [StringLength(64)]
    public string? PrimaryKey { get; init; }

    /// <summary>
    /// Selected attribute fields to publish. Empty means include all.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Request payload for enabling/disabling a layer.
/// </summary>
public sealed class LayerEnabledRequest
{
    public bool Enabled { get; init; }
}
