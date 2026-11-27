// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Events;

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(FeatureChangeEvent))]
[JsonSerializable(typeof(FeatureChangeEventRequest))]
[JsonSerializable(typeof(FeatureChangeEvent[]))]
[JsonSerializable(typeof(AotFeatureAttributeTypes))]
internal sealed partial class FeatureChangeEventsJsonContext : JsonSerializerContext
{
}
