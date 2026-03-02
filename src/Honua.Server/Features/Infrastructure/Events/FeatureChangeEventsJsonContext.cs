// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Events;

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(FeatureChangeEvent))]
[JsonSerializable(typeof(FeatureChangeEvent[]))]
internal sealed partial class FeatureChangeEventsJsonContext : JsonSerializerContext
{
}
