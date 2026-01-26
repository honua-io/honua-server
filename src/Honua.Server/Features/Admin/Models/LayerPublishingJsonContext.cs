// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;
using Honua.Server.Features.Infrastructure.Models;

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
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(PublishLayerRequest))]
[JsonSerializable(typeof(LayerEnabledRequest))]
[JsonSerializable(typeof(PublishedLayerSummary))]
internal sealed partial class LayerPublishingJsonContext : JsonSerializerContext
{
}
