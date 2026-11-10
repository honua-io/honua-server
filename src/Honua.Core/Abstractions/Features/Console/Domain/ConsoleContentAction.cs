// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

// Clients never declare a verb allow-list; the server resolves which verbs the principal may use.
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleContentAction>))]
public enum ConsoleContentAction
{
    [JsonStringEnumMemberName("view")]
    View,

    [JsonStringEnumMemberName("edit")]
    Edit,

    [JsonStringEnumMemberName("publish")]
    Publish,

    [JsonStringEnumMemberName("share")]
    Share,

    [JsonStringEnumMemberName("embed")]
    Embed,

    [JsonStringEnumMemberName("operate")]
    Operate,

    [JsonStringEnumMemberName("administer")]
    Administer,
}
