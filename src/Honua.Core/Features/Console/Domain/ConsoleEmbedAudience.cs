// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Scope of an embed authorization — what the redeeming surface is permitted to
/// render.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleEmbedAudience>))]
public enum ConsoleEmbedAudience
{
    /// <summary>
    /// Embed renders an interactive map composition.
    /// </summary>
    [JsonStringEnumMemberName("map")]
    Map,

    /// <summary>
    /// Embed renders non-map content (dashboard, report, generated app surface).
    /// </summary>
    [JsonStringEnumMemberName("content")]
    Content,
}
