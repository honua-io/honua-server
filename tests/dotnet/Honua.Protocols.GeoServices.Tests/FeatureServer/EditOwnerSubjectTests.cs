// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public async Task NonAdminUpdate_SuppliedOwner_KeepsStoredOwner()
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

        var reassigned = await handler.BuildFeatureFromGeoServicesAsync(
            FeatureWith("after", "subject-b"),
            7,
            resource,
            AttributeRuleEditEvent.Update,
            principal,
            CancellationToken.None,
            existing,
            snapshots);

        reassigned.Attributes.Values.Should().NotContain("subject-b");
        reassigned.PreserveOmittedMaskedAttributes.Should().BeTrue();
        var kept = FeatureDataAccess.PreserveOmittedAttributes(reassigned, stored);
        kept.Attributes["name"].Should().Be("after");
        kept.Attributes["owner"].Should().Be("subject-a");
        kept.Attributes["secret"].Should().Be("keep");

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

    private static ClaimsPrincipal Principal(string subjectType, string subject, string displayName)
        => new(new ClaimsIdentity(
            [
                new Claim(subjectType, subject),
                new Claim(ClaimTypes.Name, displayName),
            ],
            authenticationType: "Bearer"));

    private static GeoServicesFeature FeatureWith(string name, string? owner)
    {
        var attributes = new Dictionary<string, object?> { ["name"] = name };
        if (owner is not null)
        {
            attributes["owner"] = owner;
        }

        return new GeoServicesFeature { Attributes = attributes };
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

    private static FeatureServerEditsHandler CreateHandler()
        => new(
            new FeatureServerEditsDependencies(
                Substitute.For<IResourceValidator>(),
                Substitute.For<IFeatureWriter>(),
                Substitute.For<IFeatureReader>(),
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
