// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;

namespace Honua.Server.Features.Admin.Models;

[JsonSerializable(typeof(TableDiscoveryResponse))]
[JsonSerializable(typeof(TableInfo))]
[JsonSerializable(typeof(ColumnInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class TableDiscoveryJsonContext : JsonSerializerContext
{
}
