// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac;
using Honua.Protocols.Stac.Models;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Pure contract tests for STAC search query translation. These do not require a provider
/// or container, so canonical extension-field regressions fail locally before the endpoint
/// integration suite is available.
/// </summary>
public sealed class StacSearchContractTests
{
    /// <summary>
    /// Regression test for #4147: canonical STAC fields are accepted by both extensions
    /// even when the source schema only exposes the backing temporal/id fields.
    /// </summary>
    [Fact]
    public void CanonicalFields_AreAcceptedByFieldsAndSort()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "acq_time", Type = MetadataV2FieldType.DateTime }
            ],
            Temporal = new MetadataV2ResourceTemporal { StartTimeField = "acq_time" }
        };

        var fields = new StacFieldsExtension
        {
            Includes = ImmutableArray.Create("properties.datetime")
        };

        SearchEndpoints.TryBuildFieldSelection(
            resource,
            fields,
            out _,
            out _,
            out var fieldsError).Should().BeTrue(fieldsError);

        foreach (var field in new[] { "datetime", "properties.datetime", "id" })
        {
            SearchEndpoints.TryBuildSortOrder(
                resource,
                ImmutableArray.Create(new StacSortDefinition { Field = field }),
                out _,
                out var sortError).Should().BeTrue(sortError);
        }
    }
}
