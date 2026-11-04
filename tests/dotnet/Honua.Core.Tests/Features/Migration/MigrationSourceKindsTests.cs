// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Tests.Features.Migration;

public sealed class MigrationSourceKindsTests
{
    [UnitTheory]
    [InlineData("geoservices-rest")]
    [InlineData("GeoServices-REST")]
    [InlineData("arcgis-geoservices-rest")]
    public void IsGeoServicesRest_CurrentOrLegacyValue_IsAccepted(string sourceKind)
        => MigrationSourceKinds.IsGeoServicesRest(sourceKind).Should().BeTrue();

    [UnitTheory]
    [InlineData("geoserver-rest")]
    [InlineData("")]
    [InlineData(null)]
    public void IsGeoServicesRest_OtherValue_IsRejected(string? sourceKind)
        => MigrationSourceKinds.IsGeoServicesRest(sourceKind).Should().BeFalse();
}
