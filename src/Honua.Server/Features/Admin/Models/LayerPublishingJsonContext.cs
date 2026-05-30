// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for layer publishing admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<PublishedLayerSummary>>))]
[JsonSerializable(typeof(ApiResponse<PublishedLayerSummary>))]
[JsonSerializable(typeof(ApiResponse<TablePublishValidationResult>))]
[JsonSerializable(typeof(ApiResponse<LayerExtentRefreshResult>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(PublishLayerRequest))]
[JsonSerializable(typeof(ValidateTablePublishRequest))]
[JsonSerializable(typeof(LayerEnabledRequest))]
[JsonSerializable(typeof(PublishedLayerSummary))]
[JsonSerializable(typeof(LayerExtentRefreshResult))]
[JsonSerializable(typeof(LayerExtentRefreshLayerResult))]
[JsonSerializable(typeof(LayerExtentRefreshLayerResult[]))]
[JsonSerializable(typeof(TablePublishValidationResult))]
[JsonSerializable(typeof(TablePublishValidationCheck))]
[JsonSerializable(typeof(TablePublishValidationCheck[]))]
[JsonSerializable(typeof(TablePublishValidationField))]
[JsonSerializable(typeof(TablePublishValidationField[]))]
internal sealed partial class LayerPublishingJsonContext : JsonSerializerContext
{
}
