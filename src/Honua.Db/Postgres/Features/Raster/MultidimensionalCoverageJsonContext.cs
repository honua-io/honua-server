// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// AOT-safe JSON serialization context for cloud-optimized HDF5 / NetCDF4
/// multidimensional coverage persistence types.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MultidimensionalCoverageMetadata))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class MultidimensionalCoverageJsonContext : JsonSerializerContext
{
}
