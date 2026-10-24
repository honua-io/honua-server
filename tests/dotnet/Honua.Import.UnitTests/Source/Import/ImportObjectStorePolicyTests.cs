// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.ObjectStore;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class ImportObjectStorePolicyTests
{
    [UnitTheory]
    [InlineData("s3://bucket/a/../secret.geojson")]
    [InlineData("s3://bucket/a/./secret.geojson")]
    [InlineData("s3://bucket/%2e%2e/secret.geojson")]
    [InlineData("s3://bucket/a/%2e%2e/secret.geojson")]
    [InlineData("s3://bucket/%2E/%2E%2E/secret.geojson")]
    [InlineData("s3://bucket//secret.geojson")]
    public void DotSegments_AreRejectedBeforeUriCanonicalization(string sourceUri)
    {
        var admitted = ImportObjectStorePolicy.TryAdmit(
            sourceUri,
            assumeRoleArn: null,
            AllowBucket(),
            out var location,
            out var problem);

        admitted.Should().BeFalse();
        location.Should().BeNull();
        problem!.StatusCode.Should().Be(400);
        problem.Detail.Should().Be("sourceUri must not contain '.' or '..' path segments.");
    }

    [UnitTheory]
    [InlineData("s3://bucket/foo.geojson", "foo.geojson")]
    [InlineData("s3://bucket/.hidden/a.geojson", ".hidden/a.geojson")]
    [InlineData("s3://bucket/a/..secret.geojson", "a/..secret.geojson")]
    [InlineData("s3://bucket/my%20file.geojson", "my file.geojson")]
    public void OrdinaryKeys_StayAdmitted(string sourceUri, string key)
    {
        var admitted = ImportObjectStorePolicy.TryAdmit(
            sourceUri,
            assumeRoleArn: null,
            AllowBucket(),
            out var location,
            out var problem);

        admitted.Should().BeTrue(problem?.Detail);
        location!.Bucket.Should().Be("bucket");
        location.Key.Should().Be(key);
        location.SourceUri.Should().Be(sourceUri);
    }

    [UnitTest]
    public void Credentials_KeepTheExistingRefusal()
    {
        var admitted = ImportObjectStorePolicy.TryAdmit(
            "s3://user:pass@bucket/a/../secret.geojson",
            assumeRoleArn: null,
            AllowBucket(),
            out var location,
            out var problem);

        admitted.Should().BeFalse();
        location.Should().BeNull();
        problem!.Detail.Should().Be("sourceUri must not embed credentials.");
    }

    private static ImportObjectStoreOptions AllowBucket()
        => new() { AllowedBuckets = ["bucket"] };
}
