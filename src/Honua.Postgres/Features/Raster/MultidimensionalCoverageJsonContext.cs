// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Postgres.Features.Raster;

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
