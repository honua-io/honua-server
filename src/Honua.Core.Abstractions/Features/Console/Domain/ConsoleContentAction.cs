// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Console RBAC verbs that may be evaluated for a content item or navigation route.
/// </summary>
/// <remarks>
/// These verbs are server-authored: clients never declare a verb-allow; they ask which
/// verbs are permitted for the requesting principal and the server resolves the answer
/// against the underlying authorization policy set.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleContentAction>))]
public enum ConsoleContentAction
{
    /// <summary>
    /// Read metadata and supporting content. Maps to <c>metadata.read</c> plus
    /// the item's visibility check.
    /// </summary>
    [JsonStringEnumMemberName("view")]
    View,

    /// <summary>
    /// Modify item metadata or definition. Maps to <c>metadata.write</c> and
    /// <c>features.edit</c> where applicable.
    /// </summary>
    [JsonStringEnumMemberName("edit")]
    Edit,

    /// <summary>
    /// Publish the item or a derived artifact. Maps to <c>catalog.publish</c>.
    /// </summary>
    [JsonStringEnumMemberName("publish")]
    Publish,

    /// <summary>
    /// Grant access to the item by changing visibility/sharing metadata.
    /// Maps to <c>metadata.write</c> against the item's sharing facet.
    /// </summary>
    [JsonStringEnumMemberName("share")]
    Share,

    /// <summary>
    /// Embed the item into an external surface (iframe, generated app). Maps to
    /// <c>metadata.read</c> plus the item's embed flag.
    /// </summary>
    [JsonStringEnumMemberName("embed")]
    Embed,

    /// <summary>
    /// Operate the item at runtime — issue queries, render rasters, run reports.
    /// Maps to service-specific operate actions (<c>features.query</c>,
    /// <c>raster.render</c>, …).
    /// </summary>
    [JsonStringEnumMemberName("operate")]
    Operate,

    /// <summary>
    /// Administer the item or surface — manage roles, retention, lifecycle.
    /// Maps to <c>admin.rbac.write</c>.
    /// </summary>
    [JsonStringEnumMemberName("administer")]
    Administer,
}
