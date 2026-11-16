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
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
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

[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Security)]
public sealed class Wfs20CanonicalServiceWriteTests
{
    private const string SecondaryPublicationId = "svc-cities-layer-1";
    private const string CanonicalServiceName = "primary";
    private const string SecondaryServiceName = "cities";

    private const string InsertAction =
        "<wfs:Insert><honua:svc-cities-layer-1><honua:name>created</honua:name></honua:svc-cities-layer-1></wfs:Insert>";

    private static readonly AccessPolicy AnonymousWrites = new() { AllowAnonymous = true, AllowAnonymousWrite = true };

    [UnitTest]
    public async Task HandleTransactionAsync_SecondaryGrantWithoutCanonicalServiceGrant_IsForbidden()
    {
        var (status, writer) = await ExecuteAsync(
            CreateProvider(canonicalPolicy: null, secondaryPolicy: null),
            Editor($"data-editor:{SecondaryServiceName}"));

        status.Should().Be(StatusCodes.Status403Forbidden);
        writer.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task HandleTransactionAsync_SecondaryAndCanonicalServiceGrants_Inserts()
    {
        var (status, writer) = await ExecuteAsync(
            CreateProvider(canonicalPolicy: null, secondaryPolicy: null),
            Editor($"data-editor:{SecondaryServiceName}", $"data-editor:{CanonicalServiceName}"));

        status.Should().Be(StatusCodes.Status200OK);
        await writer.ReceivedWithAnyArgs(1).ApplyEditsAsync(default, default!, default);
    }

    [UnitTest]
    public async Task HandleTransactionAsync_AnonymousWriteOnlyOnSecondaryService_IsRejected()
    {
        var (status, writer) = await ExecuteAsync(
            CreateProvider(canonicalPolicy: null, secondaryPolicy: AnonymousWrites),
            Anonymous());

        status.Should().Be(
            StatusCodes.Status401Unauthorized,
            "anonymous writes on the secondary service do not open the canonical service's resource");
        writer.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task HandleTransactionAsync_AnonymousWriteOnBothServices_Inserts()
    {
        var (status, writer) = await ExecuteAsync(
            CreateProvider(canonicalPolicy: AnonymousWrites, secondaryPolicy: AnonymousWrites),
            Anonymous());

        status.Should().Be(StatusCodes.Status200OK);
        await writer.ReceivedWithAnyArgs(1).ApplyEditsAsync(default, default!, default);
    }

    [Theory]
    [InlineData(InsertAction)]
    [InlineData("<wfs:Update typeName=\"honua:svc-cities-layer-1\"><wfs:Property><wfs:ValueReference>name</wfs:ValueReference><wfs:Value>changed</wfs:Value></wfs:Property><fes:Filter><fes:ResourceId rid=\"svc-cities-layer-1.1\"/></fes:Filter></wfs:Update>")]
    [InlineData("<wfs:Delete typeName=\"honua:svc-cities-layer-1\"><fes:Filter><fes:ResourceId rid=\"svc-cities-layer-1.1\"/></fes:Filter></wfs:Delete>")]
    [InlineData("<wfs:Replace><honua:svc-cities-layer-1><honua:name>changed</honua:name></honua:svc-cities-layer-1><fes:Filter><fes:ResourceId rid=\"svc-cities-layer-1.1\"/></fes:Filter></wfs:Replace>")]
    public async Task HandleTransactionAsync_CanonicalGrantWithoutAddressedServiceGrant_IsForbidden(string action)
    {
        var (status, writer) = await ExecuteAsync(
            CreateProvider(canonicalPolicy: null, secondaryPolicy: null),
            Editor($"data-editor:{CanonicalServiceName}"), action);

        status.Should().Be(StatusCodes.Status403Forbidden);
        writer.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task HandleTransactionAsync_AnonymousWriteOnlyOnCanonicalService_IsRejected()
    {
        var (status, writer) = await ExecuteAsync(
            // Keep the addressed type discoverable so this reaches its write-policy check.
            CreateProvider(canonicalPolicy: AnonymousWrites, secondaryPolicy: new AccessPolicy { AllowAnonymous = true }),
            Anonymous());

        status.Should().Be(StatusCodes.Status401Unauthorized);
        writer.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task HandleTransactionAsync_TwoPublicationsOfSameResource_ChecksEachService()
    {
        var primaryInsert = InsertAction.Replace(SecondaryPublicationId, "svc-primary-layer-0", StringComparison.Ordinal);
        var (status, writer) = await ExecuteAsync(
            CreateProvider(canonicalPolicy: null, secondaryPolicy: null),
            Editor($"data-editor:{CanonicalServiceName}"), primaryInsert + InsertAction);

        status.Should().Be(StatusCodes.Status403Forbidden);
        writer.ReceivedCalls().Should().BeEmpty();
    }

    private static ClaimsPrincipal Editor(params string[] roles)
        => new(new ClaimsIdentity(
            roles.Select(role => new Claim(ClaimTypes.Role, role))
                .Prepend(new Claim(ClaimTypes.NameIdentifier, "alice")),
            "Test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static async Task<(int Status, IFeatureWriter Writer)> ExecuteAsync(
        IMetadataV2GraphProvider provider,
        ClaimsPrincipal user,
        string action = InsertAction)
    {
        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default!, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 1,
                updatedCount: 0,
                deletedCount: 0,
                createdIds: ImmutableArray.Create(101L),
                createResults: ImmutableArray.Create(EditOperationResult.Success(101))));
        var handler = CreateHandler(provider, writer);
        var context = CreateContext(provider, user, action);

        var result = await handler.HandleTransactionAsync(context, CancellationToken.None);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, writer);
    }

    private static DefaultHttpContext CreateContext(IMetadataV2GraphProvider provider, ClaimsPrincipal user, string action)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions { DataEditorRoles = ["editor"] }));
        services.AddSingleton(provider);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.User = user;
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

    // Both WFS services publish one resource at the same index. Requests must authorize
    // the addressed publication even when numeric resolution would prefer the primary.
    private static TestMetadataV2GraphProvider CreateProvider(AccessPolicy? canonicalPolicy, AccessPolicy? secondaryPolicy)
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-layer-1", "cities", MetadataV2ObjectType.FeatureDataset, fields: Fields())
            .AddStorageBinding("binding-layer-1", "res-layer-1", "test.layers.1", storageLayerId: 1)
            .AddService(
                "svc-primary",
                CanonicalServiceName,
                protocols: [MetadataV2ServiceProtocols.Wfs20],
                accessPolicy: canonicalPolicy)
            .AddService(
                "svc-cities",
                SecondaryServiceName,
                protocols: [MetadataV2ServiceProtocols.Wfs20],
                accessPolicy: secondaryPolicy)
            .AddPublication(
                "svc-primary-layer-0",
                "svc-primary",
                "res-layer-1",
                layerIndex: 1,
                storageBindingId: "binding-layer-1",
                isPrimary: true,
                publicationType: MetadataV2LayerType.OgcCollection)
            .AddPublication(
                SecondaryPublicationId,
                "svc-cities",
                "res-layer-1",
                layerIndex: 1,
                storageBindingId: "binding-layer-1",
                publicationType: MetadataV2LayerType.OgcCollection)
            .BuildProvider();

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
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Editable = true }
        ];

    private static Wfs20Handler CreateHandler(IMetadataV2GraphProvider provider, IFeatureWriter writer)
        => Wfs20WriteTestHandler.Create(provider, writer, Substitute.For<IFeatureReader>());
}
