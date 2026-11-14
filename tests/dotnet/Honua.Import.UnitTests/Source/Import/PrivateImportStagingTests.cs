// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Import.FileImport;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

// Upload staging must not be readable by, or pre-creatable by, other local users.
public sealed class PrivateImportStagingTests
{
    [UnitTest]
    public async Task CopyStreamToTempFileAsync_CreatesTheStagedFileOwnerOnly()
    {
        var path = PrivateTempFile.NewPath("honua-import-staging-test-", ".geojson");
        try
        {
            await using var source = new MemoryStream([1, 2, 3]);

            var written = await MultipartParsingHelpers.CopyStreamToTempFileAsync(source, path, 1024, CancellationToken.None);

            written.Should().Be(3);
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(PrivateTempFile.OwnerReadWrite);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    public async Task CopyStreamToTempFileAsync_DoesNotReuseAPreCreatedPath()
    {
        var path = PrivateTempFile.NewPath("honua-import-staging-test-", ".geojson");
        await File.WriteAllBytesAsync(path, [9]);
        try
        {
            await using var source = new MemoryStream([1, 2, 3]);

            var act = () => MultipartParsingHelpers.CopyStreamToTempFileAsync(source, path, 1024, CancellationToken.None);

            await act.Should().ThrowAsync<IOException>();
            (await File.ReadAllBytesAsync(path)).Should().Equal(9);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
