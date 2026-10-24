// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Plugins.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Stable edit-owner subjects and non-admin owner reassignment (honua-server#5625, SRV-GS-005).
/// </summary>
public sealed class EditOwnerSubjectTests
{
    [UnitTest]
    public void SameDisplayName_DifferentNameIdentifier_ResolvesDifferentOwnerIds()
    {
        var ada = Principal(ClaimTypes.NameIdentifier, "subject-a", "Ada Lovelace");
        var otherAda = Principal(ClaimTypes.NameIdentifier, "subject-b", "Ada Lovelace");

        ada.Identity!.Name.Should().Be(otherAda.Identity!.Name);
        EditOwnerSubject.Resolve(ada).Should().Be("subject-a");
        EditOwnerSubject.Resolve(otherAda).Should().Be("subject-b");

        var subOnly = Principal("sub", "subject-from-sub", "Ada Lovelace");
        EditOwnerSubject.Resolve(subOnly).Should().Be("subject-from-sub");

        var keyOnly = Principal("api_key_id", "key-1", "Ada Lovelace");
        EditOwnerSubject.Resolve(keyOnly).Should().Be("key-1");

        var nameOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Ada Lovelace")],
            authenticationType: "Bearer"));
        EditOwnerSubject.Resolve(nameOnly).Should().Be("Ada Lovelace");
    }

    [UnitTest]
    public async Task NonAdminUpdate_ChangedOwner_IsRejected_AndOmittedOwnerIsPreserved()
    {
        var handler = CreateHandler();
        var resource = OwnerResource();
        var principal = new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false);
        var existing = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty.Add("name", "before"));
        var stored = Feature.Create(
            7,
            null,
            ImmutableDictionary<string, object?>.Empty
                .Add("name", "before")
                .Add("owner", "subject-a")
                .Add("secret", "keep"));
        var snapshots = new Dictionary<long, OwnerFieldSnapshot>
        {
            [7] = new(true, "subject-a"),
        };

        Func<Task> reassigned = () => handler.BuildFeatureFromGeoServicesAsync(
            FeatureWith("after", "subject-b"),
            7,
            resource,
            AttributeRuleEditEvent.Update,
            principal,
            CancellationToken.None,
            existing,
            snapshots);
        await reassigned.Should().ThrowAsync<Exception>()
            .WithMessage("Only an administrator can change the feature owner.");

        var omitted = await handler.BuildFeatureFromGeoServicesAsync(
            FeatureWith("after", owner: null),
            7,
            resource,
            AttributeRuleEditEvent.Update,
            principal,
            CancellationToken.None,
            existing,
            snapshots);
        omitted.PreserveOmittedMaskedAttributes.Should().BeTrue();
        omitted.Attributes.ContainsKey("owner").Should().BeFalse();
        FeatureDataAccess.PreserveOmittedAttributes(omitted, stored).Attributes["owner"].Should().Be("subject-a");

        var adminEdit = await handler.BuildFeatureFromGeoServicesAsync(
            FeatureWith("after", "subject-b"),
            7,
            resource,
            AttributeRuleEditEvent.Update,
            new EditPrincipal("admin-subject", IsAuthenticated: true, IsAdmin: true),
            CancellationToken.None,
            existing);
        adminEdit.Attributes["owner"].Should().Be("subject-b");

        var inserted = await handler.BuildFeatureFromGeoServicesAsync(
            FeatureWith("created", "ignored"),
            0,
            resource,
            AttributeRuleEditEvent.Insert,
            principal,
            CancellationToken.None);
        inserted.Attributes["owner"].Should().Be("subject-a");
    }

    [UnitTest]
    public async Task Insert_AssignsGlobalIdAndReplacesClientEditorTracking()
    {
        var clock = new DateTimeOffset(2026, 10, 14, 3, 4, 5, TimeSpan.Zero);
        var clientGlobalId = "11111111-1111-1111-1111-111111111111";
        var inserted = await CreateHandler().BuildFeatureFromGeoServicesAsync(
            new GeoServicesFeature
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["name"] = "tracked",
                    ["GlobalID"] = clientGlobalId,
                    ["created_user"] = "forged-editor",
                    ["created_date"] = 0L,
                    ["locked_code"] = "client",
                },
            },
            0,
            TrackedResource(),
            AttributeRuleEditEvent.Insert,
            new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false),
            CancellationToken.None,
            timeProvider: new FixedTimeProvider(clock));

        inserted.Attributes["GlobalID"].Should().BeOfType<string>();
        inserted.Attributes["GlobalID"].Should().NotBe(clientGlobalId);
        Guid.TryParse(inserted.Attributes["GlobalID"]!.ToString(), out _).Should().BeTrue();
        inserted.Attributes["created_user"].Should().Be("subject-a");
        inserted.Attributes["editor"].Should().Be("subject-a");
        inserted.Attributes["created_date"].Should().Be(clock.ToUnixTimeMilliseconds());
        inserted.Attributes["updated_date"].Should().Be(clock.ToUnixTimeMilliseconds());
        inserted.Attributes.ContainsKey("locked_code").Should().BeFalse();

        var kept = await CreateHandler().BuildFeatureFromGeoServicesAsync(
            new GeoServicesFeature
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["name"] = "tracked",
                    ["GlobalID"] = clientGlobalId,
                },
            },
            0,
            TrackedResource(),
            AttributeRuleEditEvent.Insert,
            new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false),
            CancellationToken.None,
            useGlobalIds: true,
            timeProvider: new FixedTimeProvider(clock));
        kept.Attributes["GlobalID"].Should().Be(clientGlobalId);
    }

    [UnitTest]
    public async Task Update_RestoresCreatorAndGlobalId_AndStampsEditor()
    {
        var clock = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
        var storedGlobalId = "22222222-2222-2222-2222-222222222222";
        var existing = Feature.Create(7, null, ImmutableDictionary.CreateRange(
            StringComparer.OrdinalIgnoreCase,
            new KeyValuePair<string, object?>[]
            {
                new("name", "before"),
                new("GlobalID", storedGlobalId),
                new("created_user", "subject-a"),
                new("created_date", 10L),
            }));

        var updated = await CreateHandler().BuildFeatureFromGeoServicesAsync(
            new GeoServicesFeature
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["objectid"] = 7L,
                    ["name"] = "after",
                    ["GlobalID"] = "33333333-3333-3333-3333-333333333333",
                    ["created_user"] = "forged-editor",
                    ["editor"] = "forged-editor",
                },
            },
            7,
            TrackedResource(),
            AttributeRuleEditEvent.Update,
            new EditPrincipal("subject-b", IsAuthenticated: true, IsAdmin: false),
            CancellationToken.None,
            existing,
            timeProvider: new FixedTimeProvider(clock));

        updated.Attributes["GlobalID"].Should().Be(storedGlobalId);
        updated.Attributes["created_user"].Should().Be("subject-a");
        updated.Attributes["created_date"].Should().Be(10L);
        updated.Attributes["editor"].Should().Be("subject-b");
        updated.Attributes["updated_date"].Should().Be(clock.ToUnixTimeMilliseconds());
        updated.Attributes["name"].Should().Be("after");
    }

    [UnitTest]
    public async Task NonAdminReplacement_ChangedOrOmittedOwner_IsRejected()
    {
        var feature = FeatureWith("after", "subject-b", replaceAttributes: true);
        var existing = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty.Add("name", "before"));
        var snapshots = new Dictionary<long, OwnerFieldSnapshot> { [7] = new(true, "subject-a") };

        Func<Task> edit = () => CreateHandler().BuildFeatureFromGeoServicesAsync(
            feature, 7, OwnerResource(), AttributeRuleEditEvent.Update,
            new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false),
            CancellationToken.None, existing, snapshots);

        await edit.Should().ThrowAsync<Exception>()
            .WithMessage("Only an administrator can change the feature owner.");

        Func<Task> omitted = () => CreateHandler().BuildFeatureFromGeoServicesAsync(
            FeatureWith("after", owner: null, replaceAttributes: true),
            7, OwnerResource(), AttributeRuleEditEvent.Update,
            new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false),
            CancellationToken.None, existing, snapshots);
        await omitted.Should().ThrowAsync<Exception>()
            .WithMessage("Only an administrator can change the feature owner.");
    }

    [UnitTest]
    public async Task NonAdminReplacement_UnchangedOwner_DropsOtherOmittedAttributes()
    {
        var feature = FeatureWith("after", "subject-a", replaceAttributes: true);
        var stored = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
            .Add("owner", "subject-a").Add("secret", "remove"));
        var snapshots = new Dictionary<long, OwnerFieldSnapshot> { [7] = new(true, "subject-a") };

        var edited = await CreateHandler().BuildFeatureFromGeoServicesAsync(
            feature, 7, OwnerResource(), AttributeRuleEditEvent.Update,
            new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false),
            CancellationToken.None, stored, snapshots);

        edited.PreserveOmittedMaskedAttributes.Should().BeFalse();
        var replacement = FeatureDataAccess.PreserveOmittedAttributes(edited, stored);
        replacement.Attributes["owner"].Should().Be("subject-a");
        replacement.Attributes.ContainsKey("secret").Should().BeFalse();
    }

    [UnitTest]
    public async Task MultiUpdate_CapturesOnlyAcceptedOwner_AndKeepsItsPreWriteValue()
    {
        var currentOwner = "subject-a";
        var reader = OwnerReader(() => currentOwner);
        var context = new FeatureServerEditsHandler.EditOperationContext
        {
            UpdateResults = new EditResult?[2],
            RequiresOwnerGuards = true,
        };
        var request = new ApplyEditsRequest
        {
            Updates =
            [
                EditFeature(7, "allowed"),
                EditFeature(8, "denied"),
            ],
        };

        await CreateHandler(reader).ProcessUpdateOperationsAsync(
            request, context, OwnerEditResource(), storageLayerId: 7,
            new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false),
            guardUpdateSnapshot: false, versionContext: null, CancellationToken.None);

        context.UpdateWriteFeatures.Should().ContainSingle(feature => feature.Id == 7);
        context.UpdateResults![1].Should().NotBeNull();
        context.UpdateResults[1]!.Error!.Code.Should().Be(GeoServicesEditErrorCodes.NotPermitted);
        currentOwner = "subject-b";

        var guards = FeatureServerEditsHandler.BuildOwnerGuards(
            FeatureEditBatch.Create(updates: context.UpdateWriteFeatures.ToImmutableArray()),
            context.OwnerGuards);
        guards.Should().ContainSingle().Which.Should().Be(new OwnerEditGuard(7, "owner", "subject-a"));
    }

    [UnitTest]
    public async Task BranchDelete_CapturesOnlyAcceptedOwner_AndGuardsOrderedWrite()
    {
        var currentOwner = "subject-a";
        var reader = OwnerReader(() => currentOwner);
        var context = new FeatureServerEditsHandler.EditOperationContext
        {
            DeleteResults = new EditResult?[2],
            RequiresOwnerGuards = true,
        };
        var branch = new VersionContext
        {
            VersionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            VersionName = "owner.branch",
            CommonAncestorGeneration = 1,
        };

        await CreateHandler(reader).ProcessDeleteOperationsAsync(
            new ApplyEditsRequest { Deletes = [7L, 8L] },
            OwnerEditResource(), storageLayerId: 7, context,
            new EditPrincipal("subject-a", IsAuthenticated: true, IsAdmin: false),
            branch, CancellationToken.None);

        context.DeleteIds.Should().ContainSingle().Which.Should().Be(7);
        context.DeleteResults![1].Should().NotBeNull();
        context.DeleteResults[1]!.Error!.Code.Should().Be(GeoServicesEditErrorCodes.NotPermitted);
        await reader.Received().QueryAsync(7,
            Arg.Is<FeatureQuery>(query => query.VersionContext == branch && query.OutFields != null && query.OutFields.Contains("owner")),
            Arg.Any<CancellationToken>());
        currentOwner = "subject-b";

        var guards = FeatureServerEditsHandler.BuildOwnerGuards(
            FeatureEditBatch.Create(operations: [FeatureEditOperation.Delete(7)]), context.OwnerGuards);
        guards.Should().ContainSingle().Which.Should().Be(new OwnerEditGuard(7, "owner", "subject-a"));
        Action missingGuard = () => FeatureServerEditsHandler.BuildOwnerGuards(
            FeatureEditBatch.Create(deletes: [7L, 8L]), context.OwnerGuards);
        missingGuard.Should().Throw<FeatureServerEditsHandler.EditBatchValidationException>()
            .WithMessage("Edit not permitted.");
    }

    private static IFeatureReader OwnerReader(Func<string> currentOwner)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<FeatureQuery>(1);
                var features = new[]
                {
                    Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
                        .Add("name", "before").Add("owner", currentOwner())),
                    Feature.Create(8, null, ImmutableDictionary<string, object?>.Empty
                        .Add("name", "before").Add("owner", "subject-b")),
                }.Where(feature => query.ObjectIds == null || query.ObjectIds.Value.IsDefaultOrEmpty || query.ObjectIds.Value.Contains(feature.Id))
                    .ToImmutableArray();
                return Task.FromResult(QueryResult<Feature>.Create(features.Length, features));
            });
        return reader;
    }

    private static GeoServicesFeature EditFeature(long objectId, string name)
        => new() { Attributes = new Dictionary<string, object?> { ["objectid"] = objectId, ["name"] = name } };

    private static MetadataV2Resource OwnerEditResource()
        => OwnerResource() with
        {
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"],
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String, Nullable = true },
            ],
        };

    private static ClaimsPrincipal Principal(string subjectType, string subject, string displayName)
        => new(new ClaimsIdentity(
            [
                new Claim(subjectType, subject),
                new Claim(ClaimTypes.Name, displayName),
            ],
            authenticationType: "Bearer"));

    private static GeoServicesFeature FeatureWith(string name, string? owner, bool replaceAttributes = false)
    {
        var attributes = new Dictionary<string, object?> { ["name"] = name };
        if (owner is not null)
        {
            attributes["owner"] = owner;
        }

        return new GeoServicesFeature { Attributes = attributes, ReplaceAttributes = replaceAttributes };
    }

    private static MetadataV2Resource TrackedResource()
        => new()
        {
            Editing = new MetadataV2ResourceEditing
            {
                GlobalIdField = "GlobalID",
                CreatorField = "created_user",
                CreatedAtField = "created_date",
                EditorField = "editor",
                UpdatedAtField = "updated_date",
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "GlobalID", Type = MetadataV2FieldType.Uuid, Nullable = true },
                new MetadataV2Field { Name = "created_user", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "editor", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.DateTime, Nullable = true },
                new MetadataV2Field { Name = "updated_date", Type = MetadataV2FieldType.DateTime, Nullable = true },
                new MetadataV2Field
                {
                    Name = "locked_code",
                    Type = MetadataV2FieldType.String,
                    Nullable = true,
                    Editable = false,
                },
            ],
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static MetadataV2Resource OwnerResource()
        => new()
        {
            OwnerEditPolicy = new MetadataV2OwnerEditPolicy
            {
                Enabled = true,
                OwnerField = "owner",
                StampOwnerOnInsert = true,
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field { Name = "secret", Type = MetadataV2FieldType.String, Nullable = true },
            ],
        };

    private static FeatureServerEditsHandler CreateHandler(IFeatureReader? reader = null)
        => new(
            new FeatureServerEditsDependencies(
                Substitute.For<IResourceValidator>(),
                Substitute.For<IFeatureWriter>(),
                reader ?? Substitute.For<IFeatureReader>(),
                Substitute.For<IFeatureServerGeometryServices>(),
                Substitute.For<IEditParameterAdapter<GeoServicesEditRequest>>(),
                Substitute.For<IEditProcessor>(),
                new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
                Substitute.For<IFilterExpressionService>(),
                Substitute.For<IHttpContextAccessor>(),
                new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()),
                Substitute.For<IPluginEditPipeline>(),
                Substitute.For<IApplyEditsIdempotencyStore>(),
                Substitute.For<IFeatureEditGuard>(),
                Substitute.For<IFeatureLockService>()),
            NullLogger<FeatureServerEditsHandler>.Instance);
}
