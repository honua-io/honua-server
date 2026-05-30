// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Io.Export;

/// <summary>
/// Source-generated JSON serialization context for data export types.
/// </summary>
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

/// <summary>
/// Response returned when a large export is accepted for async processing.
/// </summary>
internal sealed record ExportAcceptedResponse
{
    /// <summary>
    /// Operation ID for tracking export progress.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Total features to export.
    /// </summary>
    public required long TotalFeatures { get; init; }

    /// <summary>
    /// URL to poll for progress.
    /// </summary>
    public required string StatusUrl { get; init; }
}
