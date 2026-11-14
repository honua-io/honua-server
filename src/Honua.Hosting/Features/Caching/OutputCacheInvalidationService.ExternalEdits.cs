// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.SharedCaching;

namespace Honua.Infrastructure.Caching;

internal sealed partial class OutputCacheInvalidationService
{
    internal async Task InvalidateExternalEditAsync(
        int layerId, BoundingBox? extent, string changeId, CancellationToken cancellationToken)
    {
        await EvictStorageLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (_cdnInvalidation is null)
        {
            return;
        }

        var paths = await CdnPathsForEditedStorageLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (extent is { } bounds && _cdnInvalidation.TagsEnabled && !paths.Contains("/*", StringComparer.Ordinal))
        {
            paths = RegionalExternalEditPaths(layerId, bounds, paths);
        }

        var result = await _cdnInvalidation.InvalidateNowAsync(paths, cancellationToken, CdnPurgeCause.Edit(changeId))
            .ConfigureAwait(false);
        if (result.Failed)
        {
            throw new InvalidOperationException("External edit CDN invalidation has not completed.");
        }
    }

    internal static string[] RegionalExternalEditPaths(int layerId, BoundingBox bounds, string[] paths)
    {
        var tileScope = "#honua-scope:tiles/" + layerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var regional = paths.Where(path => !path.Equals(tileScope, StringComparison.Ordinal))
            .Concat(CdnTileRegionTags.Invalidations(layerId, bounds))
            .Append("#" + CdnTileRegionTags.Metadata(layerId))
            .Distinct(StringComparer.Ordinal).ToArray();
        return regional.Length <= CdnTileRegionTags.MaxInvalidations ? regional : CdnPaths.Everything;
    }
}
