// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Server.Tests.Features.Export;

public sealed class ExportProgressTests
{
    [Theory]
    [InlineData(99, 1)]
    [InlineData(0, 0)]
    [InlineData(5, 7)]
    public void CompletedExport_ReportsCompletionWithoutChangingActualCount(long estimated, long actual)
    {
        var progress = ExportProgress.CreateInitial("export", "shapefile", "service", 1, estimated) with
        {
            Status = OperationStatus.Completed,
            ProcessedFeatures = actual
        };
        Assert.Equal(100d, progress.PercentComplete);
        Assert.Equal(actual, progress.ProcessedFeatures);
        Assert.Equal(estimated, progress.TotalFeatures);
    }

    [Fact]
    public void InProgressExport_RetainsPartialPercentage()
    {
        var progress = ExportProgress.CreateInitial("export", "csv", "service", 1, 100) with
        {
            Status = OperationStatus.Processing,
            ProcessedFeatures = 25
        };
        Assert.Equal(25d, progress.PercentComplete);
    }

    [Fact]
    public void QueuedExport_UnknownTotalHasNoPercentage()
    {
        var progress = ExportProgress.CreateInitial("export", "csv", "service", 1, 0);
        Assert.Null(progress.PercentComplete);
    }
}
