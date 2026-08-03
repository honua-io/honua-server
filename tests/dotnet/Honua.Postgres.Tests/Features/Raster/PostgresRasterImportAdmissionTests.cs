// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgresRasterImportAdmissionTests
{
    [Fact]
    public async Task ImportAsync_FileAboveDirectPayloadLimit_RejectsBeforeOpeningDatabase()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-raster-{Guid.NewGuid():N}.tif");
        try
        {
            await using (var file = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous))
            {
                file.SetLength(FileSizeConstants.MaxDirectRasterImportSize + 1);
            }

            var connectionProvider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
            var service = new PostgresRasterImportService(
                connectionProvider,
                Substitute.For<ICrsDetectionService>(),
                NullLogger<PostgresRasterImportService>.Instance);

            var result = await service.ImportAsync(new RasterImportRequest
            {
                LayerId = 1,
                Name = "oversized",
                FilePath = filePath,
                FileName = "oversized.tif",
                Format = SupportedRasterFormat.GeoTiff,
                TileZoomLevels = [],
                OverviewFactors = []
            });

            result.Success.Should().BeFalse();
            await connectionProvider.DidNotReceiveWithAnyArgs().OpenConnectionAsync(default);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
