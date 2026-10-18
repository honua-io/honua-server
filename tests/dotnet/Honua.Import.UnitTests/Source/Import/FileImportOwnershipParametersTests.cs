// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Import.FileImport.Durable;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class FileImportOwnershipParametersTests
{
    [UnitTheory]
    [InlineData("tenant-a")]
    [InlineData(null)]
    public void DurableParameters_RetainAcceptingAuthorityAcrossWorkerReconstruction(string? tenant)
    {
        var ownership = new ImportOperationOwnership("apikey:api-key:owner", tenant);
        var original = new ImportRequest
        {
            CloudFileId = "shared-file",
            FileName = "roads.geojson",
            TableName = "roads",
            Ownership = ownership,
        };
        var parameters = FileImportJobParameters.Build(original, 123);
        FileImportJobParameters.TryParse(parameters, out var restored, out var size, out var error).Should().BeTrue();
        error.Should().BeNull();
        size.Should().Be(123);
        restored!.Ownership.Should().Be(ownership);

        parameters.Remove(FileImportJobParameters.OwnerActorId);
        FileImportJobParameters.TryParse(parameters, out var legacy, out _, out _).Should().BeTrue();
        legacy!.Ownership.Should().BeNull("a tenant alone cannot establish legacy ownership");
    }
}
