// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Admin;

public sealed class AdminListQueryTests
{
    [UnitTest]
    public void MissingQuery_UsesTheFirstPageOfOneHundred()
    {
        var parsed = Parse(new QueryCollection());

        parsed.Page.Should().Be(CatalogListLimits.DefaultPage);
        parsed.PageSize.Should().Be(CatalogListLimits.DefaultPageSize);
        parsed.NameQuery.Should().BeNull();
        parsed.FolderId.Should().BeEmpty();
    }

    [UnitTest]
    public void BlankNameQuery_IsNoFilter()
    {
        var parsed = Parse(Query(("q", "   ")));

        parsed.NameQuery.Should().BeNull();
    }

    [UnitTest]
    public void FolderId_IsPreserved()
    {
        var parsed = Parse(Query(("folderId", "folder-1"), ("page", "2")));

        parsed.FolderId.Should().Be("folder-1");
        parsed.Page.Should().Be(2);
    }

    [UnitTest]
    public void UnknownKeys_AreIgnored()
    {
        var parsed = Parse(Query(("serviceName", "default"), ("q", " roads ")));

        parsed.NameQuery.Should().Be("roads");
        parsed.Page.Should().Be(1);
    }

    [UnitTest]
    public void PageSizeAboveTheCap_IsRejected()
    {
        Rejects(Query(("pageSize", "101")), "pageSize");
    }

    [UnitTest]
    public void PageBelowOne_IsRejected()
    {
        Rejects(Query(("page", "0")), "page");
    }

    [UnitTest]
    public void PageSizeBelowOne_IsRejected()
    {
        Rejects(Query(("pageSize", "0")), "pageSize");
    }

    [UnitTest]
    public void PageThatOverflowsTheOffset_IsRejected()
    {
        Rejects(Query(("page", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("pageSize", "100")), "page");
    }

    [UnitTest]
    public void NameQueryOverTheLengthCap_IsRejected()
    {
        Rejects(Query(("q", new string('a', CatalogListLimits.MaxNameQueryLength + 1))), "q");
    }

    [UnitTest]
    public void NonIntegerPage_IsRejected()
    {
        Rejects(Query(("page", "one")), "page");
    }

    private static AdminListQuery Parse(IQueryCollection query)
    {
        AdminListQueryParser.TryParse(query, out var parsed, out var error).Should().BeTrue(error);
        return parsed;
    }

    private static void Rejects(IQueryCollection query, string key)
    {
        AdminListQueryParser.TryParse(query, out _, out var error).Should().BeFalse();
        error.Should().Contain(key);
    }

    private static QueryCollection Query(params (string Key, string Value)[] pairs)
        => new(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));
}
