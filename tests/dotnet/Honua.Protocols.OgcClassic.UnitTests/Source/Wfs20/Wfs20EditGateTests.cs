// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OgcClassic.UnitTests.Wfs20;

public sealed class Wfs20EditGateTests
{
    [UnitTest]
    public async Task ExecutePreparedEditAsync_EditorTracking_IgnoresForgeriesAndStampsServerValues()
    {
        var resource = TrackingResource();
        var insertWritten = await ExecuteTrackedEditAsync(resource, new Wfs20EditOperation(FeatureEditOperation.Create(ForgedTrackingRow(0))));
        insertWritten.Attributes.Should().Contain("editor", "alice");
        insertWritten.Attributes["updated"].Should().BeOfType<DateTime>();
        insertWritten.Attributes.Should().Contain("creator", "alice");
        insertWritten.Attributes["created"].Should().Be(insertWritten.Attributes["updated"]);

        var updateWritten = await ExecuteTrackedEditAsync(resource, new Wfs20EditOperation(FeatureEditOperation.Update(ForgedTrackingRow(42)), EditUpdateMode.Replace));
        updateWritten.Attributes.Should().Contain("editor", "alice");
        updateWritten.Attributes["updated"].Should().BeOfType<DateTime>();
        updateWritten.Attributes.Should().NotContainKey("creator").And.NotContainKey("created");
        updateWritten.PreservedAttributeNames.Should().Contain(["creator", "created"]);
    }

    private static Feature ForgedTrackingRow(long objectId)
    {
        var baseFeature = Row(objectId, "red", "S");
        return baseFeature with { Attributes = baseFeature.Attributes.Add("creator", "forged").Add("editor", "forged") };
    }

    private static async Task<Feature> ExecuteTrackedEditAsync(MetadataV2Object resource, Wfs20EditOperation operation)
    {
        var reader = CreateReader(Row(42, "red", "S"));
        var writer = new RecordingWriter();
        var handler = Wfs20HandlerTestFactory.Create(reader, writer);
        var context = CreateContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

        var result = await handler.ExecutePreparedEditAsync(
            context,
            SingleEditRequest(resource, operation),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        writer.ApplyCount.Should().Be(1);
        return writer.LastBatch.Operations.Single().Feature!.Value;
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_ContingentViolation_IsRejectedBeforeWrite()
    {
        var reader = CreateReader(Row(42, "red", "S"));
        var writer = new RecordingWriter();
        var handler = Wfs20HandlerTestFactory.Create(reader, writer);
        var operation = new Wfs20EditOperation(FeatureEditOperation.Create(Row(0, "red", "L")));

        var result = await handler.ExecutePreparedEditAsync(
            CreateContext(),
            SingleEditRequest(ContingentResource(), operation),
            CancellationToken.None);

        writer.ApplyCount.Should().Be(0);
        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.ErrorMessage.Should().Contain("colorSize");
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_AttributeRule_CalculatesAndRejectsConstraintViolation()
    {
        var resource = CreateAttributeRuleResource();
        var calcOp = new Wfs20EditOperation(FeatureEditOperation.Create(
            Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("color", "red"))));
        var (calcResult, calcWriter) = await ExecuteSingleEditAsync(resource, calcOp);

        calcResult.IsSuccess.Should().BeTrue();
        calcWriter.LastBatch.Operations.Single().Feature!.Value.Attributes.Should().Contain("size", "S");

        var violationOp = new Wfs20EditOperation(FeatureEditOperation.Create(Row(0, "blue", "S")));
        var (result, writer) = await ExecuteSingleEditAsync(resource, violationOp);

        writer.ApplyCount.Should().Be(0);
        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.ErrorMessage.Should().Be("Color must be red.");
    }

    private static MetadataV2Object CreateAttributeRuleResource() => ContingentResource() with
    {
        AttributeRules =
        [
            new MetadataV2AttributeRule
            {
                Name = "setSize",
                Type = MetadataV2AttributeRuleType.Calculation,
                FieldName = "size",
                TriggeringEvents = ["insert"],
                ScriptExpression = "return 'S';",
            },
            new MetadataV2AttributeRule
            {
                Name = "requireRed",
                Type = MetadataV2AttributeRuleType.Constraint,
                TriggeringEvents = ["insert"],
                ScriptExpression = "$feature.color == 'red'",
                ErrorMessage = "Color must be red.",
            },
        ]
    };

    private static async Task<(FeatureEditResult Result, RecordingWriter Writer)> ExecuteSingleEditAsync(
        MetadataV2Object resource,
        Wfs20EditOperation operation)
    {
        var writer = new RecordingWriter();
        var handler = Wfs20HandlerTestFactory.Create(CreateReader(Row(42, "red", "S")), writer);
        var result = await handler.ExecutePreparedEditAsync(
            CreateContext(),
            SingleEditRequest(resource, operation),
            CancellationToken.None);
        return (result, writer);
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_PluginHooks_RunBeforeAndAfterWrite()
    {
        var hooks = new RecordingHooks();
        var reader = CreateReader(Row(42, "red", "S"));
        var writer = new RecordingWriter();
        var handler = Wfs20HandlerTestFactory.Create(reader, writer);
        var operation = new Wfs20EditOperation(FeatureEditOperation.Create(Row(0, "red", "S")));

        var result = await handler.ExecutePreparedEditAsync(
            CreateContext(hooks: hooks),
            SingleEditRequest(ContingentResource(), operation),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        hooks.BeforeCount.Should().Be(1);
        hooks.AfterCount.Should().Be(1);

        var rejectingHooks = new RecordingHooks { RejectCreate = true };
        var blockedWriter = new RecordingWriter();
        var blockedHandler = Wfs20HandlerTestFactory.Create(reader, blockedWriter);
        var blocked = await blockedHandler.ExecutePreparedEditAsync(
            CreateContext(hooks: rejectingHooks),
            SingleEditRequest(ContingentResource(), operation),
            CancellationToken.None);

        blockedWriter.ApplyCount.Should().Be(0);
        blocked.WasRolledBack.Should().BeTrue();
        rejectingHooks.AfterCount.Should().Be(0);
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_ReplaceWithActiveFieldMask_PreservesMaskedColumnsWithoutTurningIntoMerge()
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ImmutableArray.Create("secret")));
        var reader = CreateReader(Feature.Create(
            42,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("color", "red").Add("size", "S").Add("secret", "classified")));
        var writer = new RecordingWriter();
        var handler = Wfs20HandlerTestFactory.Create(reader, writer);
        var replacement = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty.Add("color", "red"));
        var operation = new Wfs20EditOperation(FeatureEditOperation.Update(replacement), EditUpdateMode.Replace);
        var maskedResource = ContingentResource() with
        {
            ContingentValueGroups = [],
            SchemaFields =
            [
                .. ContingentResource().SchemaFields,
                new MetadataV2Field { Name = "secret", Type = MetadataV2FieldType.String, Editable = true, Nullable = true },
            ],
        };

        var result = await handler.ExecutePreparedEditAsync(
            CreateContext(masks: masks),
            SingleEditRequest(maskedResource, operation),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var written = writer.LastBatch.Operations.Single().Feature!.Value;
        written.PreserveOmittedMaskedAttributes.Should().BeFalse();
        written.PreservedAttributeNames.Should().Contain("secret");
        written.PreservedAttributeNames.Should().NotContain("size");
    }

    private static Wfs20Handler.WfsPreparedEditRequest SingleEditRequest(MetadataV2Object resource, Wfs20EditOperation operation) => new()
    {
        LayerId = 7,
        Resource = resource,
        Operations = ImmutableArray.Create(operation),
        RequestGeometryChangedFlags = ImmutableArray.Create(false),
        RollbackOnFailure = true,
    };

    private static DefaultHttpContext CreateContext(IFeatureEditHooks? hooks = null, IFieldMaskSource? masks = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        if (hooks is not null)
        {
            services.AddSingleton(hooks);
        }
        if (masks is not null)
        {
            services.AddSingleton(masks);
        }

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private static IFeatureReader CreateReader(Feature stored)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, [stored])));
        return reader;
    }

    private static Feature Row(long id, string color, string size)
        => Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty.Add("color", color).Add("size", size));

    private static MetadataV2Field EditableStringField(string name)
        => new() { Name = name, Type = MetadataV2FieldType.String, Nullable = true, Editable = true };

    private static MetadataV2ContingentFieldValue CodedValue(string jsonLiteral)
        => new() { Type = "code", Code = JsonElement.Parse(jsonLiteral) };

    private static MetadataV2Object ContingentResource()
    {
        var allowedPair = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["color"] = CodedValue("\"red\""),
            ["size"] = CodedValue("\"S\""),
        };
        return new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "shirts", Name = "shirts" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
                EditableStringField("color"),
                EditableStringField("size"),
            ],
            ContingentValueGroups =
            [
                new MetadataV2ContingentValueGroup
                {
                    Name = "colorSize",
                    Restrictive = true,
                    Fields = ["color", "size"],
                    ContingentValues = [new MetadataV2ContingentValue { Id = 1, Values = allowedPair }],
                },
            ],
        };
    }

    private static MetadataV2Object TrackingResource() => ContingentResource() with
    {
        Editing = new() { CreatorField = "creator", CreatedAtField = "created", EditorField = "editor", UpdatedAtField = "updated" },
        SchemaFields =
        [
            .. ContingentResource().SchemaFields,
            EditableStringField("creator"),
            new() { Name = "created", Type = MetadataV2FieldType.DateTime, Editable = true, Nullable = true },
            EditableStringField("editor"),
            new() { Name = "updated", Type = MetadataV2FieldType.DateTime, Editable = true, Nullable = true },
        ],
    };

    private sealed class RecordingWriter : IFeatureWriter
    {
        public int ApplyCount { get; private set; }

        public FeatureEditBatch LastBatch { get; private set; }

        public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            LastBatch = editBatch;
            return Task.FromResult(BuildRecordedResult(editBatch));
        }

        private static FeatureEditResult BuildRecordedResult(FeatureEditBatch editBatch)
        {
            var creates = ImmutableArray.CreateBuilder<EditOperationResult>();
            var updates = ImmutableArray.CreateBuilder<EditOperationResult>();
            var deletes = ImmutableArray.CreateBuilder<EditOperationResult>();
            foreach (var row in FeatureEditRows.Enumerate(editBatch))
            {
                var outcome = EditOperationResult.Success(row.ObjectId ?? 101);
                if (row.Kind == FeatureEditOperationKind.Create) creates.Add(outcome);
                else if (row.Kind == FeatureEditOperationKind.Update) updates.Add(outcome);
                else if (row.Kind == FeatureEditOperationKind.Delete) deletes.Add(outcome);
            }

            return FeatureEditResult.Success(
                creates.Count,
                updates.Count,
                deletes.Count,
                createResults: creates.ToImmutable(),
                updateResults: updates.ToImmutable(),
                deleteResults: deletes.ToImmutable());
        }

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingHooks : IFeatureEditHooks
    {
        public bool RejectCreate { get; init; }

        public int BeforeCount { get; private set; }

        public int AfterCount { get; private set; }

        public bool HasHooks => true;

        public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            CancellationToken cancellationToken)
        {
            BeforeCount++;
            IReadOnlyList<FeatureEditHookRejection> rejections = RejectCreate
                ? [new FeatureEditHookRejection(FeatureEditOperationKind.Create, 0, "blocked by hook")]
                : [];
            return Task.FromResult(rejections);
        }

        public Task RunAfterAsync(FeatureEditHookScope scope, FeatureEditOutcome outcome, CancellationToken cancellationToken)
        {
            AfterCount++;
            return Task.CompletedTask;
        }
    }
}
