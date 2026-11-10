// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ConsoleContentItemType>))]
public enum ConsoleContentItemType
{
    [JsonStringEnumMemberName("service")]
    Service,

    [JsonStringEnumMemberName("layer")]
    Layer,

    [JsonStringEnumMemberName("saved-map")]
    SavedMap,

    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    [JsonStringEnumMemberName("report")]
    Report,

    [JsonStringEnumMemberName("generated-app")]
    GeneratedApp,

    [JsonStringEnumMemberName("open-data")]
    OpenData,
}
