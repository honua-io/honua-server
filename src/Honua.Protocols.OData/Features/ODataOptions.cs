// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.OData;

/// <summary>
/// Configuration options for the OData v4 protocol adapter.
/// </summary>
public sealed class ODataOptions
{
    /// <summary>
    /// The configuration section name that binds to these options.
    /// </summary>
    public const string SectionName = "OData";

    /// <summary>
    /// Server-driven paging cap. The effective page size is clamped to
    /// <c>min($top, MaxPageSize)</c> so a single OData request never tries to
    /// materialize an unbounded result page. When a client requests a larger
    /// <c>$top</c>, the server returns the first page (up to this size) and
    /// emits an <c>@odata.nextLink</c> so the client can page through the
    /// remaining rows. A very large <c>$top</c> on an ad hoc spatial query
    /// (for example <c>geo.intersects</c> with <c>$select</c>) otherwise
    /// forces a pathological database plan that can exceed the request budget.
    /// </summary>
    [Range(1, 10000)]
    public int MaxPageSize { get; set; } = 1000;
}
