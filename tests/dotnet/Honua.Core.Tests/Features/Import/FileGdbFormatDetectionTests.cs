// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
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
}
