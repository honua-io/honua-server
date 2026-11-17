// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed partial class MigrationCatalogReconcilerTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(new double[] { 6_100_000, 2_000_000, 6_000_000, 1_900_000 })]
    [InlineData(new double[] { 6_100_000, 2_000_000, 6_200_000, 2_100_000 })]
    public void Reconcile_ReversedOrTouchingExtent_DoesNotReportMismatch(double[] sourceBbox)
    {
        var outcome = MigrationCatalogReconciler.ReconcileResource(
            BuildInventoryResource(), BuildPublishedResource(), sourceBbox: sourceBbox);

        outcome.Findings.Should().NotContain(finding =>
            finding.Code == MigrationCatalogReconciliationCodes.ExtentMismatch);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null)]
    [InlineData(new double[0])]
    [InlineData(new double[] { 1, 2, 3 })]
    public void Reconcile_MissingOrMalformedSourceExtent_OmitsExtentComparison(double[]? sourceBbox)
    {
        var outcome = MigrationCatalogReconciler.ReconcileResource(
            BuildInventoryResource(), BuildPublishedResource(), sourceBbox: sourceBbox);

        outcome.Findings.Should().NotContain(finding =>
            finding.Code == MigrationCatalogReconciliationCodes.ExtentMismatch ||
            finding.Code == MigrationCatalogReconciliationCodes.ExtentMissing);
    }
}
