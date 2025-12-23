// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.OData.Models;

/// <summary>
/// JSON serialization context for OData models with AOT support
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ServiceDocument))]
[JsonSerializable(typeof(EntitySet))]
[JsonSerializable(typeof(EntitySet[]))]
[JsonSerializable(typeof(ODataResponse))]
[JsonSerializable(typeof(ODataError))]
[JsonSerializable(typeof(ErrorDetails))]
[JsonSerializable(typeof(ErrorDetail))]
[JsonSerializable(typeof(ErrorDetail[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>[]))]
internal partial class ODataJsonContext : JsonSerializerContext
{
}
