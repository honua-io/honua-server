// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;
using Honua.Protocols.GeoServices.Catalog;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geocoding;

/// <summary>
/// Advertises the configured GeocodeServer locator under
/// <c>/rest/services</c> so Esri-style clients can discover it alongside the
/// metadata-graph-driven FeatureServer/MapServer/ImageServer entries.
/// </summary>
internal sealed class GeocodeServerCatalogContributor(IOptions<GeocodingOptions> options)
    : IGeoServicesCatalogContributor
{
    private readonly GeocodingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public ValueTask<IReadOnlyList<ServiceDirectoryEntry>> GetServicesAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.LocatorName))
        {
            return ValueTask.FromResult<IReadOnlyList<ServiceDirectoryEntry>>([]);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var escapedName = Uri.EscapeDataString(_options.LocatorName);
        var entry = new ServiceDirectoryEntry
        {
            Name = _options.LocatorName,
            Type = "GeocodeServer",
            Url = $"{baseUrl}/rest/services/{escapedName}/GeocodeServer"
        };

        return ValueTask.FromResult<IReadOnlyList<ServiceDirectoryEntry>>([entry]);
    }
}
