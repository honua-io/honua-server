// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_MappedUpdateRejectedByConstraintRule_DoesNotWriteCompletion()
    {
        await MapInspectionFieldsAsync();
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            AttributeRules = [new MetadataV2AttributeRule
            {
                Name = "condition cap", Type = MetadataV2AttributeRuleType.Constraint,
                ScriptExpression = "$feature.population < 3", TriggeringEvents = ["update"], IsEnabled = true,
            }],
        });
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);
        var before = await ReadMappedFeatureAsync(assetId);
        var request = Completion(3, Later, 4);

        var refused = await CompleteAsync(taskId, request);

        refused.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("was rejected by the layer");
        await AssertMappedRollbackAsync(taskId, (assetId, before), request.CommandId!.Value);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_MappedValueOutsideFieldDomain_DoesNotWriteCompletion()
    {
        await MapInspectionFieldsAsync();
        await RestrictConditionDomainAsync();
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);
        var before = await ReadMappedFeatureAsync(assetId);
        var request = Completion(3, Later, 4);

        var refused = await CompleteAsync(taskId, request);

        refused.StatusCode.Should().NotBe(HttpStatusCode.OK);
        await AssertMappedRollbackAsync(taskId, (assetId, before), request.CommandId!.Value);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_MappedUpdate_StampsEditorTracking()
    {
        await MapInspectionFieldsAsync();
        await _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            Editing = (resource.Editing ?? new MetadataV2ObjectEditing()) with
            {
                EditorField = "last_edited_user",
                UpdatedAtField = "last_edited_date",
            },
            SchemaFields =
            [
                .. resource.SchemaFields,
                .. new[]
                {
                    new MetadataV2Field { Name = "last_edited_user", Type = MetadataV2FieldType.String, Nullable = true },
                    new MetadataV2Field { Name = "last_edited_date", Type = MetadataV2FieldType.DateTime, Nullable = true },
                }.Where(field => resource.SchemaFields.All(existing => existing.Name != field.Name)),
            ],
        });
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);

        var accepted = await CompleteAsync(taskId, Completion(3, Later, 4));

        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        using var stored = JsonDocument.Parse((string)(await ReadStoredMappedFeatureAsync(assetId))!);
        stored.RootElement.GetProperty("last_edited_user").GetString().Should().NotBeNullOrEmpty();
        stored.RootElement.GetProperty("last_edited_date").ValueKind.Should().NotBe(JsonValueKind.Null);
        stored.RootElement.GetProperty("population").GetInt32().Should().Be(4);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_OlderObservationOutsideFieldDomain_KeepsEvidenceWithoutWrite()
    {
        await MapInspectionFieldsAsync();
        await RestrictConditionDomainAsync();
        var assetId = _assets[0].AssetId;
        var newer = await StartedInspectionAsync(assetId);
        var older = await StartedInspectionAsync(assetId);
        var accepted = await CompleteAsync(newer, Completion(3, Later, 2));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        var after = await ReadMappedFeatureAsync(assetId);

        var late = await CompleteAsync(older, Completion(3, Earlier, 4));

        late.StatusCode.Should().Be(HttpStatusCode.OK, await late.Content.ReadAsStringAsync());
        (await ReadMappedFeatureAsync(assetId)).Should().Be(after);
        (await SummaryAsync(assetId)).Should().Be((2, 1L));
    }

    private Task RestrictConditionDomainAsync()
        => _fixture.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            SchemaFields = [.. resource.SchemaFields.Select(field => field.Name == "population"
                ? field with
                {
                    Domain = new MetadataV2FieldDomain
                    {
                        Name = "ConditionDomain",
                        Type = "codedValue",
                        CodedValues =
                        [
                            new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement(1), Name = "Poor" },
                            new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement(2), Name = "Good" },
                        ],
                    },
                }
                : field)],
        });
}
