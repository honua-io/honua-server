// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Io.Export;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExportProgress))]
[JsonSerializable(typeof(ExportAcceptedResponse))]
[JsonSerializable(typeof(ExportJob))]
[JsonSerializable(typeof(ExportJobService.PersistedExportJobRequest))]
[JsonSerializable(typeof(ExportField[]))]
[JsonSerializable(typeof(FeatureQuery))]
[JsonSerializable(typeof(ExportGeometryType))]
[JsonSerializable(typeof(ExportFieldType))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
internal sealed partial class ExportJsonContext : JsonSerializerContext
{
}

internal sealed record ExportAcceptedResponse
{
    public required string OperationId { get; init; }

    public required string Message { get; init; }

    public required long TotalFeatures { get; init; }

    public required string StatusUrl { get; init; }
}
