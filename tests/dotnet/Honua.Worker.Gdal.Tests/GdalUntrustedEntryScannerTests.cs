// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalUntrustedEntryScannerTests
{
    [UnitTest]
    public void Admit_RefusesIndirectionMarkerInOneChunk()
    {
        var scanner = new GdalUntrustedEntryScanner(64);

        var admitted = scanner.Admit(Encoding.UTF8.GetBytes("<ogrvrtdatasource>"), out var reason);

        admitted.Should().BeFalse();
        reason.Should().Contain("<OGRVRTDataSource");
    }

    [UnitTest]
    public void Admit_RefusesVsiPrefixSplitAcrossChunks()
    {
        var scanner = new GdalUntrustedEntryScanner(8);

        scanner.Admit(Encoding.UTF8.GetBytes("abc /vsi"), out _).Should().BeTrue();
        var admitted = scanner.Admit(Encoding.UTF8.GetBytes("curl/x  "), out var reason);

        admitted.Should().BeFalse();
        reason.Should().Contain("/vsicurl");
    }

    [UnitTest]
    public void Admit_AcceptsOrdinaryDataWithAngleBracketsAndSlashes()
    {
        var scanner = new GdalUntrustedEntryScanner(16);
        var text = Encoding.UTF8.GetBytes("<kml><Placemark><name>a/b</name></Placemark></kml> 1/2 <gml:pos>");

        for (var offset = 0; offset < text.Length; offset += 16)
        {
            var chunk = text.AsSpan(offset, Math.Min(16, text.Length - offset));
            scanner.Admit(chunk, out _).Should().BeTrue();
        }
    }

    [UnitTest]
    public void LongestRefusedLength_CoversEveryMarker()
    {
        GdalUntrustedInputGuard.LongestRefusedLength.Should().Be("<WMT_MS_Capabilities".Length);
    }
}
