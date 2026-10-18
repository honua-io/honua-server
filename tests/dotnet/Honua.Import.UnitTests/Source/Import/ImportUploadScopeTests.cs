// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Import.FileImport;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class ImportUploadScopeTests
{
    [UnitTest]
    public void SameClientId_DifferentActorOrTenant_HasDifferentStorageAndCannotBeProjected()
    {
        var owner = new ImportUploadScope(new("actor-a", "tenant-a"));
        var others = new[]
        {
            new ImportUploadScope(new("actor-b", "tenant-a")),
            new ImportUploadScope(new("actor-a", "tenant-b")),
            new ImportUploadScope(new("actor-a", null)),
        };
        var id = owner.StorageId("client-attempt");
        ImportUploadScope.IsClientId(id).Should().BeFalse("legacy callers cannot write into the internal namespace");
        var progress = UploadProgress.CreateInitial(id, "private.geojson", 100);
        owner.Project(progress)!.UploadId.Should().Be("client-attempt");
        owner.Project([progress, UploadProgress.CreateInitial("legacy", "legacy.geojson", 1)])
            .Should().ContainSingle().Which.UploadId.Should().Be("client-attempt");
        foreach (var other in others)
        {
            other.StorageId("client-attempt").Should().NotBe(id);
            other.Project(progress).Should().BeNull();
            other.StorageId(id).Should().NotBe(id, "a caller cannot bypass scoping by supplying a storage ID");
        }
        new ImportUploadScope(new ImportOperationOwnership("actor-a", "tenant-a"))
            .StorageId("client-attempt").Should().Be(id, "reopening uses the same authenticated scope");
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("../victim")]
    [InlineData("first second")]
    [InlineData("line\nend")]
    public void InvalidClientId_IsRejected(string id) => ImportUploadScope.IsClientId(id).Should().BeFalse();
}
