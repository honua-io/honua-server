// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Portal.Domain;

/// <summary>
/// Source-generated JSON serialization context for Portal item DTOs. Keeps the
/// Portal read surface AOT-safe (no reflection in the hot path).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PortalItem))]
[JsonSerializable(typeof(IReadOnlyList<PortalItem>))]
[JsonSerializable(typeof(PortalItem[]))]
public sealed partial class PortalItemJsonContext : JsonSerializerContext
{
}
