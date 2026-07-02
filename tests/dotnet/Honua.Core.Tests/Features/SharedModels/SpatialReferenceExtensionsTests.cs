// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Xunit;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Unit coverage for <see cref="SpatialReferenceExtensions"/> SRID normalization.
/// The Web Mercator alias table (#2360) must stay in sync across the query, import,
/// and edit paths so an incoming 102100 spatial reference is treated as EPSG:3857.
/// </summary>
public sealed class SpatialReferenceExtensionsTests
{
    [Theory]
    [InlineData(102100)]
    [InlineData(102113)]
    [InlineData(900913)]
    [InlineData(3785)]
    [InlineData(3857)]
    public void NormalizeWebMercatorSrid_WebMercatorAliases_MapTo3857(int srid)
    {
        SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid).Should().Be(3857);
    }

    [Theory]
    [InlineData(4326)]
    [InlineData(4269)]
    [InlineData(2154)]
    [InlineData(32633)]
    public void NormalizeWebMercatorSrid_OtherSrids_PassThroughUnchanged(int srid)
    {
        SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid).Should().Be(srid);
    }
}
