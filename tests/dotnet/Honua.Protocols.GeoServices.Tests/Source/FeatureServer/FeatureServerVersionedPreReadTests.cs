// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
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
/// Named-version applyEdits pre-reads the branch image so an attribute-only update
/// does not copy the DEFAULT row onto the branch overlay.
/// </summary>
public sealed class FeatureServerVersionedPreReadTests
{
    private static readonly byte[] BranchGeometry = [10, 20, 30];
    private static readonly byte[] BaseGeometry = [1, 2, 3];

    private static readonly VersionContext Branch = new()
    {
        VersionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        VersionName = "editor.branch",
        CommonAncestorGeneration = 4,
    };

    [UnitTest]
    public async Task AttributeOnlyBranchUpdate_KeepsBranchGeometryAndOtherAttributes()
    {
        var reader = VersionedReader();
        var handler = CreateHandler(reader);
        var context = new FeatureServerEditsHandler.EditOperationContext
        {
            UpdateResults = new EditResult?[1],
        };

        await handler.ProcessUpdateOperationsAsync(
            UpdateRequest("renamed"),
            context,
            Resource(),
            storageLayerId: 7,
            EditPrincipal.Anonymous,
            guardUpdateSnapshot: false,
            Branch,
            CancellationToken.None);

        context.HasValidationErrors.Should().BeFalse();
        var updated = context.UpdateFeatures.Should().ContainSingle().Subject;
        updated.Geometry.Should().Equal(BranchGeometry);
        updated.Attributes["name"].Should().Be("renamed");
        updated.Attributes["code"].Should().Be("branch-code");
        await reader.Received().QueryAsync(
            7,
            Arg.Is<FeatureQuery>(query => query.VersionContext == Branch),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task AttributeOnlyDefaultUpdate_ReadsTheBaseTable()
    {
        var reader = VersionedReader();
        var handler = CreateHandler(reader);

        foreach (var version in new VersionContext?[] { null, VersionContext.Default })
        {
            var context = new FeatureServerEditsHandler.EditOperationContext
            {
                UpdateResults = new EditResult?[1],
            };
            await handler.ProcessUpdateOperationsAsync(
                UpdateRequest("renamed"),
                context,
                Resource(),
                storageLayerId: 7,
                EditPrincipal.Anonymous,
                guardUpdateSnapshot: false,
                version,
                CancellationToken.None);

            var updated = context.UpdateFeatures.Should().ContainSingle().Subject;
            updated.Geometry.Should().Equal(BaseGeometry);
            updated.Attributes["code"].Should().Be("base-code");
            updated.Attributes["name"].Should().Be("renamed");
        }

        await reader.Received(2).QueryAsync(
            7,
            Arg.Is<FeatureQuery>(query => query.VersionContext == null),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task BranchDelete_PreReadsTheBranchImage()
    {
        var reader = VersionedReader();
        var handler = CreateHandler(reader);
        var context = new FeatureServerEditsHandler.EditOperationContext
        {
            DeleteResults = new EditResult?[1],
        };

        await handler.ProcessDeleteOperationsAsync(
            new ApplyEditsRequest { Deletes = [11L] },
            Resource(),
            storageLayerId: 7,
            context,
            EditPrincipal.Anonymous,
            Branch,
            CancellationToken.None);

        var deleted = context.DeleteFeatures.Should().ContainSingle().Subject;
        deleted.HasValue.Should().BeTrue();
        deleted!.Value.Geometry.Should().Equal(BranchGeometry);
        context.DeleteIds.Should().ContainSingle().Which.Should().Be(11);
        await reader.Received().QueryAsync(
            7,
            Arg.Is<FeatureQuery>(query => query.VersionContext == Branch),
            Arg.Any<CancellationToken>());
    }

    private static IFeatureReader VersionedReader()
    {
        var branch = Feature.Create(11, BranchGeometry, Attributes("branch-name", "branch-code"));
        var baseFeature = Feature.Create(11, BaseGeometry, Attributes("base-name", "base-code"));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<FeatureQuery>(1);
                var feature = query.VersionContext is { IsDefault: false } ? branch : baseFeature;
                return Task.FromResult(QueryResult<Feature>.Create(1, [feature]));
            });
        return reader;
    }

    private static ImmutableDictionary<string, object?> Attributes(string name, string code)
        => ImmutableDictionary<string, object?>.Empty
            .Add("name", name)
            .Add("code", code);

    private static ApplyEditsRequest UpdateRequest(string name)
        => new()
        {
            Updates =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = 11L,
                        ["name"] = name,
                    },
                },
            ],
        };

    private static MetadataV2Resource Resource()
        => new()
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
                new MetadataV2Field { Name = "code", Type = MetadataV2FieldType.String, Nullable = true },
            ],
        };

    private static FeatureServerEditsHandler CreateHandler(IFeatureReader reader)
        => new(
            new FeatureServerEditsDependencies(
                Substitute.For<IResourceValidator>(),
                Substitute.For<IFeatureWriter>(),
                reader,
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
