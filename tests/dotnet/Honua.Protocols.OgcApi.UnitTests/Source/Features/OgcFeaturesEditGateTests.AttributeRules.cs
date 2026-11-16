// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

public sealed partial class OgcFeaturesEditGateTests
{
    [UnitTest]
    public async Task PartialUpdate_CalculatesFromStoredValues_WithoutCopyingThemToTheWrite()
    {
        var resource = Resource() with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "size", Type = MetadataV2AttributeRuleType.Calculation, FieldName = "size",
                ScriptExpression = "$feature.size", IsEnabled = true,
            }],
        };
        var writer = new CountingWriter();
        var hooks = new RecordingHooks();
        var request = new OgcFeaturesEditRequest
        {
            Operation = OgcFeaturesEditOperation.Patch, ObjectId = 42,
            Feature = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty),
        };

        var result = await CreateHandlers(writer).Crud.ExecuteEditAsync(Context(hooks), 7, resource, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        writer.LastBatch!.Value.Updates.Single().Attributes.Should().Contain("size", "S").And.NotContainKey("color");
        hooks.BeforeBatches.Should().ContainSingle().Which.Updates.Single().Attributes.Should().Contain("color", "red");
    }

    [UnitTest]
    public async Task Calculation_PrecedesContingentsAndHooks_ForItemAndTransactionWrites()
    {
        var resource = Resource() with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "size", Type = MetadataV2AttributeRuleType.Calculation, FieldName = "size",
                ScriptExpression = "return 'S';", IsEnabled = true,
            }],
        };
        foreach (var transaction in new[] { false, true })
        {
            var writer = new CountingWriter();
            var hooks = new RecordingHooks();
            var handlers = CreateHandlers(writer);
            var request = Create("red", "L");
            var result = transaction
                ? await handlers.Transaction.ExecuteEditAsync(Context(hooks), 7, resource, request, CancellationToken.None)
                : await handlers.Crud.ExecuteEditAsync(Context(hooks), 7, resource, request, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            writer.LastBatch!.Value.Creates.Single().Attributes["size"].Should().Be("S");
            hooks.BeforeBatches.Should().ContainSingle().Which.Creates.Single().Attributes["size"].Should().Be("S");
        }
    }

    [UnitTest]
    public async Task DeleteConstraint_ReadsStoredValues_AndStopsTheWholeRequestBeforeHooks()
    {
        var resource = Resource() with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "keep-red", Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "$feature.color != 'red'", TriggeringEvents = ["delete"],
                ErrorMessage = "protected row", IsEnabled = true,
            }],
        };
        var writer = new CountingWriter();
        var hooks = new RecordingHooks();
        var request = new OgcFeaturesEditRequest { Operation = OgcFeaturesEditOperation.Delete, ObjectId = 42 };

        var result = await CreateHandlers(writer).Crud.ExecuteEditAsync(Context(hooks), 7, resource, request, CancellationToken.None);

        writer.ApplyCount.Should().Be(0);
        hooks.BeforeBatches.Should().BeEmpty();
        result.DeleteResults.Should().ContainSingle().Which.ErrorMessage.Should().Be("protected row");
    }
}
