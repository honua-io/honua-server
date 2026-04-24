// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.TestKit;
using Honua.TestKit.Infrastructure;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoParquetActiveCoverageTests
{
    [Fact]
    public async Task PreviewFileAsync_GeoParquetNativeEncoding_RejectsWithExplicitWkbOnlyContract()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(encoding: "point");
        var service = PreviewImportServiceFactory.Create();

        var act = () => service.PreviewFileAsync(stream, "native-point.parquet");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*native geometry encodings*supports only WKB*");
    }

    [Fact]
    public async Task ImportFileAsync_GeoParquetNativeEncoding_ReturnsClearFailureBeforeDatabaseAccess()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(encoding: "point");
        var service = PreviewImportServiceFactory.Create();

        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "native-point.parquet",
            TableName = "native_geoparquet",
            TargetSrid = 4326,
            OverwriteExisting = true
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("native geometry encodings");
        result.ErrorMessage.Should().Contain("WKB");
    }
}
