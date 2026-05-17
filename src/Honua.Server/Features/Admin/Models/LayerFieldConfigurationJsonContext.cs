// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for layer field configuration admin APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LayerFieldConfigurationUpdateRequest))]
[JsonSerializable(typeof(LayerFieldConfigurationUpdateItem))]
[JsonSerializable(typeof(LayerFieldConfigurationResponse))]
[JsonSerializable(typeof(LayerFieldConfigurationItem))]
[JsonSerializable(typeof(ApiResponse<LayerFieldConfigurationResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(FieldDomainDefinition))]
[JsonSerializable(typeof(DomainCodedValueDefinition))]
[JsonSerializable(typeof(DomainCodedValueDefinition[]))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class LayerFieldConfigurationJsonContext : JsonSerializerContext
{
}
