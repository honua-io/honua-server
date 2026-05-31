// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Admin request body for <c>POST /api/v{version}/admin/import/i3s-slpk</c>.
/// </summary>
internal sealed record I3sSlpkImportRequest
{
    /// <summary>Absolute server-side filesystem path to the <c>.slpk</c> archive.</summary>
    [JsonPropertyName("slpkPath")]
    public string? SlpkPath { get; init; }

    /// <summary>
    /// Optional URL slug for the registered scene dataset. When omitted, a slug
    /// is derived from the file basename.
    /// </summary>
    [JsonPropertyName("datasetId")]
    public string? DatasetId { get; init; }

    /// <summary>Optional display name override.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Whether the registered scene should be public (default: true).</summary>
    [JsonPropertyName("isPublic")]
    public bool? IsPublic { get; init; }

    /// <summary>Optional edition gate slug.</summary>
    [JsonPropertyName("editionGate")]
    public string? EditionGate { get; init; }

    /// <summary>
    /// When true, parse and validate without writing GLB files, tileset.json,
    /// or registering the dataset.
    /// </summary>
    [JsonPropertyName("dryRun")]
    public bool? DryRun { get; init; }

    /// <summary>
    /// Optional explicit output directory override. When omitted, the converter
    /// uses the configured <c>I3sImport:AssetRootBase</c> joined with the
    /// resolved dataset id.
    /// </summary>
    [JsonPropertyName("assetRoot")]
    public string? AssetRoot { get; init; }
}

/// <summary>
/// Admin response body returned by the I3S/.slpk import endpoint.
/// </summary>
internal sealed record I3sSlpkImportResult
{
    /// <summary>True when the import succeeded.</summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>Resolved dataset URL slug.</summary>
    [JsonPropertyName("datasetId")]
    public string? DatasetId { get; init; }

    /// <summary>Registered display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>I3S layer type that was converted (e.g. <c>3DObject</c>).</summary>
    [JsonPropertyName("layerType")]
    public string? LayerType { get; init; }

    /// <summary>Number of I3S nodes converted to GLB tiles.</summary>
    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; init; }

    /// <summary>Total bytes of GLB content written.</summary>
    [JsonPropertyName("tileBytesWritten")]
    public long TileBytesWritten { get; init; }

    /// <summary>Resolved output asset directory.</summary>
    [JsonPropertyName("assetRoot")]
    public string? AssetRoot { get; init; }

    /// <summary>Wall-clock conversion duration.</summary>
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; init; }

    /// <summary>Non-fatal warnings emitted during conversion.</summary>
    [JsonPropertyName("warnings")]
    public string[]? Warnings { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Configuration block bound from <c>I3sImport:*</c>.
/// </summary>
internal sealed class I3sImportOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "I3sImport";

    /// <summary>
    /// Base directory for converted tilesets. The dataset id is appended to
    /// this path to form the per-scene asset root.
    /// </summary>
    public string? AssetRootBase { get; set; }

    /// <summary>
    /// Whether the import endpoint may write outside <see cref="AssetRootBase"/>
    /// when the caller supplies an explicit <c>assetRoot</c> override.
    /// </summary>
    public bool AllowExplicitAssetRoot { get; set; }
}
