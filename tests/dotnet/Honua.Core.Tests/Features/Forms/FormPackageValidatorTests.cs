// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Forms;

[Protocol(Protocols.Admin)]
public sealed class FormPackageValidatorTests
{
    [UnitTest]
    public async Task ValidateForPublish_WithValidPackage_ReturnsValidResult()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateForPublishAsync(CreatePackage());

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ValidateForPublish_WithExplicitNullCollections_ReturnsIssuesWithoutThrowing()
    {
        var validator = CreateValidator();
        var package = JsonSerializer.Deserialize(
            """
            {
              "schemaVersion": "honua.form-package.v1",
              "title": "Inspection Form",
              "target": { "serviceId": "test", "layerId": 0 },
              "sections": null,
              "fields": null,
              "submitPolicy": { "allowedOperations": null },
              "attachmentPolicy": null,
              "privacyPolicy": null,
              "offlinePolicy": null
            }
            """,
            FormPackageJsonContext.Default.FormPackageDocument)!;

        var result = await validator.ValidateForPublishAsync(package);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain(["fieldsRequired", "submitPolicyOperationRequired"]);
    }

    [UnitTest]
    public async Task ValidateForPublish_WithMissingDomainChoiceCode_ReturnsDomainChoiceCodeRequired()
    {
        var validator = CreateValidator();
        var package = JsonSerializer.Deserialize(
            """
            {
              "schemaVersion": "honua.form-package.v1",
              "title": "Inspection Form",
              "target": { "serviceId": "test", "layerId": 0 },
              "sections": [ { "sectionId": "main", "label": "Main", "fieldIds": ["category"] } ],
              "fields": [
                {
                  "fieldId": "category",
                  "label": "Category",
                  "type": "choice",
                  "targetField": "category",
                  "sectionId": "main",
                  "domain": { "type": "codedValue", "choices": [ { "label": "Missing code" } ] }
                }
              ],
              "submitPolicy": { "allowedOperations": ["create"] }
            }
            """,
            FormPackageJsonContext.Default.FormPackageDocument)!;

        var result = await validator.ValidateForPublishAsync(package);

        result.Issues.Select(static issue => issue.Code).Should().Contain("domainChoiceCodeRequired");
    }

    [UnitTest]
    public async Task ValidateForPublish_WithSchemaAndPolicyDrift_ReturnsExpectedIssueCodes()
    {
        var validator = CreateValidator(serviceCapabilities: ["Query", "Extract"]);
        var package = new FormPackageDocument
        {
            Title = "Inspection Form",
            Target = new FormTargetDefinition
            {
                ServiceId = "test",
                LayerId = 0
            },
            Fields =
            [
                new FormFieldDefinition
                {
                    FieldId = "name",
                    Label = "Name",
                    Type = "text",
                    TargetField = "name",
                    Required = true,
                    SectionId = "missing-section",
                    Validation =
                    [
                        new FormValidationRule
                        {
                            Type = "maxLength",
                            Parameters = Json("""{"value":300}""")
                        }
                    ],
                    Visibility = new FormConditionalRule
                    {
                        DependsOnFieldId = "name",
                        Operator = "near"
                    }
                },
                new FormFieldDefinition
                {
                    FieldId = "name-copy",
                    Label = "Name copy",
                    Type = "text",
                    TargetField = "name",
                    Required = true,
                    SectionId = "main"
                },
                new FormFieldDefinition
                {
                    FieldId = "category",
                    Label = "Category",
                    Type = "choice",
                    TargetField = "category",
                    Domain = new FormFieldDomainDefinition
                    {
                        Type = "codedValue",
                        Choices =
                        [
                            new FormDomainChoice { Code = Json("\"a\""), Label = "A" },
                            new FormDomainChoice { Code = Json("\"a\""), Label = "Duplicate A" }
                        ]
                    }
                },
                new FormFieldDefinition
                {
                    FieldId = "photo",
                    Label = "Photo",
                    Type = "attachment",
                    Required = true
                }
            ],
            Sections =
            [
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Main",
                    FieldIds = ["name", "missing-field"]
                },
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Duplicate",
                    FieldIds = []
                }
            ],
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations =
                [
                    FormSubmissionOperations.Create,
                    FormSubmissionOperations.Update,
                    FormSubmissionOperations.Delete
                ],
                AllowAttachments = false
            },
            AttachmentPolicy = new FormAttachmentPolicy
            {
                Enabled = true,
                MaxAttachmentsPerSubmission = 99,
                MaxAttachmentBytes = 99_000_000,
                MaxTotalBytes = 99_000_000,
                AllowedContentTypes = ["application/x-msdownload"],
                RequireExifStripping = true,
                Fields =
                [
                    new FormFieldAttachmentPolicy
                    {
                        FieldId = "missing-attachment",
                        Required = true
                    }
                ]
            },
            PrivacyPolicy = new FormPrivacyPolicy
            {
                PrivateFieldIds = ["missing-private-field"],
                RequiredTransformations = ["serverRedact"],
                RetentionDays = 0
            },
            OfflinePolicy = new FormOfflinePolicy
            {
                Enabled = true,
                PreferredTransports = ["custom-sync"],
                ReplicaTransportEnabled = false,
                FieldCollectionTransportEnabled = false
            }
        };

        var result = await validator.ValidateForPublishAsync(package);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain(
            "targetOperationUnsupported",
            "sectionNotFound",
            "sectionIdDuplicate",
            "sectionFieldNotFound",
            "validationMaxLengthExceedsTarget",
            "visibilityOperatorUnsupported",
            "visibilityCycle",
            "domainChoiceDuplicate",
            "attachmentsDisallowedBySubmitPolicy",
            "attachmentCountLimitInvalid",
            "attachmentSizeLimitInvalid",
            "attachmentTotalLimitInvalid",
            "attachmentContentTypeNotAllowed",
            "attachmentTransformUnsupported",
            "attachmentPolicyFieldNotFound",
            "privacyFieldNotFound",
            "privacyTransformUnsupported",
            "privacyRetentionInvalid",
            "offlineTransportUnsupported",
            "offlineTransportRequired");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithPublishedPackage_EnforcesOperationGeometryAndAttachmentPolicy()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(includeAttachment: true)
        };
        var request = new FormSubmissionRequest
        {
            Operation = FormSubmissionOperations.Update,
            Values = new Dictionary<string, JsonElement>
            {
                ["name"] = Json("\"Inspection\"")
            },
            Geometry = Json("""{"x":-157.8583,"y":21.3069,"spatialReference":{"wkid":3857}}""")
        };

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain(
            "operationNotAllowed",
            "targetFeatureIdRequired",
            "geometrySridMismatch",
            "requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithMixedCaseOperations_UsesCaseInsensitiveOperationBranches()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(allowedOperations:
            [
                FormSubmissionOperations.Create,
                FormSubmissionOperations.Update,
                FormSubmissionOperations.Delete
            ])
        };
        var update = CreateSubmissionRequest("Update");
        var delete = CreateSubmissionRequest(
            "Delete",
            values: new Dictionary<string, JsonElement>(),
            includeGeometry: false);

        var updateResult = await validator.ValidateSubmissionAsync(packageVersion, update);
        var deleteResult = await validator.ValidateSubmissionAsync(packageVersion, delete);

        updateResult.Issues.Select(static issue => issue.Code).Should().Contain("targetFeatureIdRequired");
        deleteResult.Issues.Select(static issue => issue.Code).Should().Contain("targetFeatureIdRequired");
        deleteResult.Issues.Select(static issue => issue.Code).Should().NotContain([
            "requiredFieldMissing",
            "geometryRequired"
        ]);
    }

    [UnitTest]
    public async Task ValidateSubmission_WithNonNumericPointCoordinate_ReturnsValidationIssue()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage()
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            geometry: Json("""{"x":"abc","y":"21.3069","spatialReference":{"wkid":4326}}"""));

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain("geometryCoordinateInvalid");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithArcGisWebMercatorLatestWkid_AcceptsLayerSrid()
    {
        var validator = CreateValidator(spatialReference: MetadataV2SpatialReference.WebMercator);
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage()
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            geometry: Json("""{"x":100,"y":200,"spatialReference":{"wkid":102100,"latestWkid":3857}}"""));

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeTrue();
        result.Issues.Select(static issue => issue.Code).Should().NotContain("geometrySridMismatch");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithUnparseableNumericField_ReturnsValidationIssue()
    {
        var validator = CreateValidator();
        var package = CreatePackage();
        package = new FormPackageDocument
        {
            Title = package.Title,
            Target = package.Target,
            Sections =
            [
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Main",
                    FieldIds = ["name", "count"]
                }
            ],
            Fields =
            [
                CreateNameField(),
                new FormFieldDefinition
                {
                    FieldId = "count",
                    Label = "Count",
                    Type = "integer",
                    TargetField = "count",
                    Required = true,
                    SectionId = "main"
                }
            ],
            SubmitPolicy = package.SubmitPolicy,
            AttachmentPolicy = package.AttachmentPolicy,
            OfflinePolicy = package.OfflinePolicy
        };
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = package
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            values: new Dictionary<string, JsonElement>
            {
                ["name"] = Json("\"Inspection\""),
                ["count"] = Json("1.5")
            });

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "fieldValueTypeMismatch" && issue.FieldId == "count");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithPerFieldAttachmentPolicy_EnforcesCountRequiredAndContentType()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(
                includeAttachment: true,
                attachmentFieldRequired: false,
                fieldAttachmentRequired: true,
                attachmentAllowedContentTypes: ["image/*"],
                fieldAttachmentAllowedContentTypes: ["image/png"],
                fieldAttachmentMaxCount: 1)
        };
        var missing = CreateSubmissionRequest(FormSubmissionOperations.Create);
        var invalid = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            attachments:
            [
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "photo-1",
                    ContentType = "image/jpeg",
                    SizeBytes = 100
                },
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-2",
                    FieldId = "photo",
                    PartName = "photo-2",
                    ContentType = "image/png",
                    SizeBytes = 100
                }
            ]);

        var missingResult = await validator.ValidateSubmissionAsync(packageVersion, missing);
        var invalidResult = await validator.ValidateSubmissionAsync(packageVersion, invalid);

        missingResult.Issues.Select(static issue => issue.Code).Should().Contain("requiredAttachmentMissing");
        invalidResult.Issues.Select(static issue => issue.Code).Should().Contain([
            "attachmentFieldCountExceeded",
            "attachmentContentTypeNotAllowed"
        ]);
    }

    [UnitTest]
    public async Task ValidateSubmission_DeleteWithRequiredAttachmentField_DoesNotRequireAttachment()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(
                includeAttachment: true,
                allowedOperations: [FormSubmissionOperations.Delete],
                attachmentFieldRequired: true,
                fieldAttachmentRequired: true)
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Delete,
            targetFeatureId: 101,
            values: new Dictionary<string, JsonElement>(),
            includeGeometry: false);

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeTrue();
        result.Issues.Select(static issue => issue.Code).Should().NotContain("requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateSubmission_DeleteWithAttachmentDescriptor_ReturnsAttachmentPolicyIssue()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(
                includeAttachment: true,
                allowedOperations: [FormSubmissionOperations.Delete],
                attachmentFieldRequired: false,
                fieldAttachmentRequired: false)
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Delete,
            targetFeatureId: 101,
            values: new Dictionary<string, JsonElement>(),
            includeGeometry: false,
            attachments:
            [
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "photo-1",
                    ContentType = "image/png",
                    SizeBytes = 100
                }
            ]);

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain("attachmentsNotAllowedForDelete");
        result.Issues.Select(static issue => issue.Code).Should().NotContain("requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateForPublish_WithInvalidFieldAttachmentPolicy_ReturnsExpectedIssueCodes()
    {
        var validator = CreateValidator();
        var package = CreatePackage(
            includeAttachment: true,
            attachmentAllowedContentTypes: ["image/*"],
            fieldAttachmentAllowedContentTypes: ["application/pdf"],
            fieldAttachmentMaxCount: 3);

        var result = await validator.ValidateForPublishAsync(package);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain([
            "attachmentFieldCountLimitInvalid",
            "attachmentFieldContentTypeNotAllowed"
        ]);
    }

    private static FormPackageValidator CreateValidator(
        string[]? serviceCapabilities = null,
        MetadataV2SpatialReference? spatialReference = null)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-test", Name = "test", Title = "Test service" },
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonArray(serviceCapabilities ?? ["Query", "Extract", "Create", "Update", "Delete"])
            }
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-inspections", Name = "0" },
            ServiceId = service.Metadata.Id,
            ResourceId = "resource-inspections",
            LayerIndex = 0
        };
        var resource = CreateResource(spatialReference ?? MetadataV2SpatialReference.Wgs84);
        return new FormPackageValidator(
            new StaticFormTargetMetadataResolver(new FormTargetMetadataResolution(service, publication, resource, 0)),
            Options.Create(new LimitsOptions
            {
                Attachments = new AttachmentLimits
                {
                    MaxAttachmentSize = 5_000_000,
                    MaxAttachmentsPerFeature = 5,
                    MaxTotalAttachmentSize = 20_000_000,
                    AllowedMimeTypes = "image/*,application/pdf"
                }
            }));
    }

    private static MetadataV2Resource CreateResource(MetadataV2SpatialReference spatialReference)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "resource-inspections",
                Name = "Inspections",
                Title = "Field inspections"
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = spatialReference,
                PrimaryGeometryField = "shape"
            },
            Editing = new MetadataV2ResourceEditing
            {
                SupportsAttachments = true
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 255, Nullable = true },
                new MetadataV2Field { Name = "count", Type = MetadataV2FieldType.Integer, Nullable = true },
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String, Length = 64, Nullable = true },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true }
            ]
        };

    private static FormPackageDocument CreatePackage(
        bool includeAttachment = false,
        string[]? allowedOperations = null,
        string[]? attachmentAllowedContentTypes = null,
        string[]? fieldAttachmentAllowedContentTypes = null,
        int? fieldAttachmentMaxCount = 1,
        bool attachmentFieldRequired = true,
        bool fieldAttachmentRequired = true)
        => new()
        {
            Title = "Inspection Form",
            Target = new FormTargetDefinition
            {
                ServiceId = "test",
                LayerId = 0
            },
            Sections =
            [
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Main",
                    FieldIds = includeAttachment ? ["name", "photo"] : ["name"]
                }
            ],
            Fields = includeAttachment
                ?
                [
                    CreateNameField(),
                    new FormFieldDefinition
                    {
                        FieldId = "photo",
                        Label = "Photo",
                        Type = "attachment",
                        Required = attachmentFieldRequired,
                        SectionId = "main"
                    }
                ]
                : [CreateNameField()],
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations = allowedOperations ?? [FormSubmissionOperations.Create],
                AllowAttachments = includeAttachment
            },
            AttachmentPolicy = includeAttachment
                ? new FormAttachmentPolicy
                {
                    Enabled = true,
                    MaxAttachmentsPerSubmission = 2,
                    MaxAttachmentBytes = 1_000_000,
                    MaxTotalBytes = 2_000_000,
                    AllowedContentTypes = attachmentAllowedContentTypes ?? ["image/png"],
                    Fields =
                    [
                        new FormFieldAttachmentPolicy
                        {
                            FieldId = "photo",
                            Required = fieldAttachmentRequired,
                            MaxCount = fieldAttachmentMaxCount,
                            AllowedContentTypes = fieldAttachmentAllowedContentTypes ?? ["image/png"]
                        }
                    ]
                }
                : new FormAttachmentPolicy(),
            OfflinePolicy = new FormOfflinePolicy
            {
                Enabled = true,
                PreferredTransports = ["feature-server-replica", "fieldcollection"]
            }
        };

    private static FormFieldDefinition CreateNameField()
        => new()
        {
            FieldId = "name",
            Label = "Name",
            Type = "text",
            TargetField = "name",
            Required = true,
            SectionId = "main"
        };

    private static FormSubmissionRequest CreateSubmissionRequest(
        string operation,
        long? targetFeatureId = null,
        Dictionary<string, JsonElement>? values = null,
        JsonElement? geometry = null,
        bool includeGeometry = true,
        FormSubmissionAttachmentDescriptor[]? attachments = null)
        => new()
        {
            Operation = operation,
            TargetFeatureId = targetFeatureId,
            Values = values ?? new Dictionary<string, JsonElement>
            {
                ["name"] = Json("\"Inspection\"")
            },
            Geometry = includeGeometry
                ? geometry ?? Json("""{"x":-157.8583,"y":21.3069,"spatialReference":{"wkid":4326}}""")
                : null,
            Attachments = attachments ?? []
        };

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement JsonArray(IEnumerable<string> values)
        => JsonSerializer.SerializeToElement(values);

    private sealed class StaticFormTargetMetadataResolver(FormTargetMetadataResolution resolution) : IFormTargetMetadataResolver
    {
        public Task<FormTargetMetadataResolution> ResolveAsync(
            FormTargetDefinition? target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                target is { LayerId: 0 } &&
                string.Equals(target.ServiceId, "test", StringComparison.OrdinalIgnoreCase)
                    ? resolution
                    : default);
    }
}
