// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.FileStorage;

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
