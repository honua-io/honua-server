// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model with JSON source generation for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(TableDiscoveryResponse))]
[JsonSerializable(typeof(TableInfo))]
[JsonSerializable(typeof(ColumnInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class TableDiscoveryJsonContext : JsonSerializerContext
{
}
