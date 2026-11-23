// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

[Collection("Unit")]
public sealed class AwsS3FileStorageObjectKeyPrefixTests
{
    [UnitTheory]
    [InlineData("honua", "honua/")]
    [InlineData("/honua/", "honua/")]
    [InlineData("tenant/honua", "tenant/honua/")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void ObjectKeyPrefix_ReflectsConfiguredKeyPrefix(string? keyPrefix, string? expected)
    {
        ICloudFileStorage storage = CreateStorage(keyPrefix);

        storage.ObjectKeyPrefix.Should().Be(expected);
    }

    private static AwsS3FileStorage CreateStorage(string? keyPrefix)
    {
        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "unit-test-bucket",
                Region = "us-east-1",
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                ServiceUrl = "http://127.0.0.1:9",
                ForcePathStyle = true,
                KeyPrefix = keyPrefix
            }
        });

        return new AwsS3FileStorage(
            options,
            NullLogger<AwsS3FileStorage>.Instance,
            new InMemoryUploadProgressStore());
    }
}
