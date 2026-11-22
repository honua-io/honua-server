// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

public sealed partial class OgcFeaturesEditGateTests
{
    [UnitTest]
    public async Task EditorTracking_ItemAndTransactionEdits_IgnoreForgeriesAndPreserveCreationFields()
    {
        var resource = TrackingResource();
        foreach (var transaction in new[] { false, true })
            foreach (var insert in new[] { false, true })
            {
                var writer = new CountingWriter();
                var context = Context(new RecordingHooks());
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));
                var row = Row(insert ? 0 : 42, "red", "S");
                row = row with { Attributes = row.Attributes.Add("creator", "forged").Add("editor", "forged") };
                var request = new OgcFeaturesEditRequest
                {
                    Operation = insert ? OgcFeaturesEditOperation.Create : OgcFeaturesEditOperation.Replace,
                    ObjectId = insert ? null : 42,
                    Feature = row,
                };
                var handlers = CreateHandlers(writer);
                var result = transaction
                    ? await handlers.Transaction.ExecuteEditAsync(context, 7, resource, request, CancellationToken.None)
                    : await handlers.Crud.ExecuteEditAsync(context, 7, resource, request, CancellationToken.None);

                result.IsSuccess.Should().BeTrue();
                var batch = writer.LastBatch!.Value;
                var written = insert ? batch.Creates.Single() : batch.Updates.Single();
                written.Attributes.Should().Contain("editor", "alice");
                written.Attributes["updated"].Should().BeOfType<DateTime>();
                if (insert)
                {
                    written.Attributes.Should().Contain("creator", "alice");
                    written.Attributes["created"].Should().Be(written.Attributes["updated"]);
                }
                else
                {
                    written.Attributes.Should().NotContainKey("creator").And.NotContainKey("created");
                    written.PreservedAttributeNames.Should().Contain(["creator", "created"]);
                }
            }
    }

    private static MetadataV2Object TrackingResource() => Resource() with
    {
        Editing = new() { CreatorField = "creator", CreatedAtField = "created", EditorField = "editor", UpdatedAtField = "updated" },
        SchemaFields = [.. Resource().SchemaFields,
            new() { Name = "creator", Type = MetadataV2FieldType.String, Editable = true },
            new() { Name = "created", Type = MetadataV2FieldType.DateTime, Editable = true },
            new() { Name = "editor", Type = MetadataV2FieldType.String, Editable = true },
            new() { Name = "updated", Type = MetadataV2FieldType.DateTime, Editable = true }],
    };
}
