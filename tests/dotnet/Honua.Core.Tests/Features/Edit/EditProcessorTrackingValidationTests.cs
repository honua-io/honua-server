// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Edit;

public sealed class EditProcessorTrackingValidationTests
{
    [UnitTest]
    public void Create_TrackingFieldsAreServerSupplied_OrdinaryRequiredFieldsRemainRequired()
    {
        var resource = new MetadataV2Object
        {
            Editing = new() { CreatorField = "CREATOR", CreatedAtField = "CREATED", EditorField = "EDITOR", UpdatedAtField = "UPDATED" },
            SchemaFields = [
                new() { Name = "creator", Type = MetadataV2FieldType.String },
                new() { Name = "created", Type = MetadataV2FieldType.DateTime },
                new() { Name = "editor", Type = MetadataV2FieldType.String },
                new() { Name = "updated", Type = MetadataV2FieldType.DateTime }],
        };
        var empty = ImmutableDictionary<string, object?>.Empty;
        EditProcessor.DescribeCreateAttributeRejection(resource, empty).Should().BeNull();
        EditProcessor.DescribeCreateAttributeRejection(resource, empty.Add("extra", "value")).Should().BeNull();

        resource = resource with { SchemaFields = [.. resource.SchemaFields, new() { Name = "name", Type = MetadataV2FieldType.String }] };
        EditProcessor.DescribeCreateAttributeRejection(resource, empty).Should().Be("Attributes required for create operation");
        EditProcessor.DescribeCreateAttributeRejection(resource, empty.Add("extra", "value"))
            .Should().Be("Required attribute(s) missing for create operation: name");
        EditProcessor.DescribeCreateAttributeRejection(resource, empty.Add("name", "value")).Should().BeNull();
    }
}
