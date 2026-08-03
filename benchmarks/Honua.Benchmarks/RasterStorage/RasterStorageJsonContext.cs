// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Benchmarks.RasterStorage;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RasterStorageProtocolDefinition))]
[JsonSerializable(typeof(RasterStorageBenchmarkRun))]
internal sealed partial class RasterStorageJsonContext : JsonSerializerContext;
