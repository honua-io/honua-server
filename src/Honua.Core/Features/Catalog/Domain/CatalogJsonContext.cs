// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using System.Text.Json;

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// JSON serialization context for catalog domain payloads.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FieldDomainDefinition))]
[JsonSerializable(typeof(DomainCodedValueDefinition))]
[JsonSerializable(typeof(DomainCodedValueDefinition[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class CatalogJsonContext : JsonSerializerContext
{
}
