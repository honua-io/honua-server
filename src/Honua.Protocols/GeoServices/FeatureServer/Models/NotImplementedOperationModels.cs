// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class QueryContingentValuesResponse
{
    [JsonPropertyName("typeCodes")]
    public string[] TypeCodes { get; set; } =
    [
        "Unknown",
        "Any",
        "Null",
        "Code",
        "Range"
    ];

    [JsonPropertyName("stringDicts")]
    public object[] StringDicts { get; set; } = [];

    [JsonPropertyName("contingentValuesDefinitions")]
    public ContingentValuesDefinition[] ContingentValuesDefinitions { get; set; } = [];
}

public sealed class ContingentValuesDefinition
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("fieldGroups")]
    public ContingentValueFieldGroup[] FieldGroups { get; set; } = [];
}

public sealed class ContingentValueFieldGroup
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("restrictive")]
    public bool Restrictive { get; set; } = true;

    [JsonPropertyName("fields")]
    public string[] Fields { get; set; } = [];

    [JsonPropertyName("contingentValues")]
    public ContingentValueRow[] ContingentValues { get; set; } = [];
}

public sealed class ContingentValueRow
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("subtypeCode")]
    public System.Text.Json.JsonElement? SubtypeCode { get; set; }

    [JsonPropertyName("values")]
    public Dictionary<string, ContingentFieldValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ContingentFieldValue
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "any";

    [JsonPropertyName("code")]
    public System.Text.Json.JsonElement? Code { get; set; }

    [JsonPropertyName("range")]
    public System.Text.Json.JsonElement[]? Range { get; set; }
}

public sealed class SharedTemplatesResponse
{
    [JsonPropertyName("sharedTemplates")]
    public object[] SharedTemplates { get; set; } = [];
}

public sealed class HtmlPopupResponse
{
    [JsonPropertyName("htmlPopupType")]
    public string HtmlPopupType { get; set; } = "esriServerHTMLPopupTypeNone";
}

public sealed class HasAssetsResponse
{
    [JsonPropertyName("hasAssets")]
    public bool HasAssets { get; set; }
}

public sealed class QueryAssetsResponse
{
    [JsonPropertyName("assets")]
    public object[] Assets { get; set; } = [];

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; set; }
}

public sealed class CleanupAssetsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("cleanedAssetCount")]
    public int CleanedAssetCount { get; set; }
}

public sealed class UpdateMetadataResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
