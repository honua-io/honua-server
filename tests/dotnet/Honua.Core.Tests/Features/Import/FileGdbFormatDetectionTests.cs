// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class FileGdbFormatDetectionTests
{
    [Fact]
    public void DetectFormat_FileGdb_IsZipOnly()
    {
        var service = new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance);

        service.DetectFormat("sample.gdb.zip").Should().Be(SupportedFileFormat.FileGdb);
        service.DetectFormat("sample.gdb").Should().BeNull();
        service.GetSupportedExtensions().Should().Contain(".gdb.zip");
        service.GetSupportedExtensions().Should().NotContain(".gdb");
    }

    [Fact]
    public void DetectFormat_UnsupportedAdvertisedFormats_AreRejected()
    {
        var service = new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance);

        service.DetectFormat("sample.gml").Should().BeNull();
        service.DetectFormat("sample.twkb").Should().BeNull();
        service.GetSupportedExtensions().Should().NotContain(".gml");
        service.GetSupportedExtensions().Should().NotContain(".twkb");
    }
}
