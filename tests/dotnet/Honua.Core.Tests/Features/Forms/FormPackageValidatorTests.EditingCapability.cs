// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Forms;

public sealed partial class FormPackageValidatorTests
{
    [UnitTheory]
    [InlineData("Editing")]
    [InlineData("FeatureServer.Editing")]
    public async Task ValidateForPublish_EditingCapability_SupportsEveryEditOperation(string editingToken)
    {
        var validator = CreateValidator(serviceCapabilities: ["Query", editingToken]);
        var package = CreatePackage(allowedOperations:
            [FormSubmissionOperations.Create, FormSubmissionOperations.Update, FormSubmissionOperations.Delete]);

        var result = await validator.ValidateForPublishAsync(package);

        result.Issues.Select(static issue => issue.Code).Should().NotContain("targetOperationUnsupported");
    }

    [UnitTest]
    public async Task ValidateForPublish_QueryOnlyCapabilities_StillRefuseEditOperations()
    {
        var validator = CreateValidator(serviceCapabilities: ["Query", "Extract"]);

        var result = await validator.ValidateForPublishAsync(CreatePackage());

        result.Issues.Select(static issue => issue.Code).Should().Contain("targetOperationUnsupported");
    }
}
