// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// The paged GetFeature fast path follows the reader the publication resolves to.
/// A non-paging source reader must stay on the ordinary query path (#6356).
/// </summary>
public sealed class Wfs20PagedReaderFastPathTests
{
    [UnitTest]
    public void ReaderSupportsPagedGetFeatureFastPath_NonPagingReader_IsFalse()
    {
        var reader = Substitute.For<IFeatureReader>();

        Wfs20Handler.ReaderSupportsPagedGetFeatureFastPath(reader, Wfs20Utilities.OutputFormats.Csv)
            .Should().BeFalse();
        Wfs20Handler.ReaderSupportsPagedGetFeatureFastPath(reader, Wfs20Utilities.OutputFormats.GeoJson)
            .Should().BeFalse();
        Wfs20Handler.ReaderSupportsPagedGetFeatureFastPath(reader, Wfs20Utilities.OutputFormats.Json)
            .Should().BeFalse();
    }

    [UnitTest]
    public void ReaderSupportsPagedGetFeatureFastPath_PagingReader_MatchesFormat()
    {
        var reader = Substitute.For<IFeatureReader, IPagedFeatureReader>();

        Wfs20Handler.ReaderSupportsPagedGetFeatureFastPath(reader, Wfs20Utilities.OutputFormats.Csv)
            .Should().BeTrue();
        Wfs20Handler.ReaderSupportsPagedGetFeatureFastPath(reader, Wfs20Utilities.OutputFormats.GeoJson)
            .Should().BeTrue();
        Wfs20Handler.ReaderSupportsPagedGetFeatureFastPath(reader, Wfs20Utilities.OutputFormats.Gml32)
            .Should().BeFalse();
    }
}
