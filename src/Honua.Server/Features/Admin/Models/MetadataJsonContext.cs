// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// AOT-compatible JSON serialization context for admin metadata API
/// </summary>
[JsonSerializable(typeof(CreateServiceRequest))]
[JsonSerializable(typeof(UpdateServiceRequest))]
[JsonSerializable(typeof(ServiceResponse))]
[JsonSerializable(typeof(ServiceListResponse))]
[JsonSerializable(typeof(CreateLayerRequest))]
[JsonSerializable(typeof(UpdateLayerRequest))]
[JsonSerializable(typeof(LayerResponse))]
[JsonSerializable(typeof(LayerListResponse))]
[JsonSerializable(typeof(BindLayerRequest))]
[JsonSerializable(typeof(BindingResponse))]
[JsonSerializable(typeof(CreateRelationshipRequest))]
[JsonSerializable(typeof(RelationshipResponse))]
[JsonSerializable(typeof(RelationshipListResponse))]
[JsonSerializable(typeof(UpdateStyleRequest))]
[JsonSerializable(typeof(StyleResponse))]
[JsonSerializable(typeof(SuccessResponse))]
[JsonSerializable(typeof(ValidationErrorResponse))]
[JsonSerializable(typeof(Honua.Core.Features.Catalog.Domain.AccessPolicy))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class MetadataJsonContext : JsonSerializerContext
{
}
