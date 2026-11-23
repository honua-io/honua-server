// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Api.Features;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

public sealed partial class OgcFeaturesOwnerEditPolicyTests
{
    [UnitTheory]
    [InlineData("code")]
    [InlineData("CODE")]
    [InlineData("globalid")]
    [InlineData("GLOBALID")]
    public async Task HandlePatchFeatureAsync_NullProtectedProperty_IsRejectedBeforeWrite(string field)
    {
        var existing = ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add("name", "before").Add(field.ToLowerInvariant(), "stable");
        var result = await ExecuteProtectedPatchAsync($"{{\"properties\":{{\"{field}\":null}}}}", existing);

        result.Status.Should().Be(StatusCodes.Status400BadRequest);
        result.Batch.Should().BeNull();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlePatchFeatureAsync_EditableChangeWithProtectedFields_Succeeds(bool masked)
    {
        var existing = ImmutableDictionary<string, object?>.Empty.Add("name", "before");
        if (!masked)
        {
            existing = existing.Add("code", "A").Add("globalid", "stable-id");
        }

        var result = await ExecuteProtectedPatchAsync("{\"properties\":{\"name\":\"after\"}}", existing);

        result.Status.Should().Be(StatusCodes.Status200OK);
        result.Batch.HasValue.Should().BeTrue();
        var replacement = result.Batch.GetValueOrDefault().Updates[0];
        replacement.Attributes["name"].Should().Be("after");
        replacement.PreserveOmittedMaskedAttributes.Should().BeTrue();
    }

    [UnitTest]
    public async Task HandlePatchFeatureAsync_EditableChangeWithProtectedFields_NullRemovalOfNonnullableEditableFieldSucceeds()
    {
        var existing = ImmutableDictionary<string, object?>.Empty.Add("name", "before");

        var result = await ExecuteProtectedPatchAsync("{\"properties\":{\"name\":null}}", existing);

        result.Status.Should().Be(StatusCodes.Status200OK);
        var replacement = result.Batch!.Value.Updates[0];
        replacement.Attributes.Should().NotContainKey("name");
        replacement.ExplicitAttributeRemovals.Should().ContainSingle().Which.Should().Be("name");
    }

    [UnitTest]
    public async Task HandlePatchFeatureAsync_NullProperties_ClearsEditableFieldsAndPreservesOnlyProtectedFields()
    {
        var existing = ImmutableDictionary<string, object?>.Empty
            .Add("name", "before").Add("code", "A").Add("globalid", "stable-id");

        var result = await ExecuteProtectedPatchAsync("{\"properties\":null}", existing);

        result.Status.Should().Be(StatusCodes.Status200OK);
        var replacement = result.Batch!.Value.Updates[0];
        replacement.Attributes.Should().BeEmpty();
        replacement.PreserveOmittedMaskedAttributes.Should().BeFalse();
        replacement.PreservedAttributeNames.Should().BeEquivalentTo(["code", "globalid"]);
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("\"invalid timestamp\"")]
    public async Task HandlePatchFeatureAsync_ClientTrackingValues_AreIgnoredBeforeValidation(string value)
    {
        var existing = ImmutableDictionary<string, object?>.Empty.Add("name", "before")
            .Add("creator", "original").Add("created", DateTime.UnixEpoch);
        var result = await ExecuteProtectedPatchAsync(
            $"{{\"properties\":{{\"name\":\"after\",\"CREATED\":{value},\"updated\":{value}}}}}", existing, tracking: new MetadataV2ObjectEditing
            {
                GlobalIdField = "globalid",
                CreatorField = "creator",
                CreatedAtField = "created",
                EditorField = "editor",
                UpdatedAtField = "updated",
            });

        result.Status.Should().Be(StatusCodes.Status200OK);
        var update = result.Batch!.Value.Updates.Single();
        update.Attributes.Should().Contain("name", "after").And.NotContainKey("created").And.NotContainKey("creator");
        update.Attributes["updated"].Should().BeOfType<DateTime>();
        update.PreservedAttributeNames.Should().Contain(["creator", "created"]);
        update.ExplicitAttributeRemovals.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("application/json")]
    [InlineData("application/geo+json")]
    [InlineData("")]
    public async Task HandlePatchFeatureAsync_NonMergePatchContentType_Returns415EvenWithZeroContentLength(string contentType)
    {
        var reader = Substitute.For<IFeatureReader>();
        var existing = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty.Add("name", "before"));
        reader.GetAsync(default, default, default).ReturnsForAnyArgs((Feature?)existing);
        reader.QueryAsync(default, default, default).ReturnsForAnyArgs(QueryResult<Feature>.Create(1, [existing]));
        var writer = Substitute.For<IFeatureWriter>();
        var (_, handler) = CreateHandlers(reader, writer);
        var context = ProtectedPatchContext(string.Empty, tracking: null);
        context.Request.ContentType = string.IsNullOrEmpty(contentType) ? null : contentType;
        context.Request.ContentLength = 0;

        var result = await handler.HandlePatchFeatureAsync("pub-cities", "42", null, context, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
    }

    [UnitTest]
    public async Task HandlePatchFeatureAsync_NestedObjectProperties_AppliesRecursiveJsonMergePatch()
    {
        var existingMeta = new Dictionary<string, object?>
        {
            ["keep"] = "original",
            ["removeMe"] = "gone",
            ["nested"] = new Dictionary<string, object?>
            {
                ["innerKeep"] = 1L,
                ["innerRemove"] = 2L
            }
        };
        var existing = ImmutableDictionary<string, object?>.Empty
            .Add("name", "before")
            .Add("metadata", existingMeta);

        var result = await ExecuteProtectedPatchAsync(
            """{"properties":{"metadata":{"removeMe":null,"added":"new","nested":{"innerRemove":null,"innerAdd":3}}}}""",
            existing);

        result.Status.Should().Be(StatusCodes.Status200OK);
        var updated = result.Batch!.Value.Updates.Single();
        var mergedMeta = updated.Attributes["metadata"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        mergedMeta.Should().ContainKey("keep").WhoseValue.Should().Be("original");
        mergedMeta.Should().ContainKey("added").WhoseValue.Should().Be("new");
        mergedMeta.Should().NotContainKey("removeMe");
        var mergedNested = mergedMeta["nested"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        mergedNested.Should().ContainKey("innerKeep").WhoseValue.Should().Be(1L);
        mergedNested.Should().ContainKey("innerAdd").WhoseValue.Should().Be(3L);
        mergedNested.Should().NotContainKey("innerRemove");
    }

    [UnitTheory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Mutations_WithIfNoneMatchStarOrMatchingEtag_Return412PreconditionFailed(string method)
    {
        var reader = Substitute.For<IFeatureReader>();
        var existing = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty.Add("name", "before"));
        reader.GetAsync(default, default, default).ReturnsForAnyArgs((Feature?)existing);
        reader.QueryAsync(default, default, default).ReturnsForAnyArgs(QueryResult<Feature>.Create(1, [existing]));
        var writer = Substitute.For<IFeatureWriter>();
        var (crud, transaction) = CreateHandlers(reader, writer);
        var currentEtag = Honua.Protocols.Ogc.Api.Features.Services.OgcFeatureEntityTag.Compute(
            existing,
            new Honua.Infrastructure.Caching.ETagService());

        foreach (var ifNoneMatch in new[] { "*", currentEtag })
        {
            var context = ProtectedPatchContext("""{"type":"Feature","id":42,"geometry":null,"properties":{"name":"after"}}""", tracking: null);
            context.Request.Method = method;
            context.Request.ContentType = method == "PATCH" ? "application/merge-patch+json" : "application/geo+json";
            context.Request.Headers.IfNoneMatch = ifNoneMatch;

            var result = method switch
            {
                "PUT" => await transaction.HandleReplaceFeatureAsync("pub-cities", "42", null, context, CancellationToken.None),
                "PATCH" => await transaction.HandlePatchFeatureAsync("pub-cities", "42", null, context, CancellationToken.None),
                _ => await crud.HandleDeleteFeatureAsync("pub-cities", "42", null, context, CancellationToken.None)
            };
            await result.ExecuteAsync(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        }
    }

    private static async Task<(int Status, FeatureEditBatch? Batch)> ExecuteProtectedPatchAsync(
        string payload, ImmutableDictionary<string, object?> attributes, MetadataV2ObjectEditing? tracking = null)
    {
        var reader = Substitute.For<IFeatureReader>();
        var existing = Feature.Create(42, null, attributes);
        reader.GetAsync(default, default, default).ReturnsForAnyArgs((Feature?)existing);
        reader.QueryAsync(default, default, default).ReturnsForAnyArgs(QueryResult<Feature>.Create(1, [existing]));
        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(
            FeatureEditResult.Success(0, 1, 0, updateResults: [EditOperationResult.Success(42)]));
        var logger = Substitute.For<ILogger<OgcFeaturesTransactionHandler>>();
        logger.IsEnabled(default).ReturnsForAnyArgs(true);
        var (_, handler) = CreateHandlers(reader, writer, logger);
        var context = ProtectedPatchContext(payload, tracking);

        var result = await handler.HandlePatchFeatureAsync("pub-cities", "42", null, context, CancellationToken.None);
        await result.ExecuteAsync(context);

        logger.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<Exception>()
            .Should().BeEmpty("the PATCH fixture must reach the expected response without a caught exception");
        var calls = writer.ReceivedCalls().ToArray();
        return (context.Response.StatusCode, calls.Length == 0 ? null : (FeatureEditBatch)calls[0].GetArguments()[1]!);
    }

    private static DefaultHttpContext ProtectedPatchContext(string payload, MetadataV2ObjectEditing? tracking)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IMetadataV2GraphProvider>(ProtectedPatchProvider(tracking));
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions { DataEditorRoles = ["editor"] }));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim(ClaimTypes.Role, "editor")], "Test"));
        context.Request.Method = HttpMethods.Patch;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/ogc/features/collections/pub-cities/items/42";
        context.Request.ContentType = "application/merge-patch+json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static TestMetadataV2GraphProvider ProtectedPatchProvider(MetadataV2ObjectEditing? tracking)
    {
        MetadataV2Field[] fields =
        [
            new() { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
            new() { Name = "name", Type = MetadataV2FieldType.String, Nullable = false, Editable = true },
            new() { Name = "metadata", Type = MetadataV2FieldType.Json, Nullable = true, Editable = true },
            new() { Name = "code", Type = MetadataV2FieldType.String, Nullable = true, Editable = false },
            new() { Name = "globalid", Type = MetadataV2FieldType.String, Nullable = true, Editable = true },
            new() { Name = "creator", Type = MetadataV2FieldType.String, Nullable = true, Editable = false },
            new() { Name = "created", Type = MetadataV2FieldType.DateTime, Nullable = true, Editable = false },
            new() { Name = "editor", Type = MetadataV2FieldType.String, Nullable = true, Editable = false },
            new() { Name = "updated", Type = MetadataV2FieldType.DateTime, Nullable = true, Editable = false }
        ];
        if (tracking is null)
        {
            fields = fields.Where(field => field.Name is not ("creator" or "created" or "editor" or "updated")).ToArray();
        }
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource", "cities", MetadataV2ObjectType.FeatureDataset, fields: fields)
            .AddStorageBinding("binding", "resource", "test.layers.7", storageLayerId: 7)
            .AddService("service", "cities", protocols: [ServiceProtocols.OgcFeatures])
            .AddPublication("pub-cities", "service", "resource", layerIndex: 1,
                storageBindingId: "binding", publicationType: MetadataV2LayerType.OgcCollection)
            .Build();
        return new TestMetadataV2GraphProvider(graph with
        {
            Resources = graph.Resources.Select(resource => resource with
            {
                Editing = tracking ?? new MetadataV2ObjectEditing { GlobalIdField = "globalid" }
            }).ToArray()
        });
    }
}
