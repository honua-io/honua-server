// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.OgcClassic.UnitTests.Wfs20;

/// <summary>
/// SEC-5: WFS-T Transaction honours the publication's declared edit capabilities and the
/// per-field editability contract, the same way FeatureServer and OGC API Features do.
/// </summary>
[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20DeclaredEditCapabilityTests
{
    private const string PublicationId = "svc-cities-layer-1";

    private const string InsertAction =
        "<wfs:Insert><honua:cities><honua:name>created</honua:name></honua:cities></wfs:Insert>";

    private const string UpdateAction =
        "<wfs:Update typeName=\"honua:cities\"><wfs:Property><wfs:ValueReference>name</wfs:ValueReference>" +
        "<wfs:Value>changed</wfs:Value></wfs:Property><fes:Filter><fes:ResourceId rid=\"cities.1\"/></fes:Filter></wfs:Update>";

    private const string DeleteAction =
        "<wfs:Delete typeName=\"honua:cities\"><fes:Filter><fes:ResourceId rid=\"cities.1\"/></fes:Filter></wfs:Delete>";

    [UnitTheory]
    [InlineData(InsertAction)]
    [InlineData(UpdateAction)]
    [InlineData(DeleteAction)]
    public async Task HandleTransactionAsync_QueryOnlyPublication_RejectsEveryActionBeforeTheWrite(string action)
    {
        var (status, writer) = await ExecuteAsync(action, ["Query"]);

        status.Should().Be(StatusCodes.Status403Forbidden);
        writer.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task HandleTransactionAsync_CreateOnlyPublication_RejectsDeleteAndAllowsInsert()
    {
        var (deleteStatus, deleteWriter) = await ExecuteAsync(DeleteAction, ["Query", "Create"]);
        deleteStatus.Should().Be(StatusCodes.Status403Forbidden);
        deleteWriter.ReceivedCalls().Should().BeEmpty();

        var (insertStatus, insertWriter) = await ExecuteAsync(InsertAction, ["Query", "Create"]);
        insertStatus.Should().Be(StatusCodes.Status200OK);
        await insertWriter.ReceivedWithAnyArgs(1).ApplyEditsAsync(default, default!, default);
    }

    [UnitTest]
    public async Task HandleTransactionAsync_UnspecifiedCapabilities_KeepsLegacyInsertBehavior()
    {
        var (status, writer) = await ExecuteAsync(InsertAction, capabilities: null);

        status.Should().Be(StatusCodes.Status200OK);
        await writer.ReceivedWithAnyArgs(1).ApplyEditsAsync(default, default!, default);
    }

    [UnitTest]
    public async Task BuildTransactionUpdatedFeatureAsync_ChangingANonEditableField_IsRejected()
    {
        var handler = CreateHandler(CreateProvider(capabilities: null), Substitute.For<IFeatureWriter>());
        var existing = Feature.Create(
            1,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "before").Add("code", "A"));

        var editable = await handler.BuildTransactionUpdatedFeatureAsync(
            existing,
            Resource(),
            Changes("name", "after"),
            CancellationToken.None);
        editable.Attributes["name"].Should().Be("after");
        editable.Attributes["code"].Should().Be("A", "an untouched read-only field is carried through the merge");

        var readOnly = async () => await handler.BuildTransactionUpdatedFeatureAsync(
            existing,
            Resource(),
            Changes("code", "B"),
            CancellationToken.None);
        await readOnly.Should().ThrowAsync<ArgumentException>().WithMessage("*code*not editable*");
    }

    private static Wfs20Handler.TransactionFeatureChanges Changes(string field, string value)
        => new(
            GeometrySpecified: false,
            Geometry: null,
            ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add(field, value));

    private static async Task<(int Status, IFeatureWriter Writer)> ExecuteAsync(string action, string[]? capabilities)
    {
        var provider = CreateProvider(capabilities);
        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default!, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 1,
                updatedCount: 0,
                deletedCount: 0,
                createdIds: ImmutableArray.Create(101L),
                createResults: ImmutableArray.Create(EditOperationResult.Success(101))));
        var handler = CreateHandler(provider, writer);
        var context = CreateContext(provider, action);

        var result = await handler.HandleTransactionAsync(context, CancellationToken.None);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, writer);
    }

    private static DefaultHttpContext CreateContext(IMetadataV2GraphProvider provider, string action)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions { DataEditorRoles = ["editor"] }));
        services.AddSingleton(provider);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim(ClaimTypes.Role, "editor")],
            "Test"));
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/xml";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "<wfs:Transaction service=\"WFS\" version=\"2.0.0\" " +
            "xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" " +
            "xmlns:fes=\"http://www.opengis.net/fes/2.0\" " +
            "xmlns:honua=\"http://honua.io/wfs\">" + action + "</wfs:Transaction>"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static MetadataV2Resource Resource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-1", Name = "cities" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields = Fields()
        };

    private static MetadataV2Field[] Fields()
        =>
        [
            new MetadataV2Field
            {
                Name = "objectid",
                Type = MetadataV2FieldType.Integer,
                Nullable = false,
                SemanticRoles = ["id.primary"]
            },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Editable = true },
            new MetadataV2Field { Name = "code", Type = MetadataV2FieldType.String, Nullable = true, Editable = false }
        ];

    private static TestMetadataV2GraphProvider CreateProvider(string[]? capabilities)
    {
        var builder = new TestMetadataV2GraphBuilder()
            .AddResource("res-layer-1", "cities", MetadataV2ResourceType.FeatureDataset, fields: Fields())
            .AddStorageBinding("binding-layer-1", "res-layer-1", "test.layers.1", storageLayerId: 1)
            .AddService("svc-cities", "cities", protocols: [MetadataV2ServiceProtocols.Wfs20])
            .AddPublication(
                PublicationId,
                "svc-cities",
                "res-layer-1",
                layerIndex: 1,
                storageBindingId: "binding-layer-1",
                publicationType: MetadataV2PublicationType.OgcCollection);
        if (capabilities is not null)
        {
            builder = builder.WithPublicationCapabilities(PublicationId, capabilities);
        }

        return builder.BuildProvider();
    }

    private static Wfs20Handler CreateHandler(IMetadataV2GraphProvider provider, IFeatureWriter writer)
    {
        var coordinateTransform = Substitute.For<ICoordinateTransformService>();
        var queryServices = new Wfs20QueryServices(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IGmlFeatureStore>(),
            provider,
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            Substitute.For<IQueryProcessor>(),
            Options.Create(new Wfs20Options()),
            Substitute.For<IConfiguration>());
        var editServices = new Wfs20EditServices(
            writer,
            new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance),
            new EditProcessor(NullLogger<EditProcessor>.Instance),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()),
            Options.Create(new LimitsOptions()));
        var spatialServices = new Wfs20SpatialServices(
            new OgcFeaturesGeometryServices(
                Substitute.For<IGeometryService>(),
                coordinateTransform,
                Options.Create(new LimitsOptions()),
                NullLogger<OgcFeaturesGeometryServices>.Instance),
            coordinateTransform,
            Substitute.For<ICrsRegistry>());
        return new Wfs20Handler(NullLogger<Wfs20Handler>.Instance, queryServices, editServices, spatialServices);
    }
}
