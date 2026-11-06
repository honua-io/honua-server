// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ConsoleEmbedAudience>))]
public enum ConsoleEmbedAudience
{
    [JsonStringEnumMemberName("map")]
    Map,

    [JsonStringEnumMemberName("content")]
    Content,
}
