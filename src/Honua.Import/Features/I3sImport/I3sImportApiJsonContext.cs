// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Source-generated JSON context for the I3S/.slpk admin import API DTOs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(I3sSlpkImportRequest))]
[JsonSerializable(typeof(I3sSlpkImportResult))]
internal sealed partial class I3sImportApiJsonContext : JsonSerializerContext
{
}
