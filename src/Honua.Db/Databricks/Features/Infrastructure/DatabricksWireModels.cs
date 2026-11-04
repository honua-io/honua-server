// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Db.Databricks.Features.Infrastructure;

// JSON wire-format types for the Databricks SQL Statement Execution REST API
// (POST /api/2.0/sql/statements and GET /api/2.0/sql/statements/{id}). Kept minimal —
// only fields read by the read-through provider are modeled.

#pragma warning disable CA1812 // Internal types are instantiated via JSON (de)serialization.

internal sealed record DatabricksStatementRequest
{
    [JsonPropertyName("statement")]
    public string Statement { get; init; } = string.Empty;

    [JsonPropertyName("warehouse_id")]
    public string WarehouseId { get; init; } = string.Empty;

    [JsonPropertyName("wait_timeout")]
    public string? WaitTimeout { get; init; }

    [JsonPropertyName("on_wait_timeout")]
    public string? OnWaitTimeout { get; init; }

    [JsonPropertyName("disposition")]
    public string Disposition { get; init; } = "INLINE";

    [JsonPropertyName("format")]
    public string Format { get; init; } = "JSON_ARRAY";

    [JsonPropertyName("catalog")]
    public string? Catalog { get; init; }

    [JsonPropertyName("schema")]
    public string? Schema { get; init; }

    [JsonPropertyName("parameters")]
    public IReadOnlyList<DatabricksStatementParameter>? Parameters { get; init; }
}

internal sealed record DatabricksStatementParameter
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed record DatabricksStatementResponse
{
    [JsonPropertyName("statement_id")]
    public string? StatementId { get; init; }

    [JsonPropertyName("status")]
    public DatabricksStatementStatus? Status { get; init; }

    [JsonPropertyName("manifest")]
    public DatabricksStatementManifest? Manifest { get; init; }

    [JsonPropertyName("result")]
    public DatabricksStatementResult? Result { get; init; }
}

internal sealed record DatabricksStatementStatus
{
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("error")]
    public DatabricksStatementError? Error { get; init; }
}

internal sealed record DatabricksStatementError
{
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed record DatabricksStatementManifest
{
    [JsonPropertyName("schema")]
    public DatabricksResultSchema? Schema { get; init; }

    [JsonPropertyName("total_row_count")]
    public long? TotalRowCount { get; init; }
}

internal sealed record DatabricksResultSchema
{
    [JsonPropertyName("columns")]
    public DatabricksResultColumn[]? Columns { get; init; }
}

internal sealed record DatabricksResultColumn
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type_name")]
    public string? TypeName { get; init; }

    [JsonPropertyName("position")]
    public int Position { get; init; }
}

internal sealed record DatabricksStatementResult
{
    [JsonPropertyName("data_array")]
    public JsonElement[][]? DataArray { get; init; }

    [JsonPropertyName("row_count")]
    public long? RowCount { get; init; }

    [JsonPropertyName("next_chunk_internal_link")]
    public string? NextChunkInternalLink { get; init; }
}

[JsonSerializable(typeof(DatabricksStatementRequest))]
[JsonSerializable(typeof(DatabricksStatementResponse))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DatabricksJsonContext : JsonSerializerContext
{
}
