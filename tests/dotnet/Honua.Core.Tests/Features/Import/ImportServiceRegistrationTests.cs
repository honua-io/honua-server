// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Raster.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Honua.Core.Tests.Features.Import;

public sealed class ImportServiceRegistrationTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void AddImportSuggestionsCore_CoverageImport_PreservesScopeAndClientConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IRasterImportService>());
        services.AddImportSuggestionsCore();
        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<OgcCoverageImportService>();
        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<OgcCoverageImportService>());
        Assert.NotSame(first, secondScope.ServiceProvider.GetRequiredService<OgcCoverageImportService>());

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(OgcCoverageImportService));
        Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
        Assert.Equal("HonuaServer/1.0", client.DefaultRequestHeaders.UserAgent.ToString());
    }
}
