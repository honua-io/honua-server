// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Source-generated JSON context for the LAS/LAZ/COPC point-cloud scene ingest
/// admin API response (#1201). The request is multipart/form-data, so only the
/// response model needs source-generated serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PointCloudIngestResponse))]
internal sealed partial class ScenePointCloudIngestJsonContext : JsonSerializerContext
{
}
