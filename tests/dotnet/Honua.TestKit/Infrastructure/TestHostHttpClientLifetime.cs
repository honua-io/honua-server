// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Honua.TestKit.Infrastructure;

// When a handler's lifetime expires, IHttpClientFactory parks it on a cleanup timer until the
// outer handler is collected. A disposed test host still references that outer handler, and the
// parked handler's logging chain references the host, so the process-wide timer keeps every
// disposed host alive and each later host in the process boots and stops more slowly. Test hosts
// never need handler rotation for DNS changes, so their handlers live exactly as long as the host.
internal static class TestHostHttpClientLifetime
{
    internal static void Apply(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureServices(static services =>
            services.ConfigureAll<HttpClientFactoryOptions>(static options =>
                options.HandlerLifetime = Timeout.InfiniteTimeSpan));
    }
}
