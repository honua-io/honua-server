// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Source-generated JSON context for the CityGML/BIM scene ingest admin API
/// response (#1207). The request is multipart/form-data, so only the response
/// model needs source-generated serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CityGmlIngestResponse))]
internal sealed partial class SceneBimIngestJsonContext : JsonSerializerContext
{
}
