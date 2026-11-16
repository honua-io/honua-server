// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsSelfCheckRegistrationTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Registration_LoadedCertificateAndDisabledOidc_AreReflectedInSource()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(10);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new OpsKeyRingCertificateLifetime(expiry));
        services.Configure<OidcAuthenticationOptions>(options => options.Enabled = false);
        services.AddOpsSelfChecks(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var source = provider.GetRequiredService<IOpsSelfCheckSource>();
        Assert.Equal(expiry, source.CertificateExpiresAt);
        Assert.Empty(await source.ReadIdentityProvidersAsync(default));
        Assert.Same(provider.GetRequiredService<OpsSelfChecks>(), provider.GetRequiredService<OpsSelfChecks>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is OpsSelfCheckSampler);
    }
}
