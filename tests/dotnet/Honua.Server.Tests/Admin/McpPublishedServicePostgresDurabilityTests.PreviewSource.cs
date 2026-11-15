// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Admin;

public sealed partial class McpPublishedServicePostgresDurabilityTests
{
    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /mcp")]
    public async Task PublicationPreview_WithTheProductionImportRegistry_RemainsReadOnly()
    {
        const string migration = "Honua.Server.Migrations.141_CreateImportedDatasetSpatialMetadata.sql";
        await using var stream = typeof(ServerCoreSchemaMigrations).Assembly.GetManifestResourceStream(migration)!;
        using var reader = new StreamReader(stream);
        await _fixture.Postgres.ExecuteDdlUnderLockAsync(
            (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", "honua", StringComparison.Ordinal));

        await PublicationPreview_ReportsTheSourceAndMetadataBase_WithoutPublishing();
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /mcp")]
    public async Task PublicationPreview_ImplicitFieldsRejectSchemaChangesBeforeProposal()
    {
        var preview = await PreviewPublicationAsync([]);
        var reference = preview.GetProperty("structuredContent").GetProperty("details")
            .GetProperty("previewReference").GetString()!;
        var revision = await ReadPersistedRevisionAsync();
        await AddPreviewSourceColumnAsync();

        var response = await ProposePreviewPublicationAsync(reference, []);

        var rejected = response.GetProperty("result").GetProperty("structuredContent");
        rejected.GetProperty("status").GetString().Should().Be("Failed", response.GetRawText());
        rejected.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        (await PublishedLayerCountAsync(_firstTable)).Should().Be(0);
        (await ReadPersistedRevisionAsync()).Should().Be(revision);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp")]
    public async Task PublicationPreview_ApprovalRechecksTheSourceBeforePublishing()
    {
        var preview = await PreviewPublicationAsync([]);
        var reference = preview.GetProperty("structuredContent").GetProperty("details")
            .GetProperty("previewReference").GetString()!;
        var response = await ProposePreviewPublicationAsync(reference, []);
        var proposal = response.GetProperty("result").GetProperty("structuredContent");
        proposal.GetProperty("status").GetString().Should().Be("RequiresApproval", response.GetRawText());
        var revision = await ReadPersistedRevisionAsync();
        await AddPreviewSourceColumnAsync();

        await HumanApprovedOperation.ApproveAndReadHandleAsync(_fixture,
            proposal.GetProperty("proposalId").GetString()!, "Failed");

        (await PublishedLayerCountAsync(_firstTable)).Should().Be(0);
        (await ReadPersistedRevisionAsync()).Should().Be(revision);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp")]
    public async Task PublicationPreview_ActuationRechecksTheSourceEvenAfterPreparation()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<ILayerPublishingService>();
        var connection = await scope.ServiceProvider.GetRequiredService<ISecureConnectionResolver>()
            .ResolveConnectionStringAsync(_connectionId);
        var request = new LayerPublishRequest
        {
            ConnectionId = _connectionId, Schema = "public", Table = _firstTable,
            LayerName = "preview layer", ServiceName = _serviceName,
            GeometryColumn = "geom", GeometryType = "Point", Srid = 4326, PrimaryKey = "id"
        };
        var preview = await ((ILayerPublicationPreviewReader)publisher).PreviewPublicationAsync(connection, request);
        request.ExpectedSourceFingerprint = preview.SourceFingerprint;
        request.ExpectedMetadataRevision = preview.MetadataBase.Revision;
        request.ExpectedMetadataEtag = preview.MetadataBase.Etag;
        var revision = await ReadPersistedRevisionAsync();
        await AddPreviewSourceColumnAsync();

        var publish = () => publisher.PublishLayerAsync(connection, request);

        await publish.Should().ThrowAsync<LayerPublishingException>().WithMessage("*preview source changed*");
        (await PublishedLayerCountAsync(_firstTable)).Should().Be(0);
        (await ReadPersistedRevisionAsync()).Should().Be(revision);
    }

    private Task AddPreviewSourceColumnAsync() => _fixture.Postgres.ExecuteDdlUnderLockAsync(
        $"ALTER TABLE public.{_firstTable} ADD COLUMN unexpected_text text");
}
