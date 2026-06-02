// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response model for the service-level <c>queryContingentValues</c> operation.
/// Honua does not yet model contingent attribute values; the response is shaped per
/// the Esri specification with empty definition collections so SDK clients parse it
/// without error and observe that no contingent value rules are configured.
/// </summary>
public sealed class QueryContingentValuesResponse
{
    /// <summary>
    /// Ordered value-type identifiers used by compact contingent value rows
    /// (<c>Unknown</c>, <c>Any</c>, <c>Null</c>, <c>Code</c>, <c>Range</c>).
    /// </summary>
    [JsonPropertyName("typeCodes")]
    public string[] TypeCodes { get; set; } =
    [
        "Unknown",
        "Any",
        "Null",
        "Code",
        "Range"
    ];

    /// <summary>
    /// Domain string dictionaries referenced by contingent value rows.
    /// Empty until contingent value modeling is implemented.
    /// </summary>
    [JsonPropertyName("stringDicts")]
    public object[] StringDicts { get; set; } = [];

    /// <summary>
    /// Per-layer contingent value definitions. Empty until contingent value
    /// modeling is implemented.
    /// </summary>
    [JsonPropertyName("contingentValuesDefinitions")]
    public object[] ContingentValuesDefinitions { get; set; } = [];
}

/// <summary>
/// Response model for the service-level <c>sharedTemplates</c> resource and its
/// <c>query</c> child operation. Honua does not persist shared editing templates;
/// the response is shaped per the Esri specification with an empty template list.
/// </summary>
public sealed class SharedTemplatesResponse
{
    /// <summary>
    /// Shared editing templates configured for the service. Empty until shared
    /// template storage is implemented.
    /// </summary>
    [JsonPropertyName("sharedTemplates")]
    public object[] SharedTemplates { get; set; } = [];
}

/// <summary>
/// Response model for the service-level <c>htmlPopup</c> resource. Honua does not
/// serve author-defined HTML pop-ups, so the type is always
/// <c>esriServerHTMLPopupTypeNone</c> per the Esri specification.
/// </summary>
public sealed class HtmlPopupResponse
{
    /// <summary>
    /// HTML pop-up type. Always <c>esriServerHTMLPopupTypeNone</c> until authored
    /// pop-up content is supported.
    /// </summary>
    [JsonPropertyName("htmlPopupType")]
    public string HtmlPopupType { get; set; } = "esriServerHTMLPopupTypeNone";
}

/// <summary>
/// Response model for the layer-level <c>hasAssets</c> operation. Honua does not yet
/// store layer assets; the flag is always <see langword="false"/>.
/// </summary>
public sealed class HasAssetsResponse
{
    /// <summary>
    /// Indicates whether the layer has any stored assets. Always
    /// <see langword="false"/> until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("hasAssets")]
    public bool HasAssets { get; set; }
}

/// <summary>
/// Response model for the layer-level <c>queryAssets</c> operation. Honua does not
/// yet store layer assets; the asset collection is always empty.
/// </summary>
public sealed class QueryAssetsResponse
{
    /// <summary>
    /// Assets matching the query. Empty until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("assets")]
    public object[] Assets { get; set; } = [];

    /// <summary>
    /// Indicates whether more assets exist than were returned. Always
    /// <see langword="false"/> until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; set; }
}

/// <summary>
/// Response model for the layer-level <c>cleanupAssets</c> operation. Honua does not
/// yet store layer assets, so no assets are ever removed.
/// </summary>
public sealed class CleanupAssetsResponse
{
    /// <summary>
    /// Indicates whether the cleanup completed. Always <see langword="true"/> as a
    /// no-op until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    /// <summary>
    /// Number of orphaned assets removed. Always zero until asset storage is
    /// implemented.
    /// </summary>
    [JsonPropertyName("cleanedAssetCount")]
    public int CleanedAssetCount { get; set; }
}

/// <summary>
/// Response model for the layer-level <c>metadata/update</c> operation acknowledgement.
/// </summary>
public sealed class UpdateMetadataResponse
{
    /// <summary>
    /// Indicates whether the metadata update was accepted.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
