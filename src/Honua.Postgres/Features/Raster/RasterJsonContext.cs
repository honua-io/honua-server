// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// AOT-safe JSON serialization context for raster persistence types.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CogOverviewLevelSummary[]))]
internal sealed partial class RasterJsonContext : JsonSerializerContext
{
}
