// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Server.Features.Protocols.Ogc.Common;

internal static class OgcConformanceUris
{
    internal static readonly ImmutableArray<string> Common = ImmutableArray.Create(
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page",
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json",
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/html",
        "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections");

    /// <summary>
    /// Vendor extension conformance URIs for non-standard features supported by Honua.
    /// These are custom query parameters beyond the OGC baseline.
    /// </summary>
    internal static readonly ImmutableArray<string> VendorExtensions = ImmutableArray.Create(
        "http://honua.dev/spec/ogcapi-features/1.0/conf/ids-parameter",
        "http://honua.dev/spec/ogcapi-features/1.0/conf/properties-parameter",
        "http://honua.dev/spec/ogcapi-features/1.0/conf/batch-operations");
}
