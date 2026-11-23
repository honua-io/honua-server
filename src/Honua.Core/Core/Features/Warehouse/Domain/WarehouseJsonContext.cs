// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Warehouse.Domain;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters =
    [
        typeof(JsonStringEnumConverter<WarehouseSourceAvailability>),
        typeof(JsonStringEnumConverter<SpatialRelationship>)
    ])]
[JsonSerializable(typeof(WarehouseEffectiveSourceCapabilities))]
[JsonSerializable(typeof(IReadOnlyList<WarehouseEffectiveSourceCapabilities>))]
[JsonSerializable(typeof(WarehouseQueryPlan))]
public partial class WarehouseJsonContext : JsonSerializerContext;
