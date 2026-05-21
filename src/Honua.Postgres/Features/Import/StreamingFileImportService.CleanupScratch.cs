// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Postgres.Features.Import;

internal sealed partial class StreamingFileImportService
{
    private void CleanupShapefileScratch(ShapefileScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupShapefileScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupShapefileScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            ShapefileLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupGeoPackageScratch(GeoPackageScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupGeoPackageScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupKmzScratch(KmzScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupKmzScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupGeoPackageScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            GeoPackageLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupKmzScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            KmzLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupFileGdbScratch(FileGdbScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupFileGdbScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupFileGdbScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            FileGdbLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupGeoParquetScratch(GeoParquetScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        // Only dispose stream and directory if we created a scratch copy.
        // When ScratchDir is null, the scratch wraps the caller's original seekable
        // stream which is disposed by the caller.
        if (scratch.ScratchDir != null)
        {
            scratch.Dispose();
            CleanupGeoParquetScratchDirectory(scratch.ScratchDir);
        }
    }

    private void CleanupGeoParquetScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            GeoParquetLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }
}
