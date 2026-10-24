// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.FileStorage;

/// <summary>
/// JSON source generation context for local file storage metadata.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CloudFile))]
internal sealed partial class FileStorageJsonContext : JsonSerializerContext
{
}
