// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.SensorThings.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Covers <see cref="StaQueryOptions"/> paging arithmetic at the 32-bit offset boundary.
/// The store pages on an <see cref="int"/> offset, so a continuation whose <c>$skip</c>
/// exceeded <see cref="int.MaxValue"/> used to be emitted anyway; following it failed to
/// parse and silently reset <c>Skip</c> to zero, restarting pagination from the first
/// page (duplicating rows or looping forever).
/// </summary>
public sealed class StaQueryOptionsPagingTests
{
    private static StaQueryOptions Parse(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        return StaQueryOptions.FromRequest(context.Request);
    }

    [UnitTest]
    public void NextSkip_WithinInt32Range_AdvancesByTop()
    {
        var options = Parse("?$skip=2&$top=2");

        options.Skip.Should().Be(2);
        options.Top.Should().Be(2);
        options.NextSkip.Should().Be(4);
    }

    [UnitTest]
    public void NextSkip_AtTheLastAddressableOffset_StillAdvances()
    {
        var options = Parse($"?$skip={int.MaxValue - 1000}&$top=1000");

        options.NextSkip.Should().Be(int.MaxValue);
    }

    [UnitTest]
    public void NextSkip_WhenTheNextOffsetWouldExceedInt32_IsNull()
    {
        var options = Parse($"?$skip={int.MaxValue - 999}&$top=1000");

        options.Skip.Should().Be(int.MaxValue - 999);
        options.Top.Should().Be(1000);
        options.NextSkip.Should().BeNull();
    }

    [UnitTest]
    public void NextSkip_AtTheMaximumSkip_IsNull()
    {
        var options = Parse($"?$skip={int.MaxValue}&$top=100");

        options.NextSkip.Should().BeNull();
    }

    [UnitTest]
    public void Skip_AboveInt32Range_ClampsInsteadOfRestartingAtZero()
    {
        var options = Parse("?$skip=2147483648&$top=100");

        options.Skip.Should().Be(int.MaxValue);
        options.NextSkip.Should().BeNull();
    }

    [UnitTest]
    public void Skip_AboveInt64Range_ClampsInsteadOfRestartingAtZero()
    {
        var options = Parse("?$skip=99999999999999999999999999&$top=100");

        options.Skip.Should().Be(int.MaxValue);
        options.NextSkip.Should().BeNull();
    }

    [UnitTest]
    public void Top_AboveInt32Range_ClampsToMaxTop()
    {
        var options = Parse("?$top=4294967296");

        options.Top.Should().Be(StaQueryOptions.MaxTop);
    }

    [UnitTest]
    public void Skip_WhenNotNumeric_FallsBackToZero()
    {
        var options = Parse("?$skip=not-a-number&$top=10");

        options.Skip.Should().Be(0);
        options.NextSkip.Should().Be(10);
    }

    [UnitTest]
    public void Skip_WhenNegative_FallsBackToZero()
    {
        var options = Parse("?$skip=-5&$top=10");

        options.Skip.Should().Be(0);
    }
}
