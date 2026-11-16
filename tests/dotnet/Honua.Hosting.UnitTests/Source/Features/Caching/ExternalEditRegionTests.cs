// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;

namespace Honua.Hosting.UnitTests.Features.Caching;

public sealed class ExternalEditRegionTests
{
    [UnitTest]
    public void LocalEdit_PreservesProtocolPurgesAndTileMetadataWithoutPurgingEveryNativeTile()
    {
        var paths = OutputCacheInvalidationService.RegionalExternalEditPaths(3,
            BoundingBox.FromArray([-112d, 33d, -111d, 34d], 4326),
            ["#honua-scope:tiles/3", "#honua-scope:ogc", "#honua-scope:stac", "#honua-scope:rest/services/alpha"]);

        paths.Should().Contain(["#honua-scope:ogc", "#honua-scope:stac", "#honua-scope:rest/services/alpha",
            "#" + CdnTileRegionTags.Metadata(3)]);
        paths.Should().NotContain("#honua-scope:tiles/3");
        paths.Should().NotContain("/*");
        paths.Should().HaveCountLessThanOrEqualTo(CdnTileRegionTags.MaxInvalidations);
    }

    [UnitTest]
    public void ManyPublishedAliases_FallsBackToOneCompletePurge()
    {
        var paths = OutputCacheInvalidationService.RegionalExternalEditPaths(3,
            BoundingBox.FromArray([-112d, 33d, -111d, 34d], 4326),
            Enumerable.Range(0, 50).Select(index => "#honua-scope:rest/services/alias" + index).ToArray());

        paths.Should().Equal("/*");
    }
}
