// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Configuration;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON serialization context for configuration documentation (AOT compatible).
/// </summary>
[JsonSerializable(typeof(ConfigurationDocumentation))]
[JsonSerializable(typeof(ConfigurationSection))]
[JsonSerializable(typeof(ConfigurationProperty))]
[JsonSerializable(typeof(EnvironmentVariableInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext
{
}
