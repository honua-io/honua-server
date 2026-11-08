// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Honua.Server.Tests.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class FolderOperationIntegrationTests
{
    private Guid? _publishingConnectionId;
    [IntegrationTheory]
    [Protocol(TestProtocols.Mcp)]
    [Endpoint("POST /mcp tools/call")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData(null)]
    [InlineData("maps")]
    public async Task McpPublishService_ApprovedFolderAndNamespace_PersistAndOmissionPreservesMembership(string? publicationNamespace)
    {
        var initial = await SnapshotAsync();
        var folderParameters = new Dictionary<string, string?> { ["name"] = "Publish", ["If-Match"] = initial.Etag };
        if (publicationNamespace is not null) folderParameters["namespace"] = publicationNamespace;
        await ApplyFolderToolAsync("honua_create_folder", folderParameters);
        var folder = (await SnapshotAsync()).Graph.Folders.Single();
        var arguments = await PublishArgumentsAsync(publicationNamespace);
        arguments["folderId"] = folder.Metadata.Id;
        var serviceName = arguments["serviceName"]!;

        await ApprovePublishToolAsync(arguments);
        var first = await SnapshotAsync();
        var service = first.Graph.Services.Single(candidate => candidate.Metadata.Name == serviceName);
        service.FolderId.Should().Be(folder.Metadata.Id);
        service.PublicName.Should().Be(arguments["publicName"]);
        service.Metadata.Namespace.Should().Be(publicationNamespace);
        service.Metadata.Tenant.Should().Be(publicationNamespace is null ? null : "public");
        var layers = first.Graph.Publications.Where(layer => layer.ServiceId == service.Metadata.Id).ToArray();
        layers.Should().NotBeEmpty();
        layers.Should().OnlyContain(layer => layer.Metadata.Namespace == publicationNamespace &&
            layer.Metadata.Tenant == service.Metadata.Tenant);

        arguments = await PublishArgumentsAsync(publicationNamespace);
        arguments["serviceName"] = serviceName;
        arguments["publicName"] = service.PublicName;
        await ApprovePublishToolAsync(arguments);
        var repeated = (await SnapshotAsync()).Graph.Services.Single(candidate => candidate.Metadata.Id == service.Metadata.Id);
        repeated.FolderId.Should().Be(folder.Metadata.Id);
        repeated.PublicName.Should().Be(service.PublicName);
        repeated.Metadata.Tenant.Should().Be(service.Metadata.Tenant);
        repeated.Metadata.Namespace.Should().Be(publicationNamespace);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Mcp)]
    [Endpoint("POST /mcp tools/call")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task McpPublishService_ApprovedExplicitNull_PersistsRootAndResolvedOwnership()
    {
        var arguments = await PublishArgumentsAsync("maps");
        arguments["folderId"] = null;
        await ApprovePublishToolAsync(arguments);
        var service = (await SnapshotAsync()).Graph.Services.Single(candidate => candidate.Metadata.Name == arguments["serviceName"]);
        service.FolderId.Should().BeNull();
        service.PublicName.Should().Be(arguments["publicName"]);
        service.Metadata.Namespace.Should().Be("maps");
        service.Metadata.Tenant.Should().Be("public");
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.Mcp)]
    [Endpoint("POST /mcp tools/call")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData("")]
    [InlineData("invalid/scope")]
    public async Task McpPublishService_InvalidNamespace_DoesNotPersistAService(string publicationNamespace)
    {
        var arguments = await PublishArgumentsAsync(publicationNamespace);
        var before = await SnapshotAsync();
        var result = await CallMcpToolAsync("honua_publish_service", arguments);
        if (result.TryGetProperty("proposalId", out var proposal))
        {
            await ApproveAsync(proposal.GetString()!);
            (await _proposals.GetAsync(proposal.GetString()!))!.Status.Should().Be(OperationProposalStatus.Failed);
        }
        else result.GetProperty("status").GetString().Should().NotBe("Completed");
        var after = await SnapshotAsync();
        after.Etag.Should().Be(before.Etag);
        after.Graph.Services.Should().NotContain(service => service.Metadata.Name == arguments["serviceName"]);
    }

    private async Task<Dictionary<string, string?>> PublishArgumentsAsync(string? publicationNamespace)
    {
        await McpPublicationFixtureSetup.InstallPromotionStoresAsync(_fixture);
        var connection = await CreatePublishConnectionAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var table = "mcp_publish_" + suffix;
        await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            CREATE TABLE "{_fixture.CurrentSchema}"."{table}" (id integer PRIMARY KEY, geom geometry(Point,4326));
            INSERT INTO "{_fixture.CurrentSchema}"."{table}" VALUES (1, ST_SetSRID(ST_MakePoint(1,1),4326));
            """);
        var arguments = new Dictionary<string, string?>
        {
            ["connectionId"] = connection.ToString("D"),
            ["schema"] = _fixture.CurrentSchema,
            ["table"] = table,
            ["layerName"] = "Published points " + suffix,
            ["serviceName"] = "mcp-identity-" + suffix,
            ["publicName"] = "McpPublic-" + suffix,
            ["geometryColumn"] = "geom",
            ["geometryType"] = "Point",
            ["primaryKey"] = "id"
        };
        if (publicationNamespace is not null) arguments["namespace"] = publicationNamespace;
        return arguments;
    }

    private async Task<Guid> CreatePublishConnectionAsync()
    {
        if (_publishingConnectionId is { } existing) return existing;
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        var encryption = scope.ServiceProvider.GetRequiredService<IConnectionEncryptionService>();
        var connection = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        var encrypted = await encryption.EncryptConnectionStringAsync(connection.ConnectionString);
        var dataConnection = DataConnection.CreateWithEncryptedCredentials(
            "mcp-publish-" + Guid.NewGuid().ToString("N"), connection.Host!, connection.Port,
            connection.Database!, connection.Username!, encrypted, await encryption.GetCurrentKeyVersionAsync(),
            nameof(FolderOperationIntegrationTests), sslRequired: false, sslMode: Honua.Core.Features.Security.Domain.SslMode.Disable);
        var created = await registry.CreateConnectionAsync(dataConnection);
        _publishingConnectionId = created.ConnectionId;
        return created.ConnectionId;
    }

    private async Task DeletePublishConnectionAsync()
    {
        if (_publishingConnectionId is not { } connectionId) return;
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>().DeleteConnectionAsync(connectionId);
    }

    private async Task ApprovePublishToolAsync(Dictionary<string, string?> arguments)
    {
        var before = await SnapshotAsync();
        var result = await CallMcpToolAsync("honua_publish_service", arguments);
        result.GetProperty("status").GetString().Should().Be("RequiresApproval", result.GetRawText());
        var proposalId = result.GetProperty("proposalId").GetString()!;
        (await _proposals.GetAsync(proposalId))!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
        await ApproveAsync(proposalId);
        var proposal = (await _proposals.GetAsync(proposalId))!;
        proposal.Status.Should().Be(OperationProposalStatus.Succeeded, string.Join("; ", proposal.Plan.BlockingReasons));
    }
}
