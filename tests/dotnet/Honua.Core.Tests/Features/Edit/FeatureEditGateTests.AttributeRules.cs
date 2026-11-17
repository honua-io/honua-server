// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Core.Tests.Features.Edit;

public sealed partial class FeatureEditGateTests
{
    [UnitTest]
    public async Task AttributeRules_CalculationsFeedContingentsAndHooks_WithoutCopyingStoredFieldsToTheWrite()
    {
        var partial = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty.Add("name", "renamed")) with
        {
            ExplicitAttributeRemovals = ["size"],
        };
        var batch = FeatureEditBatch.Create(updates: [partial]);
        var check = new FeatureEditCheck(Rules(CalculateSize()), Scope, batch)
        {
            EffectiveAttributes = static row => Row("stored", "L").Attributes.SetItems(row.Feature!.Value.Attributes),
        };
        var prepared = FeatureEditGate.ApplyAttributeRules(check);
        var hooks = new RejectingHooks("none");
        var rejections = await FeatureEditGate.CheckAsync(hooks, check with
        {
            Batch = prepared.Batch,
            Rejections = prepared.Rejections,
        }, CancellationToken.None);

        rejections.Should().BeEmpty();
        var written = prepared.Batch.Updates.Should().ContainSingle().Subject;
        written.Attributes.Should().Contain("size", "S").And.NotContainKey("color");
        written.ExplicitAttributeRemovals.Should().BeEmpty();
        hooks.Seen.Should().ContainSingle().Which.Updates[0].Attributes.Should().Contain("size", "S").And.Contain("color", "red");
    }

    [UnitTest]
    public async Task AttributeRules_PartialFailures_KeepOriginalIndexesAcrossRuleContingentAndHookRejections()
    {
        var batch = FeatureEditBatch.Create(creates:
        [Row("rule", "S"), Row("contingent", "L"), Row("blocked", "S"), Row("ok", "S")]);
        var check = new FeatureEditCheck(Rules(new MetadataV2AttributeRule
        {
            Name = "allowed-name",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "$feature.name != 'rule'",
            ErrorMessage = "rule rejected",
            IsEnabled = true,
        }), Scope, batch);
        var prepared = FeatureEditGate.ApplyAttributeRules(check);
        var hooks = new RejectingHooks("blocked");

        var rejected = await FeatureEditGate.CheckAsync(hooks, check with
        {
            Batch = prepared.Batch,
            Rejections = prepared.Rejections,
        }, CancellationToken.None);

        rejected.Select(static row => row.Index).Should().Equal(0, 1, 2);
        hooks.Seen.Should().ContainSingle().Which.Creates.Select(Name).Should().Equal("blocked", "ok");
    }

    [UnitTest]
    public async Task AttributeRules_DeleteConstraint_UsesStoredAttributesAndRollbackSkipsHooks()
    {
        var batch = FeatureEditBatch.Create(deletes: [7], rollbackOnFailure: true);
        var check = new FeatureEditCheck(Rules(new MetadataV2AttributeRule
        {
            Name = "keep-red",
            Type = MetadataV2AttributeRuleType.Constraint,
            ScriptExpression = "$feature.color != 'red'",
            TriggeringEvents = ["delete"],
            IsEnabled = true,
        }), Scope, batch)
        { EffectiveAttributes = static _ => Row("stored", "S").Attributes };
        var prepared = FeatureEditGate.ApplyAttributeRules(check);
        var hooks = new RejectingHooks("none");

        var rejected = await FeatureEditGate.CheckAsync(hooks, check with { Rejections = prepared.Rejections }, CancellationToken.None);

        rejected.Should().ContainSingle().Which.Kind.Should().Be(FeatureEditOperationKind.Delete);
        hooks.Seen.Should().BeEmpty();
    }

    [UnitTest]
    public void AttributeRules_OrderedOperations_CalculationsUpdateBothBatchShapes()
    {
        var row = Row("item", "L");
        var batch = FeatureEditBatch.Create(creates: [row], operations: [FeatureEditOperation.Delete(7), FeatureEditOperation.Create(row)]);
        var prepared = FeatureEditGate.ApplyAttributeRules(new FeatureEditCheck(Rules(CalculateSize()), Scope, batch));

        prepared.Batch.Creates[0].Attributes["size"].Should().Be("S");
        prepared.Batch.Operations[1].Feature!.Value.Attributes["size"].Should().Be("S");
        prepared.Batch.Operations[0].ObjectId.Should().Be(7);
    }

    [UnitTest]
    public async Task AttributeRules_RejectedPartialUpdate_DoesNotShiftEffectiveSnapshotIndexesForHooks()
    {
        var batch = FeatureEditBatch.Create(updates: [Row("blocked", "S"), Row("ok", "S")]);
        var check = new FeatureEditCheck(Resource(), Scope, batch)
        {
            Rejections = [new(FeatureEditOperationKind.Update, 0, "rule rejected")],
            EffectiveAttributes = static row => row.Feature!.Value.Attributes.SetItem("snapshot-index", row.Index),
        };
        var hooks = new RejectingHooks("none");

        await FeatureEditGate.CheckAsync(hooks, check, CancellationToken.None);

        hooks.Seen.Should().ContainSingle().Which.Updates.Single().Attributes["snapshot-index"].Should().Be(1);
    }

    [UnitTest]
    public async Task AttributeRules_OrderedPartialUpdate_HooksSeeEffectiveValuesInBothBatchShapes()
    {
        var row = Row("item", "L");
        var batch = FeatureEditBatch.Create(updates: [row], operations: [FeatureEditOperation.Update(row)]);
        var check = new FeatureEditCheck(Rules(CalculateSize()), Scope, batch)
        {
            EffectiveAttributes = static row => row.Feature!.Value.Attributes.SetItem("stored-only", true),
        };
        var prepared = FeatureEditGate.ApplyAttributeRules(check);
        var hooks = new RejectingHooks("none");

        await FeatureEditGate.CheckAsync(hooks, check with { Batch = prepared.Batch }, CancellationToken.None);

        var seen = hooks.Seen.Should().ContainSingle().Subject;
        seen.Updates.Single().Attributes.Should().Contain("stored-only", true).And.Contain("size", "S");
        seen.Operations.Single().Feature!.Value.Attributes.Should().Contain("stored-only", true).And.Contain("size", "S");
    }

    [UnitTest]
    public void AttributeRules_UnsupportedExpression_WarnsAndPreservesTheSubmittedWrite()
    {
        var rule = CalculateSize() with { ScriptExpression = "FeatureSetByName($datastore, 'other')" };
        var resource = Rules(rule);
        var batch = FeatureEditBatch.Create(creates: [Row("item", "S")]);
        var warnings = Substitute.For<IUnsupportedExpressionSink>();

        var prepared = FeatureEditGate.ApplyAttributeRules(new FeatureEditCheck(resource, Scope, batch)
        {
            UnsupportedExpressions = warnings,
        });

        prepared.Batch.Should().Be(batch);
        prepared.Rejections.Should().BeEmpty();
        warnings.Received(1).OnUnsupported(resource, rule);
    }

    [UnitTheory]
    [InlineData("split")]
    [InlineData("ordered")]
    public void AttributeRules_UpdateAndDeleteSameTarget_RejectBothRowsAndPreserveUnrelatedRows(string shape)
    {
        var row = Row("changed", "L") with { Id = 7 };
        var batch = FeatureEditBatch.Create(creates: [Row("new", "L")], updates: [row], deletes: [7],
            operations: shape == "ordered"
                ? [FeatureEditOperation.Update(row), FeatureEditOperation.Delete(7), FeatureEditOperation.Create(Row("new", "L"))]
                : []);

        var prepared = FeatureEditGate.ApplyAttributeRules(new FeatureEditCheck(Rules(CalculateSize()), Scope, batch));

        prepared.Rejections.Should().HaveCount(2);
        prepared.Rejections.Should().Contain(rejection => rejection.Kind == FeatureEditOperationKind.Update && rejection.Index == 0);
        prepared.Rejections.Should().Contain(rejection => rejection.Kind == FeatureEditOperationKind.Delete && rejection.Index == 0);
        prepared.Batch.Creates.Single().Attributes["size"].Should().Be("S");
    }

    [UnitTheory]
    [InlineData("range")]
    [InlineData("null")]
    [InlineData("type")]
    [InlineData("length")]
    public async Task AttributeRules_InvalidCalculatedValue_RejectsItsRowBeforeHooks(string invalid)
    {
        var resource = InvalidCalculatedValueRules(invalid);
        var batch = FeatureEditBatch.Create(creates:
        [Row("bad", "S") with { Attributes = Row("bad", "S").Attributes.Add("n", 6) }]);
        var check = new FeatureEditCheck(resource, Scope, batch);
        var prepared = FeatureEditGate.ApplyAttributeRules(check);
        var hooks = new RejectingHooks("none");

        var rejected = await FeatureEditGate.CheckAsync(hooks, check with
        {
            Batch = prepared.Batch,
            Rejections = prepared.Rejections,
        }, CancellationToken.None);

        rejected.Should().ContainSingle().Which.Index.Should().Be(0);
        prepared.Batch.Creates[0].Attributes["size"].Should().Be("S");
        hooks.Seen.Should().BeEmpty();
    }

    private static MetadataV2Object InvalidCalculatedValueRules(string invalid)
    {
        var field = new MetadataV2Field
        {
            Name = "size",
            Type = MetadataV2FieldType.String,
            Nullable = false,
            Length = 1,
        };
        var expression = invalid switch
        {
            "range" => "$feature.n * 2",
            "null" => "return null;",
            "type" => "return 12;",
            _ => "return 'too long';",
        };
        if (invalid == "range")
        {
            field = field with
            {
                Type = MetadataV2FieldType.Double,
                Domain = new() { Type = "range", Range = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(10)] },
            };
        }
        return Rules(CalculateSize() with { ScriptExpression = expression }) with
        {
            SchemaFields = [field],
            ContingentValueGroups = [],
        };
    }

    [UnitTest]
    public async Task AttributeRules_InvalidCalculation_RollbackSkipsHooksForValidRowsToo()
    {
        var resource = Rules(CalculateSize() with { ScriptExpression = "$feature.name" }) with
        {
            SchemaFields = [new() { Name = "size", Type = MetadataV2FieldType.String, Length = 1 }],
            ContingentValueGroups = [],
        };
        var check = new FeatureEditCheck(resource, Scope,
            FeatureEditBatch.Create(creates: [Row("invalid", "S"), Row("S", "S")], rollbackOnFailure: true));
        var prepared = FeatureEditGate.ApplyAttributeRules(check);
        var hooks = new RejectingHooks("none");

        var rejected = await FeatureEditGate.CheckAsync(hooks, check with
        {
            Batch = prepared.Batch,
            Rejections = prepared.Rejections,
        }, CancellationToken.None);

        rejected.Should().ContainSingle().Which.Index.Should().Be(0);
        hooks.Seen.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData(AttributeRuleEditEvent.Insert)]
    [InlineData(AttributeRuleEditEvent.Update)]
    public void AttributeRules_GlobalIdCalculation_RejectsWithoutReplacingIdentity(AttributeRuleEditEvent editEvent)
    {
        const string identity = "fe83cc1f-58cd-40cc-a935-d4acdd105336";
        var resource = Rules(CalculateSize() with
        {
            FieldName = "GLOBALID",
            ScriptExpression = "return 'f27137b9-ea20-40c1-a76f-cd474bd43387';",
        }) with
        {
            Editing = new() { GlobalIdField = "globalid" },
            SchemaFields = [new() { Name = "globalid", Type = MetadataV2FieldType.Uuid }],
        };
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("globalid", identity);

        var result = AttributeRuleEngine.Apply(resource, attributes, editEvent);

        result.IsValid.Should().BeFalse();
        result.CalculatedFields.Should().BeEmpty();
        result.Attributes["globalid"].Should().Be(identity);
        MetadataV2GraphValidator.Validate(new() { Resources = [resource] }).Errors
            .Should().Contain(error => error.Contains("cannot target the global ID field", StringComparison.Ordinal));
    }

    private static MetadataV2AttributeRule CalculateSize() => new()
    {
        Name = "size",
        Type = MetadataV2AttributeRuleType.Calculation,
        FieldName = "size",
        ScriptExpression = "return 'S';",
        IsEnabled = true,
    };

    private static MetadataV2Object Rules(params MetadataV2AttributeRule[] rules)
        => Resource() with
        {
            AttributeRules = rules,
            SchemaFields = [new() { Name = "size", Type = MetadataV2FieldType.String, Nullable = true }],
        };
}
