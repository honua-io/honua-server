// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Protocols.GeoServices.VectorTileServer.Services;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// Unit tests for the VectorTileServer glyph ranges (#5535): a served fontstack is answered
/// with the bundled face's glyphs under the requested name; an unserved fontstack or range is
/// refused rather than answered with an empty glyph stack.
/// </summary>
public sealed class VectorTileEmbeddedAssetsTests
{
    [UnitTheory]
    [InlineData("Arial Regular")]
    [InlineData("Arial Unicode MS Bold")]
    [InlineData("Open Sans Semibold")]
    [InlineData("Noto Sans Italic")]
    [InlineData("Honua Default")]
    [InlineData("DejaVu Sans Book")]
    [InlineData("sans-serif")]
    [InlineData("Unknown Face Regular, Roboto Medium")]
    public void Issue5535_SansSerifFontstack_IsServed(string fontstack)
        => VectorTileEmbeddedAssets.IsServedFontStack(fontstack).Should().BeTrue();

    [UnitTheory]
    [InlineData("")]
    [InlineData(" , ")]
    [InlineData("Unknown Face Regular")]
    [InlineData("Arialish Regular")]
    [InlineData("Times New Roman Regular")]
    public void Issue5535_UnknownFontstack_IsNotServed(string fontstack)
    {
        VectorTileEmbeddedAssets.IsServedFontStack(fontstack).Should().BeFalse();
        VectorTileEmbeddedAssets.TryGetGlyphPbf(fontstack, "0-255", out var glyphPbf).Should().BeFalse();
        glyphPbf.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("256-511")]
    [InlineData("99-1234")]
    [InlineData("0-255.pbf")]
    public void Issue5535_UnbundledRange_IsNotServed(string range)
        => VectorTileEmbeddedAssets.TryGetGlyphPbf("Arial Regular", range, out _).Should().BeFalse();

    [UnitTest]
    public void Issue5535_GlyphPbf_CarriesTheRequestedFontstackAndEveryPrintableLatin1Glyph()
    {
        VectorTileEmbeddedAssets.TryGetGlyphPbf("Arial Regular", "0-255", out var glyphPbf).Should().BeTrue();

        var payload = glyphPbf!;
        var offset = 0;
        ReadVarint(payload, ref offset).Should().Be(0x0A, "the payload is a single fontstack message");
        var stackEnd = (int)ReadVarint(payload, ref offset) + offset;
        stackEnd.Should().Be(payload.Length);

        string? name = null;
        string? range = null;
        var glyphIds = new List<int>();
        while (offset < stackEnd)
        {
            var key = ReadVarint(payload, ref offset);
            var length = (int)ReadVarint(payload, ref offset);
            var value = payload.AsSpan(offset, length);
            offset += length;
            switch (key)
            {
                case 0x0A:
                    name = Encoding.UTF8.GetString(value);
                    break;
                case 0x12:
                    range = Encoding.UTF8.GetString(value);
                    break;
                case 0x1A:
                    var glyphOffset = 0;
                    var glyph = value.ToArray();
                    ReadVarint(glyph, ref glyphOffset).Should().Be(0x08, "a glyph starts with its id");
                    glyphIds.Add((int)ReadVarint(glyph, ref glyphOffset));
                    break;
                default:
                    throw new InvalidDataException($"Unexpected fontstack field key {key}.");
            }
        }

        name.Should().Be("Arial Regular");
        range.Should().Be("0-255");
        var printable = Enumerable.Range(0x20, 0x7F - 0x20).Concat(Enumerable.Range(0xA0, 0x100 - 0xA0));
        glyphIds.Should().BeEquivalentTo(printable);
    }

    private static ulong ReadVarint(byte[] payload, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var current = payload[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }
    }
}
