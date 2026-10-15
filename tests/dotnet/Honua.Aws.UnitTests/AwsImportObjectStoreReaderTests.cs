// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.FileStorage;

namespace Honua.Aws.UnitTests;

public sealed class AwsImportObjectStoreReaderTests
{
    [Fact]
    public void BuildAssumeRoleRequest_SetsExternalIdOnlyWhenConfigured()
    {
        var withId = AwsImportObjectStoreReader.BuildAssumeRoleRequest(
            "arn:aws:iam::123456789012:role/honua-import",
            "external-from-config");
        Assert.Equal("external-from-config", withId.ExternalId);
        Assert.Equal(AwsImportObjectStoreReader.RoleSessionName, withId.RoleSessionName);
        Assert.Equal(900, withId.DurationSeconds);

        var withoutId = AwsImportObjectStoreReader.BuildAssumeRoleRequest(
            "arn:aws:iam::123456789012:role/honua-import",
            "  ");
        Assert.True(string.IsNullOrEmpty(withoutId.ExternalId));
    }
}
