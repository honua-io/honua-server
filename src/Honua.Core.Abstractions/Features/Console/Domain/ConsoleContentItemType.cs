// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Item types surfaced through the Honua Console catalog API.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleContentItemType>))]
public enum ConsoleContentItemType
{
    /// <summary>
    /// A published service (Map/Feature/Tile/etc.).
    /// </summary>
    [JsonStringEnumMemberName("service")]
    Service,

    /// <summary>
    /// A layer exposed via a service or open-data endpoint.
    /// </summary>
    [JsonStringEnumMemberName("layer")]
    Layer,

    /// <summary>
    /// A saved map composition.
    /// </summary>
    [JsonStringEnumMemberName("saved-map")]
    SavedMap,

    /// <summary>
    /// A dashboard composed of widgets and data sources.
    /// </summary>
    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    /// <summary>
    /// A report template or generated report instance.
    /// </summary>
    [JsonStringEnumMemberName("report")]
    Report,

    /// <summary>
    /// A generated application surface produced by Studio.
    /// </summary>
    [JsonStringEnumMemberName("generated-app")]
    GeneratedApp,

    /// <summary>
    /// An open-data catalog item with public distribution links.
    /// </summary>
    [JsonStringEnumMemberName("open-data")]
    OpenData,
}
