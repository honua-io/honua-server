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
    public void FindRefusal_RefusesIndirectionMarkerInOneChunk()
    {
        var scanner = new GdalUntrustedEntryScanner(64);

        var reason = scanner.FindRefusal(Encoding.UTF8.GetBytes("<ogrvrtdatasource>"));

        reason.Should().Contain("<OGRVRTDataSource");
    }

    [UnitTest]
    public void FindRefusal_RefusesVsiPrefixSplitAcrossChunks()
    {
        var scanner = new GdalUntrustedEntryScanner(8);

        scanner.FindRefusal(Encoding.UTF8.GetBytes("abc /vsi")).Should().BeNull();
        var reason = scanner.FindRefusal(Encoding.UTF8.GetBytes("curl/x  "));

        reason.Should().Contain("/vsicurl");
    }

    [UnitTest]
    public void FindRefusal_AcceptsOrdinaryDataWithAngleBracketsAndSlashes()
    {
        var scanner = new GdalUntrustedEntryScanner(16);
        var text = Encoding.UTF8.GetBytes("<kml><Placemark><name>a/b</name></Placemark></kml> 1/2 <gml:pos>");

        for (var offset = 0; offset < text.Length; offset += 16)
        {
            var chunk = text.AsSpan(offset, Math.Min(16, text.Length - offset));
            scanner.FindRefusal(chunk).Should().BeNull();
        }
    }

    [UnitTest]
    public async Task ScanFileAsync_RefusesPrefixAcrossTheReadBoundary()
    {
        var path = Path.Join(Path.GetTempPath(), "honua-gdal-scan-" + Guid.NewGuid().ToString("N") + ".gml");
        try
        {
            // File reads return full 64 KiB chunks; the prefix starts three bytes before the first boundary.
            var bytes = Encoding.UTF8.GetBytes(new string('a', (64 * 1024) - 3) + "/vsicurl/https://example.invalid/x");
            await File.WriteAllBytesAsync(path, bytes);

            var reason = await GdalUntrustedEntryScanner.ScanFileAsync(path, CancellationToken.None);

            reason.Should().Contain("/vsicurl");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    public void FindRefusal_RejectsChunkLargerThanTheWindow()
    {
        var scanner = new GdalUntrustedEntryScanner(4);

        var oversized = () => scanner.FindRefusal(new byte[5]);

        oversized.Should().Throw<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public void LongestRefusedLength_CoversEveryMarker()
    {
        GdalUntrustedInputGuard.LongestRefusedLength.Should().Be("<WMT_MS_Capabilities".Length);
    }
}
