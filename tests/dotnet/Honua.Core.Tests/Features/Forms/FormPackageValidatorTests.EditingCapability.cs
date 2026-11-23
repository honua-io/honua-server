// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Forms;

public sealed partial class FormPackageValidatorTests
{
    private static readonly string[] EveryEditOperation =
        [FormSubmissionOperations.Create, FormSubmissionOperations.Update, FormSubmissionOperations.Delete];

    [UnitTest]
    public async Task ValidateForPublish_EditingCapability_SupportsEveryEditOperation()
    {
        var validator = CreateValidator(serviceCapabilities: ["Query", MetadataV2EditCapabilities.Editing]);

        var result = await validator.ValidateForPublishAsync(CreatePackage(allowedOperations: EveryEditOperation));

        result.Issues.Select(static issue => issue.Code).Should().NotContain("targetOperationUnsupported");
    }

    [UnitTest]
    public async Task ValidateForPublish_QueryOnlyCapabilities_RefuseEditOperations()
    {
        var validator = CreateValidator(serviceCapabilities: ["Query", "Extract"]);

        var result = await validator.ValidateForPublishAsync(CreatePackage());

        result.Issues.Select(static issue => issue.Code).Should().Contain("targetOperationUnsupported");
    }

    [UnitTest]
    public async Task ValidateForPublish_PublicationCapabilitiesOverrideServiceEditing_RefuseEditOperations()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-test", Name = "test" },
            Options = new Dictionary<string, JsonElement> { ["capabilities"] = JsonArray(["Query", MetadataV2EditCapabilities.Editing]) }
        };
        var publication = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-inspections", Name = "0" },
            ServiceId = service.Metadata.Id,
            ResourceId = "resource-inspections",
            Capabilities = ["Query"]
        };
        var validator = CreateValidator(new FormTargetMetadataResolution(
            service, publication, CreateResource(MetadataV2SpatialReference.Wgs84), 0));

        var result = await validator.ValidateForPublishAsync(CreatePackage(allowedOperations: EveryEditOperation));

        result.Issues.Select(static issue => issue.Code).Should().Contain("targetOperationUnsupported");
    }
}
