// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

public sealed partial class OgcFeaturesEditGateTests
{
    [UnitTest]
    public async Task Replacement_ValidatesPreservedProtectedFields_AndBindsTheSnapshotToTheWrite()
    {
        foreach (var color in new[] { "red", "blue" })
        {
            foreach (var transaction in new[] { false, true })
            {
                var reader = Substitute.For<IFeatureReader>();
                var snapshot = Row(42, "red", "S") with
                {
                    Attributes = Row(42, "red", "S").Attributes.Add("other", "stored editable value"),
                };
                reader.QueryAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                    .Returns(QueryResult<Feature>.Create(1, [snapshot]));
                var writer = new CountingWriter();
                var hooks = new RecordingHooks();
                var resource = ResourceWithNullSize();
                resource = resource with
                {
                    SchemaFields = [.. resource.SchemaFields.Select(field => field.Name == "size" ? field with { Editable = false } : field),
                        new MetadataV2Field { Name = "other", Type = MetadataV2FieldType.String, Nullable = true, Editable = true }],
                };
                var request = new OgcFeaturesEditRequest
                {
                    Operation = OgcFeaturesEditOperation.Replace,
                    ObjectId = 42,
                    Feature = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty.Add("color", color)),
                };
                var handlers = CreateHandlers(writer, reader);
                var result = transaction
                    ? await handlers.Transaction.ExecuteEditAsync(Context(hooks), 7, resource, request, CancellationToken.None)
                    : await handlers.Crud.ExecuteEditAsync(Context(hooks), 7, resource, request, CancellationToken.None);

                result.IsSuccess.Should().Be(color == "red", "PUT preserves protected size S but removes omitted editable fields");
                writer.ApplyCount.Should().Be(color == "red" ? 1 : 0);
                if (color == "red")
                {
                    var validated = hooks.BeforeBatches.Should().ContainSingle().Subject.Updates.Should().ContainSingle().Subject;
                    validated.Attributes.Should().Contain("size", "S").And.NotContainKey("other");
                    var batch = writer.LastBatch!.Value;
                    batch.Updates.Should().ContainSingle().Which.Attributes.Should().NotContainKey("size");
                    var condition = batch.Preconditions.Should().ContainSingle().Subject;
                    condition.ExpectedStateToken.Should().Be(FeatureStateToken.FromReadSnapshot(snapshot));
                    condition.MaskedFields.Should().Contain("size");
                }
            }
        }
    }
}
