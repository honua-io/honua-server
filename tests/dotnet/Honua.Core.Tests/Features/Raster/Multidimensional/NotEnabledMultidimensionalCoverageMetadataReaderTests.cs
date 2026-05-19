// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.Multidimensional.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster.Multidimensional;

/// <summary>
/// Verifies the default not-enabled reader returns the documented unavailability
/// error rather than silently succeeding. See ADR-0039.
/// </summary>
public sealed class NotEnabledMultidimensionalCoverageMetadataReaderTests
{
    private static MultidimensionalCoverageRegistration BuildRegistration() => new()
    {
        Id = 42,
        LayerId = 1,
        Name = "sst",
        Format = MultidimensionalCoverageFormat.NetCdf4,
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "bucket",
        ObjectKey = "granule.nc4",
        Variables = Array.Empty<string>(),
        CreatedAt = DateTimeOffset.UtcNow
    };

    [UnitTest]
    public async Task ReadMetadataAsync_AlwaysThrowsReaderUnavailable()
    {
        var reader = new NotEnabledMultidimensionalCoverageMetadataReader();
        var rangeReader = new StubRangeReader();

        var act = async () => await reader.ReadMetadataAsync(rangeReader, BuildRegistration());

        var exception = await act.Should()
            .ThrowAsync<MultidimensionalCoverageReaderUnavailableException>();
        exception.Which.Message.Should().Contain("ADR-0039");
    }

    private sealed class StubRangeReader : ICloudRangeReader
    {
        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [UnitTest]
    public void SupportedFormats_IsEmpty()
    {
        var reader = new NotEnabledMultidimensionalCoverageMetadataReader();

        reader.SupportedFormats.Should().BeEmpty();
    }

    [UnitTest]
    public void ProblemCode_MatchesContract()
    {
        MultidimensionalCoverageReaderUnavailableException.ProblemCode
            .Should().Be("HONUA-COV-HDF-READER-NOT-ENABLED");
        MultidimensionalCoverageUnsupportedLayoutException.ProblemCode
            .Should().Be("HONUA-COV-HDF-UNSUPPORTED-LAYOUT");
    }
}
