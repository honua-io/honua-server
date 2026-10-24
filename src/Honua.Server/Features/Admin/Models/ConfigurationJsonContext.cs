// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Configuration;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON serialization context for configuration documentation (AOT compatible).
/// </summary>
[JsonSerializable(typeof(ConfigurationDocumentation))]
[JsonSerializable(typeof(ConfigurationSection))]
[JsonSerializable(typeof(ConfigurationProperty))]
[JsonSerializable(typeof(EnvironmentVariableInfo))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(object))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext
{
}
