// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Source-generated JSON serialization context for packaging domain models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<PackageStatus>), typeof(JsonStringEnumConverter<SourceProtocol>)])]
[JsonSerializable(typeof(MapPackage))]
[JsonSerializable(typeof(AppPackage))]
[JsonSerializable(typeof(SourceBinding))]
[JsonSerializable(typeof(SourceLocator))]
[JsonSerializable(typeof(StyleRef))]
[JsonSerializable(typeof(MapInitialView))]
[JsonSerializable(typeof(PopupBinding))]
[JsonSerializable(typeof(LabelBinding))]
[JsonSerializable(typeof(AssetManifestEntry))]
[JsonSerializable(typeof(DeliveryHints))]
[JsonSerializable(typeof(LegendEntry))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class PackagingJsonContext : JsonSerializerContext
{
}
