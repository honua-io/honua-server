// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Import;

public sealed class ImportBackgroundSlotTests
{
    [UnitTest]
    public void TryReserveBackgroundSlot_StopsAtConcurrentPlusQueueDepth()
    {
        var limits = new ImportLimits
        {
            MaxConcurrentBackgroundJobs = 1,
            MaxBackgroundJobQueueDepth = 1
        };
        using var service = new UniversalImportJobService(
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IUniversalProgressStore>(),
            Mock.Of<IPerformanceMonitor>(),
            NullLogger<UniversalImportJobService>.Instance,
            licensePolicy: null,
            limits: limits);

        service.TryReserveBackgroundSlot().Should().BeTrue();
        service.TryReserveBackgroundSlot().Should().BeTrue();
        service.TryReserveBackgroundSlot().Should().BeFalse();
    }
}
