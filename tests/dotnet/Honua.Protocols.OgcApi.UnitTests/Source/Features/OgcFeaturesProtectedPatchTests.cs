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
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    private static async Task<(int Status, FeatureEditBatch? Batch)> ExecuteProtectedPatchAsync(
        string payload, ImmutableDictionary<string, object?> attributes)
    {
        var reader = Substitute.For<IFeatureReader>();
        var existing = Feature.Create(42, null, attributes);
        reader.GetAsync(default, default, default).ReturnsForAnyArgs((Feature?)existing);
        reader.QueryAsync(default, default, default).ReturnsForAnyArgs(QueryResult<Feature>.Create(1, [existing]));
        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(
            FeatureEditResult.Success(0, 1, 0, updateResults: [EditOperationResult.Success(42)]));
        var (_, handler) = CreateHandlers(reader, writer);
        var context = ProtectedPatchContext(payload);

        var result = await handler.HandlePatchFeatureAsync("pub-cities", "42", null, context, CancellationToken.None);
        await result.ExecuteAsync(context);

        var calls = writer.ReceivedCalls().ToArray();
        return (context.Response.StatusCode, calls.Length == 0 ? null : (FeatureEditBatch)calls[0].GetArguments()[1]!);
    }

    private static DefaultHttpContext ProtectedPatchContext(string payload)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMetadataV2GraphProvider>(ProtectedPatchProvider());
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

    private static TestMetadataV2GraphProvider ProtectedPatchProvider()
    {
        MetadataV2Field[] fields =
        [
            new() { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
            new() { Name = "name", Type = MetadataV2FieldType.String, Nullable = false, Editable = true },
            new() { Name = "code", Type = MetadataV2FieldType.String, Nullable = true, Editable = false },
            new() { Name = "globalid", Type = MetadataV2FieldType.String, Nullable = true, Editable = true }
        ];
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource", "cities", MetadataV2ResourceType.FeatureDataset, fields: fields)
            .AddStorageBinding("binding", "resource", "test.layers.7", storageLayerId: 7)
            .AddService("service", "cities", protocols: [ServiceProtocols.OgcFeatures])
            .AddPublication("pub-cities", "service", "resource", layerIndex: 1,
                storageBindingId: "binding", publicationType: MetadataV2PublicationType.OgcCollection)
            .Build();
        return new TestMetadataV2GraphProvider(graph with
        {
            Resources = graph.Resources.Select(resource => resource with
            {
                Editing = new MetadataV2ResourceEditing { GlobalIdField = "globalid" }
            }).ToArray()
        });
    }
}
