// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Postgres.Features.Import.JsonContexts;

/// <summary>
/// PERFORMANCE FIX: Optimized JSON serialization context for import operations
/// Pre-compiled for better performance with AOT compatibility
/// </summary>
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false, // Compact JSON for storage
    AllowTrailingCommas = false,
    PropertyNameCaseInsensitive = false)]
internal sealed partial class ImportJsonOptimizationContext : JsonSerializerContext
{
    /// <summary>
    /// PERFORMANCE FIX: Optimized JsonSerializerOptions for import operations
    /// </summary>
    public static readonly JsonSerializerOptions OptimizedOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        TypeInfoResolver = Default
    };
}
