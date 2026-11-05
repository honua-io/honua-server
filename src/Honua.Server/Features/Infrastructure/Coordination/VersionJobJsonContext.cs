// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Infrastructure.Coordination;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(VersionJob))]
internal sealed partial class VersionJobJsonContext : JsonSerializerContext
{
}
