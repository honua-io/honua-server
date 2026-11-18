// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcGlobalIdEditTests
{
    public enum FeatureEditFailurePolicy
    {
        PerRow,
        Rollback
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task Calculation_CannotOverwriteServerTrackingFields()
    {
        UseLayer(TrackedResource(MetadataV2FieldType.DateTime) with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "forged editor", Type = MetadataV2AttributeRuleType.Calculation,
                FieldName = "last_edited_user", ScriptExpression = "return 'forged';", IsEnabled = true,
            }],
        }, capabilities: null);
        ReturnWriteResult(FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)]));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature());

        await _sut.ApplyEdits(request, CallContext("insert"));

        WrittenBatch().Creates.Single().Attributes["last_edited_user"].Should().Be(Caller);
    }

    [UnitTheory]
    [InlineData("blocked")]
    [InlineData("allowed")]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task DeleteConstraint_UsesStoredValues_AndGuardsSuccessfulWrites(string name)
    {
        var stored = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty.Add("name", name));
        _featureReader.GetAsync(Arg.Any<int>(), 7, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Feature?>(stored));
        UseLayer(TrackedResource(MetadataV2FieldType.DateTime) with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "keep blocked", Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "$feature.name != 'blocked'", TriggeringEvents = ["delete"], IsEnabled = true,
            }],
        }, capabilities: null);
        ReturnWriteResult(name == "allowed"
            ? FeatureEditResult.Success(0, 0, 1, deleteResults: [EditOperationResult.Success(7)])
            : FeatureEditResult.Success(0, 0, 0));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Deletes.Add(7);

        var response = await _sut.ApplyEdits(request, CallContext("delete"));

        response.DeleteResults.Should().ContainSingle().Which.Success.Should().Be(name == "allowed");
        if (name == "blocked")
        {
            WrittenBatch().Deletes.Should().BeEmpty();
        }
        else
        {
            WrittenBatch().Preconditions.Should().ContainSingle().Which.ExpectedStateToken.Should().Be(FeatureStateToken.FromReadSnapshot(stored));
        }
    }

    [UnitTheory]
    [InlineData(FeatureEditFailurePolicy.PerRow)]
    [InlineData(FeatureEditFailurePolicy.Rollback)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ConstraintFailure_IsPerRowUnlessRollbackWasRequested(FeatureEditFailurePolicy failurePolicy)
    {
        UseLayer(TrackedResource(MetadataV2FieldType.DateTime) with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "allowed name", Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "$feature.name != 'blocked'", ErrorMessage = "blocked name", IsEnabled = true,
            }],
        }, capabilities: null);
        ReturnWriteResult(FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)]));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0, RollbackOnFailure = failurePolicy == FeatureEditFailurePolicy.Rollback };
        request.Adds.Add(new Proto.Feature { Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "blocked" } } });
        request.Adds.Add(new Proto.Feature { Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "allowed" } } });
        if (failurePolicy == FeatureEditFailurePolicy.Rollback)
        {
            var act = async () => await _sut.ApplyEdits(request, CallContext("insert"));
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
            AssertNothingWritten();
            return;
        }

        var response = await _sut.ApplyEdits(request, CallContext("insert"));

        response.AddResults.Should().HaveCount(2);
        response.AddResults[0].Success.Should().BeFalse();
        response.AddResults[1].Success.Should().BeTrue();
        WrittenBatch().Creates.Should().ContainSingle().Which.Attributes["name"].Should().Be("allowed");
    }

    [UnitTheory]
    [InlineData(FeatureEditFailurePolicy.PerRow)]
    [InlineData(FeatureEditFailurePolicy.Rollback)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task UpdateThenDelete_CannotBypassDeleteConstraintUsingTheOriginalSnapshot(FeatureEditFailurePolicy failurePolicy)
    {
        var stored = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty.Add("name", "allowed"));
        _featureReader.GetAsync(Arg.Any<int>(), 7, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Feature?>(stored));
        UseLayer(TrackedResource(MetadataV2FieldType.DateTime) with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "keep blocked", Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "$feature.name != 'blocked'", TriggeringEvents = ["delete"], IsEnabled = true,
            }],
        }, capabilities: null);
        ReturnWriteResult(FeatureEditResult.Success(0, 0, 0));
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0,
            RollbackOnFailure = failurePolicy == FeatureEditFailurePolicy.Rollback,
        };
        request.Updates.Add(new Proto.Feature
        {
            Id = 7,
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "blocked" } },
        });
        request.Deletes.Add(7);
        if (failurePolicy == FeatureEditFailurePolicy.Rollback)
        {
            var act = async () => await _sut.ApplyEdits(request, CallContext("update", "delete"));
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
            AssertNothingWritten();
        }
        else
        {
            var response = await _sut.ApplyEdits(request, CallContext("update", "delete"));
            response.UpdateResults.Should().ContainSingle().Which.Error.Message.Should().Contain("update and delete");
            response.DeleteResults.Should().ContainSingle().Which.Error.Message.Should().Contain("update and delete");
            WrittenBatch().Updates.Should().BeEmpty();
            WrittenBatch().Deletes.Should().BeEmpty();
        }
    }
}
