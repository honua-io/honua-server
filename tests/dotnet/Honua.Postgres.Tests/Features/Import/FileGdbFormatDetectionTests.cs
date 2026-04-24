// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class FileGdbFormatDetectionTests
{
    [Fact]
    public void DetectFormat_FileGdb_IsZipOnly()
    {
        var service = PreviewImportServiceFactory.Create();

        service.DetectFormat("sample.gdb.zip").Should().Be(SupportedFileFormat.FileGdb);
        service.DetectFormat("sample.gdb").Should().BeNull();
        service.GetSupportedExtensions().Should().Contain(".gdb.zip");
        service.GetSupportedExtensions().Should().NotContain(".gdb");
    }
}
