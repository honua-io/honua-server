// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Catalog date predicates compare on the UTC timeline, and NULL never satisfies
/// a comparison (including <c>&lt;&gt;</c>, <c>&lt;</c> and <c>&lt;=</c>).
/// </summary>
public sealed class ImageServerCatalogDateFilterTests
{
    [UnitTest]
    public void Apply_DateLiteralComparisons_AreChronologicalAndExcludeNulls()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        try
        {
            var evaluator = new ImageServerCatalogFilterEvaluator();

            evaluator.Apply(Items(), "AcquisitionDate > DATE '2023-06-01'")
                .Select(item => item.ObjectId).Should().Equal(2);
            evaluator.Apply(Items(), "AcquisitionDate = DATE '2024-01-15'")
                .Select(item => item.ObjectId).Should().Equal(2);
            evaluator.Apply(Items(), "AcquisitionDate <> DATE '2024-01-15'")
                .Select(item => item.ObjectId).Should().Equal(1);
            evaluator.Apply(Items(), "AcquisitionDate < DATE '2023-06-01'")
                .Select(item => item.ObjectId).Should().Equal(1);
            evaluator.Apply(Items(), "AcquisitionDate <= DATE '2023-01-01'")
                .Select(item => item.ObjectId).Should().Equal(1);
            evaluator.Apply(Items(), "AcquisitionDate IS NULL")
                .Select(item => item.ObjectId).Should().Equal(3);
            evaluator.Apply(Items(), "AcquisitionDate IS NOT NULL")
                .Select(item => item.ObjectId).Should().Equal(1, 2);
            evaluator.Apply(Items(), "AcquisitionDate > TIMESTAMP '2023-06-01 00:00:00'")
                .Select(item => item.ObjectId).Should().Equal(2);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static ImageServerCatalogItem[] Items() =>
    [
        new()
        {
            ObjectId = 1,
            Name = "early",
            PixelType = "8BUI",
            AcquisitionDate = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
        },
        new()
        {
            ObjectId = 2,
            Name = "late",
            PixelType = "8BUI",
            AcquisitionDate = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
        },
        new()
        {
            ObjectId = 3,
            Name = "undated",
            PixelType = "8BUI",
            AcquisitionDate = null,
        },
    ];
}
