// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.OData.Services;

namespace Honua.Server.Tests.Features.OData;

public sealed class ODataSearchServiceSqlTests
{
    [Fact]
    public void BuildTextSearchCondition_EscapesFieldNamesWithQuotes()
    {
        var fields = new[]
        {
            new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
            new FieldDefinition("O'Reilly", FieldType.String)
        };

        var layer = new LayerDefinition(
            Id: 1,
            Name: "Test",
            Description: null,
            GeometryType.None,
            SpatialReference.WGS84,
            fields);

        var terms = new List<List<(string term, bool isNegated, bool isPhrase)>>
        {
            new() { ("Seattle", false, false) }
        };

        var method = typeof(ODataSearchService).GetMethod(
            "BuildTextSearchCondition",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var condition = (string)method!.Invoke(null, new object[] { terms, layer })!;

        condition.Should().Contain("attributes->>'O''Reilly'");
        condition.Should().NotContain("attributes->>'O'Reilly'");
    }
}
