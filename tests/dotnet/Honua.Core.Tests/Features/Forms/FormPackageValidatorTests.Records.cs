// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Forms;

public sealed partial class FormPackageValidatorTests
{
    [UnitTheory]
    [InlineData("{}", "requiredFieldMissing")]
    [InlineData("{\"name\":null}", "requiredFieldMissing")]
    [InlineData("{\"name\":42}", "fieldValueTypeMismatch")]
    [InlineData("{\"name\":\"Inspection\",\"unknown\":true}", "fieldNotInPackage")]
    public async Task ValidateRecord_InvalidAnswers_ReturnsIssue(string answers, string issue)
    {
        var result = await CreateValidator().ValidateRecordAsync(RecordVersion(CreatePackage()), (RecordValues(answers), []));

        result.IsValid.Should().BeFalse();
        result.Issues.Select(item => item.Code).Should().Contain(issue);
    }

    [UnitTest]
    public async Task ValidateRecord_RequiredZeroAndFalse_AreValues()
    {
        var resource = CreateResource(MetadataV2SpatialReference.Wgs84);
        resource = resource with
        {
            SchemaFields = [.. resource.SchemaFields, new MetadataV2Field { Name = "checked", Type = MetadataV2FieldType.Boolean }]
        };
        var package = new FormPackageDocument
        {
            Target = new FormTargetDefinition { ServiceId = "test", LayerId = 0 },
            Fields =
            [
                new FormFieldDefinition { FieldId = "count", TargetField = "count", Type = "integer", Required = true },
                new FormFieldDefinition { FieldId = "checked", TargetField = "checked", Type = "boolean", Required = true }
            ]
        };

        var result = await CreateValidator(targetResource: resource).ValidateRecordAsync(
            RecordVersion(package), (RecordValues("{\"count\":0,\"checked\":false}"), []));

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ValidateRecord_OutsidePinnedDomain_RejectsAnswer()
    {
        var package = new FormPackageDocument
        {
            Target = new FormTargetDefinition { ServiceId = "test", LayerId = 0 },
            Fields = [new FormFieldDefinition
            {
                FieldId = "category", TargetField = "category", Type = "choice",
                Domain = new FormFieldDomainDefinition
                {
                    Type = "codedValue", Choices = [new FormDomainChoice { Code = Json("\"a\""), Label = "A" }]
                }
            }]
        };

        var result = await CreateValidator().ValidateRecordAsync(RecordVersion(package), (RecordValues("{\"category\":\"b\"}"), []));

        result.Issues.Select(item => item.Code).Should().Contain("fieldValueOutsideDomain");
    }

    [UnitTest]
    public async Task ValidateRecord_RequiredAttachment_RejectsMissingEvidence()
    {
        var result = await CreateValidator().ValidateRecordAsync(
            RecordVersion(CreatePackage(includeAttachment: true)), (RecordValues("{\"name\":\"Inspection\"}"), []));

        result.Issues.Select(item => item.Code).Should().Contain("requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateRecord_Draft_RejectsEvidence()
    {
        var result = await CreateValidator().ValidateRecordAsync(
            new FormPackageVersion { Status = FormPackageStatus.Draft, Package = CreatePackage() },
            (RecordValues("{\"name\":\"Inspection\"}"), []));

        result.Issues.Select(item => item.Code).Should().Contain("packageNotPublished");
    }

    [UnitTest]
    public async Task ValidateUpload_OtherRequiredAttachmentMayBeUploadedLater()
    {
        var package = CreatePackage(includeAttachment: true);
        package = new FormPackageDocument
        {
            Target = package.Target, AttachmentPolicy = package.AttachmentPolicy, SubmitPolicy = package.SubmitPolicy,
            Fields = [.. package.Fields, new FormFieldDefinition
            {
                FieldId = "receipt", Type = "attachment", Required = true, SectionId = "main"
            }]
        };
        var result = await CreateValidator().ValidateAttachmentUploadAsync(RecordVersion(package), UploadDescriptor());

        result.IsValid.Should().BeTrue();
        var completion = await CreateValidator().ValidateRecordAsync(RecordVersion(package),
            (RecordValues("{\"name\":\"Inspection\"}"), [UploadDescriptor()]));
        completion.Issues.Select(item => item.Code).Should().Contain("requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateUpload_RequiredTransformation_RejectsUnprocessedFile()
    {
        var package = CreatePackage(includeAttachment: true);
        package = new FormPackageDocument
        {
            Target = package.Target, Fields = package.Fields, SubmitPolicy = package.SubmitPolicy,
            AttachmentPolicy = new FormAttachmentPolicy { Enabled = true, RequireExifStripping = true }
        };

        var result = await CreateValidator().ValidateAttachmentUploadAsync(RecordVersion(package), UploadDescriptor());

        result.Issues.Select(item => item.Code).Should().Contain("attachmentTransformUnavailable");
    }

    private static FormSubmissionAttachmentDescriptor UploadDescriptor() => new()
    {
        FieldId = "photo", ClientAttachmentId = "photo-1", PartName = "photo-1",
        Filename = "inspection.png", ContentType = "image/png", SizeBytes = 32
    };

    private static FormPackageVersion RecordVersion(FormPackageDocument package)
        => new() { FormId = "inspection", Version = 1, Status = FormPackageStatus.Published, Package = package };

    private static Dictionary<string, JsonElement> RecordValues(string json)
        => Json(json).EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
}
