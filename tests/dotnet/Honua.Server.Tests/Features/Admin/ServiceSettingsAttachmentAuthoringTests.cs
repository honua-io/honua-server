// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin, TestProtocols.FeatureServer)]
[Operation(Operations.Configuration)]
public sealed class ServiceSettingsAttachmentAuthoringTests(ITestOutputHelper output)
{
    private const string ServiceName = "attachment-authoring-owned";
    private const string ResourceId = "attachment-authoring-resource";
    private const string AdminPassword = "attachment-authoring-test-key";
    private const string UpdatePath = "/api/v1/admin/services/attachment-authoring-owned/layers/900/metadata";

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task AttachmentIntent_RoundTripsWithoutChangingOtherMetadata_AndInvalidatesAliases()
    {
        var cache = Substitute.For<IOutputCacheStore>();
        await using var fixture = await CreateFixtureAsync(withEditing: true, cache);
        using var client = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        var before = fixture.GetCurrentV2GraphSnapshot().Graph;
        var original = before.Resources.Single(resource => resource.Metadata.Id == ResourceId);
        AssertOpenApiContract();

        foreach (var enabled in new[] { true, false })
        {
            using var response = await PutAsync(client, JsonSerializer.Serialize(new { editing = new { supportsAttachments = enabled } }));
            response.Be200Ok();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            output.WriteLine("Authoring response: " + document.RootElement.GetRawText());
            var editing = document.RootElement.GetProperty("data").GetProperty("editing");
            editing.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                ["globalIdField", "creatorField", "createdAtField", "editorField", "updatedAtField",
                    "canModify", "supportsAttachments", "supportsRelatedRecords"]);
            foreach (var name in new[] { "createdAtField", "editorField", "updatedAtField" })
            {
                editing.GetProperty(name).ValueKind.Should().Be(JsonValueKind.Null);
            }
            editing.GetProperty("supportsAttachments").GetBoolean().Should().Be(enabled);
            editing.GetProperty("globalIdField").GetString().Should().Be("globalid");
            editing.GetProperty("creatorField").GetString().Should().Be("owner");
            editing.GetProperty("canModify").GetBoolean().Should().BeFalse();
            editing.GetProperty("supportsRelatedRecords").GetBoolean().Should().BeFalse();

            var graph = fixture.GetCurrentV2GraphSnapshot().Graph;
            var resource = graph.Resources.Single(resource => resource.Metadata.Id == ResourceId);
            resource.Should().BeEquivalentTo(original with
            {
                Editing = original.Editing! with { SupportsAttachments = enabled }
            });
            graph.Publications.Should().BeEquivalentTo(before.Publications);
            graph.Services.Should().BeEquivalentTo(before.Services);
            graph.StorageBindings.Should().BeEquivalentTo(before.StorageBindings);
            graph.Resources.Where(resource => resource.Metadata.Id != ResourceId)
                .Should().BeEquivalentTo(before.Resources.Where(resource => resource.Metadata.Id != ResourceId));

            foreach (var path in new[] { $"/rest/services/{ServiceName}/FeatureServer/900", "/rest/services/attachment-alias-owned/FeatureServer/910" })
            {
                using var metadata = await client.GetAsync(path + "?f=json");
                metadata.Be200Ok();
                using var layer = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
                layer.RootElement.GetProperty("hasAttachments").GetBoolean().Should().Be(enabled);
            }
        }

        await cache.Received().EvictByTagAsync("layer:900", Arg.Any<CancellationToken>());
        await cache.Received().EvictByTagAsync("layer:910", Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task AttachmentIntent_OmittedOrNullPreservesLegacyMetadata_ExplicitValueUsesCanonicalDefaults()
    {
        await using var fixture = await CreateFixtureAsync(withEditing: false);
        using var client = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        foreach (var payload in new[] { "{}", "{\"editing\":null}", "{\"editing\":{}}", "{\"editing\":{\"supportsAttachments\":null}}" })
        {
            using var response = await PutAsync(client, payload);
            response.Be200Ok();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            output.WriteLine("Omitted/null intent response: " + document.RootElement.GetRawText());
            document.RootElement.GetProperty("data").GetProperty("editing").ValueKind.Should().Be(JsonValueKind.Null);
            fixture.GetCurrentV2GraphSnapshot().Graph.Resources.Should().BeEquivalentTo(original.Resources);
        }

        using var explicitResponse = await PutAsync(client, "{\"editing\":{\"supportsAttachments\":false}}");
        explicitResponse.Be200Ok();
        using var explicitDocument = JsonDocument.Parse(await explicitResponse.Content.ReadAsStringAsync());
        output.WriteLine("Explicit default response: " + explicitDocument.RootElement.GetRawText());
        var defaultEditing = explicitDocument.RootElement.GetProperty("data").GetProperty("editing");
        defaultEditing.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["globalIdField", "creatorField", "createdAtField", "editorField", "updatedAtField",
                "canModify", "supportsAttachments", "supportsRelatedRecords"]);
        foreach (var name in new[] { "globalIdField", "creatorField", "createdAtField", "editorField", "updatedAtField" })
        {
            defaultEditing.GetProperty(name).ValueKind.Should().Be(JsonValueKind.Null);
        }
        defaultEditing.GetProperty("canModify").GetBoolean().Should().BeTrue();
        defaultEditing.GetProperty("supportsAttachments").GetBoolean().Should().BeFalse();
        defaultEditing.GetProperty("supportsRelatedRecords").GetBoolean().Should().BeTrue();
        var updated = fixture.GetCurrentV2GraphSnapshot().Graph;
        var resource = updated.Resources.Single(resource => resource.Metadata.Id == ResourceId);
        resource.Editing.Should().BeEquivalentTo(new MetadataV2ResourceEditing { SupportsAttachments = false });
        resource.Metadata.Annotations["honua.io/attachments"].Should().Be("true");
        updated.Publications.Should().BeEquivalentTo(original.Publications);
        updated.Services.Should().BeEquivalentTo(original.Services);
        await AssertAuthorizationUnchangedAsync(fixture,
            original.Resources.Single(resource => resource.Metadata.Id == ResourceId), resource,
            original.Services.Single(service => service.Metadata.Id == "attachment-primary-service"));
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task AttachmentIntent_WhenPublicationRelinks_DoesNotChangeEitherResource()
    {
        await using var fixture = await CreateFixtureAsync(withEditing: true);
        using var client = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        var replacement = original.Resources.Single(resource => resource.Metadata.Id == "attachment-unrelated-resource");
        var activated = original with
        {
            Revision = original.Revision + 1,
            Publications = original.Publications.Select(publication => publication.Metadata.Id == "attachment-primary-publication"
                ? publication with { ResourceId = replacement.Metadata.Id, StorageBindingId = "attachment-unrelated-binding" }
                : publication).ToArray()
        };
        fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().ActivateAfterNextRead(activated);
        using var response = await PutAsync(client, "{\"editing\":{\"supportsAttachments\":true}}");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        fixture.GetCurrentV2GraphSnapshot().Graph.Resources.Should().BeEquivalentTo(original.Resources);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task AttachmentIntent_InvalidBoolean_IsRejectedWithoutMutation()
    {
        await using var fixture = await CreateFixtureAsync(withEditing: true);
        using var client = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        using var response = await PutAsync(client, "{\"editing\":{\"supportsAttachments\":\"yes\"}}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(original);
    }

    private static void AssertOpenApiContract()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the published contract must be checked against the runtime response");
        var root = directory ?? throw new InvalidOperationException("Repository root was not found.");
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Join(root.FullName, "docs", "developer", "api-specs", "admin-api.json")));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("UpdateLayerMetadataRequest").GetProperty("properties").GetProperty("editing")
            .GetProperty("$ref").GetString().Should().Be("#/components/schemas/UpdateLayerEditingRequest");
        schemas.GetProperty("LayerMetadataResponse").GetProperty("properties").GetProperty("editing")
            .GetProperty("$ref").GetString().Should().Be("#/components/schemas/MetadataV2ResourceEditing");
        var request = schemas.GetProperty("UpdateLayerEditingRequest");
        request.GetProperty("nullable").GetBoolean().Should().BeTrue();
        request.GetProperty("properties").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["supportsAttachments"]);
        var intent = request.GetProperty("properties").GetProperty("supportsAttachments");
        intent.GetProperty("type").GetString().Should().Be("boolean");
        intent.GetProperty("nullable").GetBoolean().Should().BeTrue();
        var response = schemas.GetProperty("MetadataV2ResourceEditing");
        response.GetProperty("nullable").GetBoolean().Should().BeTrue();
        var properties = response.GetProperty("properties");
        properties.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["globalIdField", "creatorField", "createdAtField", "editorField", "updatedAtField",
                "canModify", "supportsAttachments", "supportsRelatedRecords"]);
        foreach (var name in new[] { "globalIdField", "creatorField", "createdAtField", "editorField", "updatedAtField" })
        {
            properties.GetProperty(name).GetProperty("type").GetString().Should().Be("string");
            properties.GetProperty(name).GetProperty("nullable").GetBoolean().Should().BeTrue();
        }

        foreach (var name in new[] { "canModify", "supportsAttachments", "supportsRelatedRecords" })
        {
            properties.GetProperty(name).GetProperty("type").GetString().Should().Be("boolean");
            properties.GetProperty(name).GetProperty("default").GetBoolean().Should().Be(name != "supportsAttachments");
        }
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return await client.PutAsync(UpdatePath, content);
    }

    private static async Task AssertAuthorizationUnchangedAsync(
        WebAppFixture fixture, MetadataV2Resource before, MetadataV2Resource after, MetadataV2Service service)
    {
        // Exercise the actual shared authorization gate with identical allow/deny
        // policies before and after the canonical editing block is introduced.
        foreach (var allow in new[] { false, true })
        {
            var policy = new AccessPolicy
            {
                AllowAnonymous = true,
                AllowAnonymousWrite = allow,
                AllowedWriteRoles = allow ? [] : ["restricted-writer"]
            };
            var context = new DefaultHttpContext { RequestServices = fixture.Services };
            foreach (var operation in new[] { AuthorizationOperation.Insert, AuthorizationOperation.Update, AuthorizationOperation.Delete })
            {
                var originalDecision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
                    context, before with { AccessPolicy = policy }, service with { AccessPolicy = policy }, operation, CancellationToken.None);
                var updatedDecision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
                    context, after with { AccessPolicy = policy }, service with { AccessPolicy = policy }, operation, CancellationToken.None);
                originalDecision.IsAllowed.Should().Be(allow);
                updatedDecision.Should().Be(originalDecision);
            }
        }
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(bool withEditing, IOutputCacheStore? cache = null)
    {
        var provider = new TestMetadataV2GraphProvider(BuildGraph(withEditing));
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(provider);
                services.AddSingleton<IMetadataV2GraphProvider>(provider);
                services.AddSingleton<IMetadataV2GraphStore>(provider);
                services.RemoveAll<IAttachmentStore>();
                services.AddSingleton<IAttachmentStore, TestAttachmentStore>();
                if (cache is not null)
                {
                    services.RemoveAll<IOutputCacheStore>();
                    services.AddSingleton(cache);
                }
            });
        await fixture.InitializeAsync();
        return fixture;
    }

    private static MetadataV2Graph BuildGraph(bool withEditing)
    {
        var access = new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = false };
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource(ResourceId, "Attachment authoring", MetadataV2ResourceType.Table,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer },
                    new MetadataV2Field { Name = "globalid", Type = MetadataV2FieldType.Uuid },
                    new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String }
                ], accessPolicy: access, annotations: new Dictionary<string, string> { ["honua.io/attachments"] = "true" })
            .AddResource("attachment-unrelated-resource", "Unrelated", MetadataV2ResourceType.Table, accessPolicy: access)
            .AddStorageBinding("attachment-primary-binding", ResourceId, "test.layers.900", storageLayerId: 900)
            .AddStorageBinding("attachment-unrelated-binding", "attachment-unrelated-resource", "test.layers.901", storageLayerId: 901)
            .AddService("attachment-primary-service", ServiceName, protocols: [ServiceProtocols.FeatureServer], accessPolicy: access)
            .AddService("attachment-alias-service", "attachment-alias-owned", protocols: [ServiceProtocols.FeatureServer], accessPolicy: access)
            .AddPublication("attachment-primary-publication", "attachment-primary-service", ResourceId,
                layerIndex: 900, storageBindingId: "attachment-primary-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .AddPublication("attachment-alias-publication", "attachment-alias-service", ResourceId,
                layerIndex: 910, storageBindingId: "attachment-primary-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .Build();
        return graph with
        {
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == ResourceId && withEditing
                ? resource with
                {
                    Editing = new MetadataV2ResourceEditing
                    {
                        GlobalIdField = "globalid",
                        CreatorField = "owner",
                        CanModify = false,
                        SupportsAttachments = false,
                        SupportsRelatedRecords = false
                    }
                } : resource).ToArray()
        };
    }
}
