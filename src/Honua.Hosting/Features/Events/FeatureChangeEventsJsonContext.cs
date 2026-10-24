// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Events;

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(FeatureChangeEvent))]
[JsonSerializable(typeof(FeatureChangeEventRequest))]
[JsonSerializable(typeof(FeatureChangeEvent[]))]
[JsonSerializable(typeof(PendingFeatureChangePublish))]
[JsonSerializable(typeof(PendingFeatureChangeIndex))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(double[]))]
internal sealed partial class FeatureChangeEventsJsonContext : JsonSerializerContext
{
}
