// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Sdk.Field.Forms.Authoring;

namespace Honua.Server.Features.Forms;

// One workbook cell that blocks an XLSForm import or export.
internal sealed class FormXlsFormDiagnostic
{
    [JsonPropertyName("sheet")]
    public string Sheet { get; init; } = string.Empty;

    [JsonPropertyName("row")]
    public int Row { get; init; }

    [JsonPropertyName("column")]
    public string Column { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    public static FormXlsFormDiagnostic From(XlsFormDiagnostic diagnostic) => new()
    {
        Sheet = diagnostic.Sheet,
        Row = diagnostic.RowNumber,
        Column = diagnostic.Column,
        Message = diagnostic.Message
    };
}

// Problem details body that lists every blocking workbook cell.
internal sealed class FormXlsFormProblem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("diagnostics")]
    public FormXlsFormDiagnostic[] Diagnostics { get; init; } = [];
}

// The resolved target layer an XLSForm import binds its questions to.
internal sealed class FormXlsFormTarget
{
    public required FormTargetDefinition Definition { get; init; }

    // Layer attribute types keyed case-insensitively by attribute name.
    public required IReadOnlyDictionary<string, MetadataV2FieldType> Attributes { get; init; }

    public bool HasGeometry { get; init; }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FormXlsFormProblem))]
[JsonSerializable(typeof(FormXlsFormDiagnostic[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class FormXlsFormJsonContext : JsonSerializerContext
{
}
