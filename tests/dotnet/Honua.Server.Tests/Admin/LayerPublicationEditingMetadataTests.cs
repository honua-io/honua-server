// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishRetainedTable_PreservesGlobalIdAndAttachmentMetadataWithoutEnablingEdits(bool supportsAttachments)
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"ALTER TABLE public.{_tableName} ADD COLUMN stable_id uuid NOT NULL DEFAULT '090cfe5f-e253-4e2e-ae8c-0b8f526ff310'::uuid", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var request = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = _tableName,
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            GlobalIdField = "STABLE_ID",
            SupportsAttachments = supportsAttachments,
            ServiceName = _serviceName
        };
        var response = await _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(request, options: _jsonOptions));
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
        _layerId = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions)!.Data!.StorageLayerId;

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.Single(candidate =>
            candidate.LayerIndex == _layerId && candidate.PublicationType == MetadataV2LayerType.EsriFeatureLayer);
        var resource = snapshot.Graph.Resources.Single(candidate => candidate.Metadata.Id == publication.ResourceId);
        resource.Editing!.GlobalIdField.Should().Be("stable_id");
        resource.Editing.SupportsAttachments.Should().Be(supportsAttachments);
        resource.Editing.CanModify.Should().BeFalse();

        var metadataResponse = await _client.GetAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        metadata.RootElement.GetProperty("globalIdField").GetString().Should().Be("stable_id");
        metadata.RootElement.GetProperty("hasAttachments").GetBoolean().Should().Be(supportsAttachments);
        metadata.RootElement.GetProperty("capabilities").GetString().Should().NotContain("Editing");
        var field = metadata.RootElement.GetProperty("fields").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "stable_id");
        field.GetProperty("type").GetString().Should().Be("esriFieldTypeGlobalID");
        field.GetProperty("editable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("POST /api/v1/admin/forms")]
    [Endpoint("POST /api/v1/admin/forms/{formId}/versions/{formVersion}/validate")]
    public async Task PublishManagedEditableLayer_WithAttachments_SetsCanModifyTrueAndPassesFormValidation()
    {
        await UseServerFeatureStoreConnectionAsync();

        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = _tableName,
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = _idNamePopulationFields,
            StorageMode = "managed",
            SupportsAttachments = true,
            Capabilities = ["Query", "Extract", "Create", "Update", "Delete"],
            ServiceName = _serviceName
        });
        _layerId = published.StorageLayerId;

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.Single(candidate =>
            candidate.LayerIndex == _layerId && candidate.PublicationType == MetadataV2LayerType.EsriFeatureLayer);
        var resource = snapshot.Graph.Resources.Single(candidate => candidate.Metadata.Id == publication.ResourceId);
        resource.Editing.Should().NotBeNull();
        resource.Editing!.SupportsAttachments.Should().BeTrue();
        resource.Editing.CanModify.Should().BeTrue();

        var validation = await CreateAndValidateManagedFormAsync(_serviceName, _layerId.Value);
        validation.IsValid.Should().BeTrue();
    }

    private async Task<FormPackageValidationResult> CreateAndValidateManagedFormAsync(string serviceId, int layerId)
    {
        var formDoc = CreateManagedLayerFormPackage(serviceId, layerId);
        using var draftContent = new StringContent(
            JsonSerializer.Serialize(formDoc, FormPackageJsonContext.Default.FormPackageDocument),
            Encoding.UTF8, "application/json");
        using var draftResponse = await _client.PostAsync("/api/v1/admin/forms", draftContent);
        var draftPayload = await draftResponse.Content.ReadAsStringAsync();
        draftResponse.StatusCode.Should().Be(HttpStatusCode.Created, draftPayload);
        var draft = JsonSerializer.Deserialize(draftPayload, FormPackageJsonContext.Default.FormPackageVersion)!;

        using var validateResponse = await _client.PostAsync($"/api/v1/admin/forms/{draft.FormId}/versions/1/validate", content: null);
        var validatePayload = await validateResponse.Content.ReadAsStringAsync();
        validateResponse.StatusCode.Should().Be(HttpStatusCode.OK, validatePayload);
        return JsonSerializer.Deserialize(validatePayload, FormPackageJsonContext.Default.FormPackageValidationResult)!;
    }

    private static FormPackageDocument CreateManagedLayerFormPackage(string serviceId, int layerId) => new()
    {
        Title = "Managed layer attachments form",
        Target = new FormTargetDefinition { ServiceId = serviceId, LayerId = layerId },
        Sections = [new FormSectionDefinition { SectionId = "main", Label = "Main", FieldIds = ["name"] }],
        Fields = [new FormFieldDefinition { FieldId = "name", Label = "Name", Type = "text", TargetField = "name", Required = true, SectionId = "main" }],
        SubmitPolicy = new FormSubmitPolicy { AllowedOperations = [FormSubmissionOperations.Create], RequiresGeometry = true, AllowAttachments = false }
    };
}
