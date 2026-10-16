// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.FileStorage;

namespace Honua.Aws.UnitTests;

public sealed class AwsS3MultipartDecisionTests
{
    private const int Mebibyte = 1024 * 1024;
    private const long FiftyGiB = 50L * 1024 * 1024 * 1024;

    [Fact]
    public void RequiresMultipartUpload_WhenLengthIsUnknownOrAboveFiveGiB()
    {
        Assert.True(AwsS3FileStorage.RequiresMultipartUpload(conditional: false, knownLength: null));
        Assert.True(AwsS3FileStorage.RequiresMultipartUpload(
            conditional: false,
            knownLength: AwsS3FileStorage.S3SinglePutMaxBytes + 1));
        Assert.False(AwsS3FileStorage.RequiresMultipartUpload(
            conditional: false,
            knownLength: AwsS3FileStorage.S3SinglePutMaxBytes));
        Assert.False(AwsS3FileStorage.RequiresMultipartUpload(conditional: false, knownLength: 1024));
        Assert.False(AwsS3FileStorage.RequiresMultipartUpload(conditional: true, knownLength: null));
        Assert.False(AwsS3FileStorage.RequiresMultipartUpload(
            conditional: true,
            knownLength: AwsS3FileStorage.S3SinglePutMaxBytes + 1));
    }

    [Fact]
    public void MultipartPartSize_CoversTheDefaultImportLimitInSixteenMebibyteParts()
    {
        Assert.Equal(16 * Mebibyte, AwsS3FileStorage.MultipartPartSize(null));
        Assert.Equal(16 * Mebibyte, AwsS3FileStorage.MultipartPartSize(FiftyGiB));
        Assert.Equal(16 * Mebibyte, AwsS3FileStorage.MultipartPartSize(AwsS3FileStorage.S3SinglePutMaxBytes + 1));
    }

    [Fact]
    public void MultipartPartSize_GrowsInMebibyteStepsAndStaysUnderThePartCap()
    {
        var length = 200L * 1024 * 1024 * 1024;
        var part = AwsS3FileStorage.MultipartPartSize(length);

        Assert.True(part >= 16 * Mebibyte);
        Assert.Equal(0, part % Mebibyte);
        Assert.True((long)part * 10_000 >= length);
    }

    [Fact]
    public void MultipartPartSize_RejectsAnObjectThatCannotFitInOneUpload()
    {
        var length = 128L * Mebibyte * 10_000 + 1;

        var error = Assert.Throws<InvalidOperationException>(() => AwsS3FileStorage.MultipartPartSize(length));

        Assert.Contains("too large", error.Message, StringComparison.Ordinal);
    }
}
