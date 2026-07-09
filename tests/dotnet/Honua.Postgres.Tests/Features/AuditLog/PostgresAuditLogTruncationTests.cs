// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.AuditLog;

namespace Honua.Postgres.Tests.Features.AuditLog;

/// <summary>
/// Docker-free coverage of <see cref="PostgresAuditLog.Truncate"/>. Guards the
/// truncation-marker encoding contract: an over-long value must be truncated to
/// the column width and end with a real horizontal ellipsis (U+2026), not the
/// three-code-point mojibake sequence produced by a bad UTF-8/Latin-1 round trip.
/// </summary>
public sealed class PostgresAuditLogTruncationTests
{
    private const string Ellipsis = "\u2026";

    [Fact]
    public void Truncate_OverlongAsciiValue_TruncatesToMaxAndEndsWithEllipsis()
    {
        var value = new string('A', 1024);

        var result = PostgresAuditLog.Truncate(value, 256);

        result.Length.Should().Be(256);
        result.Should().EndWith(Ellipsis);
    }

    [Fact]
    public void Truncate_MarkerIsSingleEllipsisChar_NotMojibake()
    {
        var result = PostgresAuditLog.Truncate(new string('A', 300), 256);

        // The final char must be the real ellipsis code point, and must not be
        // the 3-char Latin-1 mis-decoding.
        result[^1].Should().Be('\u2026');
        result.Should().NotContain("\u00E2\u20AC\u00A6");
    }

    [Fact]
    public void Truncate_OverlongMultiByteValue_DoesNotSplitAndEndsWithEllipsis()
    {
        // Multi-byte content (each char is 3 UTF-8 bytes) exercises the char-vs-byte
        // truncation seam: char-based truncation must never corrupt a code point.
        var value = string.Concat(Enumerable.Repeat("\u4E2D", 512)); // CJK U+4E2D

        var result = PostgresAuditLog.Truncate(value, 256);

        result.Length.Should().Be(256);
        result.Should().EndWith(Ellipsis);
        // Everything before the marker must be intact CJK chars (255 of them), no split code point.
        result[..^1].Should().Be(new string('\u4E2D', 255));
    }

    [Fact]
    public void Truncate_ValueWithinLimit_ReturnedUnchanged()
    {
        const string value = "short-actor";

        PostgresAuditLog.Truncate(value, 256).Should().Be(value);
    }
}
