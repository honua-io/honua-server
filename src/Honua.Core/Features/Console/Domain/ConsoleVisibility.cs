// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Sharing scope for a Console content item.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleVisibility>))]
public enum ConsoleVisibility
{
    /// <summary>
    /// Only the owner can see the item.
    /// </summary>
    [JsonStringEnumMemberName("personal")]
    Personal,

    /// <summary>
    /// Members of the owner's team scope can see the item.
    /// </summary>
    [JsonStringEnumMemberName("team")]
    Team,

    /// <summary>
    /// Everyone in the organization can see the item.
    /// </summary>
    [JsonStringEnumMemberName("organization")]
    Organization,

    /// <summary>
    /// The item is publicly readable (anonymous access permitted where the
    /// surface supports it).
    /// </summary>
    [JsonStringEnumMemberName("public")]
    Public,
}
