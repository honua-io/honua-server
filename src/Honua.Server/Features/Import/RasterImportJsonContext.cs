// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Import;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RasterImportResult))]
[JsonSerializable(typeof(RasterImportProgress))]
[JsonSerializable(typeof(RasterImportEndpoints.RasterFormatsResponse))]
[JsonSerializable(typeof(SupportedRasterFormat))]
[JsonSerializable(typeof(RasterImportPhase))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(TimeSpan))]
internal sealed partial class RasterImportJsonContext : JsonSerializerContext
{
}
