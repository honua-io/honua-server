// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

// A public-link tier never changes membership visibility.
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleShareAccessTier>))]
public enum ConsoleShareAccessTier
{
    [JsonStringEnumMemberName("private")]
    Private,

    [JsonStringEnumMemberName("organization")]
    Organization,

    [JsonStringEnumMemberName("public-link")]
    PublicLink,

    [JsonStringEnumMemberName("public-indexed")]
    PublicIndexed,
}
