// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData.Services;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed class ODataSearchServiceSqlTests
{
    [Fact]
    public void BuildTextSearchCondition_EscapesFieldNamesWithQuotes()
    {
        var fields = new[]
        {
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "O'Reilly", Type = MetadataV2FieldType.String }
        };

        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-test", Name = "Test" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields = fields
        };

        var terms = new List<List<(string term, bool isNegated, bool isPhrase)>>
        {
            new() { ("Seattle", false, false) }
        };

        var method = typeof(ODataSearchService).GetMethod(
            "BuildTextSearchCondition",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var fragment = (SqlFragment)method!.Invoke(null, new object[] { terms, resource })!;

        fragment.Sql.Should().Contain("attributes->>'O''Reilly'");
        fragment.Sql.Should().NotContain("attributes->>'O'Reilly'");
    }
}
