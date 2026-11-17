// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Startup;

public sealed class OidcProductionConfigurationTests
{
    [UnitTheory]
    [InlineData("ValidateIssuer", "false")]
    [InlineData("ValidateAudience", "false")]
    [InlineData("ValidateLifetime", "false")]
    [InlineData("ValidateIssuerSigningKey", "false")]
    [InlineData("SymmetricSigningKey", "development-only-test-signing-key-32-bytes")]
    public void StartupOptions_UnsafeProductionConfiguration_FailsValidation(string setting, string value)
    {
        using var provider = CreateProvider(Environments.Production, setting, value);

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value);
    }

    [UnitTest]
    public void StartupOptions_DefaultProductionConfiguration_Succeeds()
    {
        using var provider = CreateProvider(Environments.Production, "ValidateIssuer", "true");

        Assert.True(provider.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value.TokenValidation.ValidateIssuer);
    }

    [UnitTest]
    public void StartupOptions_DevelopmentSharedKey_RemainsSupported()
    {
        using var provider = CreateProvider(Environments.Development, "SymmetricSigningKey",
            "development-only-test-signing-key-32-bytes");

        Assert.NotNull(provider.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value.TokenValidation.SymmetricSigningKey);
    }

    private static ServiceProvider CreateProvider(string environmentName, string setting, string value)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Oidc:TokenValidation:{setting}"] = value
        });
        StartupConfigurationHelpers.RegisterConfigurationValidators(builder.Services);
        builder.Services.AddHonuaAuthenticationOptions(builder.Configuration, builder.Environment);
        return builder.Services.BuildServiceProvider();
    }
}
