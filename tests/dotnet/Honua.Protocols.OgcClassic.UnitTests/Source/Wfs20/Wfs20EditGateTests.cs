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
        foreach (var isInsert in new[] { true, false })
        {
            var reader = CreateReader(Row(42, "red", "S"));
            var writer = new RecordingWriter();
            var handler = Wfs20HandlerTestFactory.Create(reader, writer);
            var context = CreateContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));
            var feature = Row(isInsert ? 0 : 42, "red", "S");
            feature = feature with { Attributes = feature.Attributes.Add("creator", "forged").Add("editor", "forged") };
            var operation = isInsert
                ? new Wfs20EditOperation(FeatureEditOperation.Create(feature))
                : new Wfs20EditOperation(FeatureEditOperation.Update(feature), EditUpdateMode.Replace);

            var result = await handler.ExecutePreparedEditAsync(
                context,
                layerId: 7,
                resource,
                ImmutableArray.Create(operation),
                ImmutableArray.Create(false),
                rollbackOnFailure: true,
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            writer.ApplyCount.Should().Be(1);
            var written = writer.LastBatch.Operations.Single().Feature!.Value;
            written.Attributes.Should().Contain("editor", "alice");
            written.Attributes["updated"].Should().BeOfType<DateTime>();
            if (isInsert)
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

    [UnitTest]
    public async Task ExecutePreparedEditAsync_ContingentViolation_IsRejectedBeforeWrite()
    {
        var reader = CreateReader(Row(42, "red", "S"));
        var writer = new RecordingWriter();
        var handler = Wfs20HandlerTestFactory.Create(reader, writer);
        var operation = new Wfs20EditOperation(FeatureEditOperation.Create(Row(0, "red", "L")));

        var result = await handler.ExecutePreparedEditAsync(
            CreateContext(),
            layerId: 7,
            ContingentResource(),
            ImmutableArray.Create(operation),
            ImmutableArray.Create(false),
            rollbackOnFailure: true,
            CancellationToken.None);

        writer.ApplyCount.Should().Be(0);
        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.ErrorMessage.Should().Contain("colorSize");
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_AttributeRule_CalculatesAndRejectsConstraintViolation()
    {
        var resource = ContingentResource() with
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
        var reader = CreateReader(Row(42, "red", "S"));
        var calcWriter = new RecordingWriter();
        var calcHandler = Wfs20HandlerTestFactory.Create(reader, calcWriter);
        var calcOp = new Wfs20EditOperation(FeatureEditOperation.Create(
            Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("color", "red"))));
        var calcResult = await calcHandler.ExecutePreparedEditAsync(
            CreateContext(),
            layerId: 7,
            resource,
            ImmutableArray.Create(calcOp),
            ImmutableArray.Create(false),
            rollbackOnFailure: true,
            CancellationToken.None);

        calcResult.IsSuccess.Should().BeTrue();
        calcWriter.LastBatch.Operations.Single().Feature!.Value.Attributes.Should().Contain("size", "S");

        var writer = new RecordingWriter();
        var handler = Wfs20HandlerTestFactory.Create(reader, writer);
        var operation = new Wfs20EditOperation(FeatureEditOperation.Create(Row(0, "blue", "S")));

        var result = await handler.ExecutePreparedEditAsync(
            CreateContext(),
            layerId: 7,
            resource,
            ImmutableArray.Create(operation),
            ImmutableArray.Create(false),
            rollbackOnFailure: true,
            CancellationToken.None);

        writer.ApplyCount.Should().Be(0);
        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.ErrorMessage.Should().Be("Color must be red.");
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
            layerId: 7,
            ContingentResource(),
            ImmutableArray.Create(operation),
            ImmutableArray.Create(false),
            rollbackOnFailure: true,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        hooks.BeforeCount.Should().Be(1);
        hooks.AfterCount.Should().Be(1);

        var rejectingHooks = new RecordingHooks { RejectCreate = true };
        var blockedWriter = new RecordingWriter();
        var blockedHandler = Wfs20HandlerTestFactory.Create(reader, blockedWriter);
        var blocked = await blockedHandler.ExecutePreparedEditAsync(
            CreateContext(hooks: rejectingHooks),
            layerId: 7,
            ContingentResource(),
            ImmutableArray.Create(operation),
            ImmutableArray.Create(false),
            rollbackOnFailure: true,
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

        var result = await handler.ExecutePreparedEditAsync(
            CreateContext(masks: masks),
            layerId: 7,
            ContingentResource() with
            {
                ContingentValueGroups = [],
                SchemaFields =
                [
                    .. ContingentResource().SchemaFields,
                    new MetadataV2Field { Name = "secret", Type = MetadataV2FieldType.String, Editable = true, Nullable = true },
                ],
            },
            ImmutableArray.Create(operation),
            ImmutableArray.Create(false),
            rollbackOnFailure: true,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var written = writer.LastBatch.Operations.Single().Feature!.Value;
        written.PreserveOmittedMaskedAttributes.Should().BeFalse();
        written.PreservedAttributeNames.Should().Contain("secret");
        written.PreservedAttributeNames.Should().NotContain("size");
    }

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

    private static MetadataV2Object ContingentResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "shirts", Name = "shirts" },
        Type = MetadataV2ObjectType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
            new MetadataV2Field { Name = "color", Type = MetadataV2FieldType.String, Nullable = true, Editable = true },
            new MetadataV2Field { Name = "size", Type = MetadataV2FieldType.String, Nullable = true, Editable = true },
        ],
        ContingentValueGroups =
        [
            new MetadataV2ContingentValueGroup
            {
                Name = "colorSize",
                Restrictive = true,
                Fields = ["color", "size"],
                ContingentValues =
                [
                    new MetadataV2ContingentValue
                    {
                        Id = 1,
                        Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["color"] = new() { Type = "code", Code = JsonElement.Parse("\"red\"") },
                            ["size"] = new() { Type = "code", Code = JsonElement.Parse("\"S\"") },
                        },
                    },
                ],
            },
        ],
    };

    private static MetadataV2Object TrackingResource() => ContingentResource() with
    {
        Editing = new() { CreatorField = "creator", CreatedAtField = "created", EditorField = "editor", UpdatedAtField = "updated" },
        SchemaFields =
        [
            .. ContingentResource().SchemaFields,
            new() { Name = "creator", Type = MetadataV2FieldType.String, Editable = true, Nullable = true },
            new() { Name = "created", Type = MetadataV2FieldType.DateTime, Editable = true, Nullable = true },
            new() { Name = "editor", Type = MetadataV2FieldType.String, Editable = true, Nullable = true },
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
            var rows = FeatureEditRows.Enumerate(editBatch);
            ImmutableArray<EditOperationResult> Results(FeatureEditOperationKind kind)
                => [.. rows.Where(row => row.Kind == kind).Select(row => EditOperationResult.Success(row.ObjectId ?? 101))];
            return Task.FromResult(FeatureEditResult.Success(
                rows.Count(static row => row.Kind == FeatureEditOperationKind.Create),
                rows.Count(static row => row.Kind == FeatureEditOperationKind.Update),
                rows.Count(static row => row.Kind == FeatureEditOperationKind.Delete),
                createResults: Results(FeatureEditOperationKind.Create),
                updateResults: Results(FeatureEditOperationKind.Update),
                deleteResults: Results(FeatureEditOperationKind.Delete)));
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
