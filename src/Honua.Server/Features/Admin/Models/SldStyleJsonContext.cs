// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Styling.Sld;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// AOT-friendly JSON serialization context for SLD admin import/export endpoints.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SldImportResponse))]
[JsonSerializable(typeof(SldImportFailureResponse))]
[JsonSerializable(typeof(SldConversionDiagnostic))]
[JsonSerializable(typeof(SldConversionDiagnostic[]))]
[JsonSerializable(typeof(IReadOnlyList<SldConversionDiagnostic>))]
[JsonSerializable(typeof(ApiResponse<SldImportResponse>))]
[JsonSerializable(typeof(ApiResponse<SldImportFailureResponse>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class SldStyleJsonContext : JsonSerializerContext
{
}
