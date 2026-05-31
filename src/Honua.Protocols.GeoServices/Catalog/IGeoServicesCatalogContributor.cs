// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.GeoServices.Catalog;

/// <summary>
/// Contributes additional entries to the Esri GeoServices services directory at
/// <c>/rest/services</c>. Lets non-metadata-graph protocols (GeocodeServer,
/// future GPServer/NAServer surfaces, etc.) appear in the directory alongside
/// FeatureServer/MapServer/ImageServer entries projected from the metadata graph.
/// </summary>
internal interface IGeoServicesCatalogContributor
{
    /// <summary>
    /// Returns directory entries the contributor wants advertised under
    /// <c>/rest/services</c>. Implementations should honor the caller's
    /// configuration (enablement, locator/service name, access policy) and
    /// resolve the base URL through the supplied <see cref="HttpContext"/>.
    /// </summary>
    ValueTask<IReadOnlyList<ServiceDirectoryEntry>> GetServicesAsync(
        HttpContext context,
        CancellationToken cancellationToken);
}
