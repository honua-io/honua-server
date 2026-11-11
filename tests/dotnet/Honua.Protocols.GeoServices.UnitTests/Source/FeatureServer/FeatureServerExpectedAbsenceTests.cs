// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
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

public sealed class FeatureServerExpectedAbsenceTests
{
    [UnitTheory]
    [InlineData("objectid", true, true)]
    [InlineData("id", true, false)]
    [InlineData(null, true, false)]
    [InlineData("objectid", false, false)]
    public async Task MissingDelete_OnlyDispatchesAbsenceGuardForConfirmedStorageIdentity(
        string? storageKey, bool expectedAbsent, bool shouldDispatch)
    {
        var target = CreateTarget(storageKey);
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(0, []));
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression?>(), Arg.Any<MetadataV2Resource>())
            .Returns(FilterTranslationResult.Success(null, new SqlFragment("objectid = @p0", [11L])));
        var handler = CreateHandler(reader, filters);
        var context = new FeatureServerEditsHandler.EditOperationContext
        {
            DeleteResults = new EditResult?[1]
        };

        await handler.ProcessDeleteOperationsAsync(
            new ApplyEditsRequest
            {
                Deletes = [11L],
                Preconditions = expectedAbsent
                    ? [new FeatureEditPrecondition { ObjectId = 11, ExpectedRowAbsent = true }]
                    : []
            },
            target, context, CancellationToken.None);

        context.HasValidationErrors.Should().Be(!shouldDispatch);
        if (shouldDispatch)
        {
            context.DeleteIds.Should().Equal(11L);
            context.DeleteFeatures.Should().ContainSingle().Which.Should().BeNull();
        }
        else
        {
            context.DeleteIds.Should().BeEmpty();
            context.DeleteResults![0]!.Error!.Code.Should().Be(expectedAbsent
                ? GeoServicesEditErrorCodes.UpdateConflict
                : GeoServicesEditErrorCodes.DeleteNotFound);
        }
    }

    private static FeatureServerEditsHandler.EditTarget CreateTarget(string? storageKey)
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.Integer,
                    SemanticRoles = ["id.primary"]
                }
            ]
        };
        using var keyDocument = JsonDocument.Parse(storageKey is null ? "null" : $"\"{storageKey}\"");
        var binding = storageKey is null ? null : new MetadataV2StorageBinding
        {
            Locator = "features",
            StorageLayerId = 7,
            Options = new Dictionary<string, JsonElement>
            {
                ["primaryKeyColumn"] = keyDocument.RootElement.Clone()
            }
        };
        return new FeatureServerEditsHandler.EditTarget
        {
            Resource = resource, StorageLayerId = 7, StorageBinding = binding
        };
    }

    private static FeatureServerEditsHandler CreateHandler(IFeatureReader reader, IFilterExpressionService filters)
        => new(
            new FeatureServerEditsDependencies(
                Substitute.For<IResourceValidator>(),
                Substitute.For<IFeatureWriter>(),
                reader,
                Substitute.For<IFeatureServerGeometryServices>(),
                Substitute.For<IEditParameterAdapter<GeoServicesEditRequest>>(),
                Substitute.For<IEditProcessor>(),
                new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
                filters,
                Substitute.For<IHttpContextAccessor>(),
                new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()),
                Substitute.For<IPluginEditPipeline>(),
                Substitute.For<IApplyEditsIdempotencyStore>(),
                Substitute.For<IFeatureEditGuard>(),
                Substitute.For<IFeatureLockService>()),
            NullLogger<FeatureServerEditsHandler>.Instance);
}
