// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

public partial class OidcAuthenticationOptionsValidatorTests
{
    [UnitTheory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("lifetime")]
    [InlineData("signing")]
    public void Validate_ProductionUnsafeTokenSettings_RejectsConfiguration(string setting)
    {
        var options = new OidcAuthenticationOptions();
        SetUnsafeTokenSetting(options.TokenValidation, setting);

        var result = CreateValidator(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Production", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures, failure => failure.Contains(TestSigningKey, StringComparison.Ordinal));
    }

    [UnitTheory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("lifetime")]
    [InlineData("signing")]
    [InlineData("shared-key")]
    public void Validate_DevelopmentTokenSettings_RemainSupported(string setting)
    {
        var options = new OidcAuthenticationOptions();
        SetUnsafeTokenSetting(options.TokenValidation, setting);

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [UnitTest]
    public void Validate_ProductionStaticIssuerKey_IsAccepted()
    {
        var options = new OidcAuthenticationOptions();
        options.TokenValidation.SymmetricSigningKey = TestSigningKey;

        Assert.True(CreateValidator(Environments.Production).Validate(null, options).Succeeded);
    }

    [UnitTest]
    public void Validate_ProductionShortStaticIssuerKey_RejectsConfiguration()
    {
        var options = new OidcAuthenticationOptions();
        options.TokenValidation.SymmetricSigningKey = "too-short-key";

        Assert.True(CreateValidator(Environments.Production).Validate(null, options).Failed);
    }

    [UnitTest]
    public void Validate_ProductionDefaultTokenSettings_Succeeds()
    {
        Assert.True(CreateValidator(Environments.Production).Validate(null, new OidcAuthenticationOptions()).Succeeded);
    }

    private static OidcAuthenticationOptionsValidator CreateValidator(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        using var provider = new ServiceCollection().AddSingleton(environment).BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<OidcAuthenticationOptionsValidator>(provider);
    }

    private static void SetUnsafeTokenSetting(TokenValidationOptions options, string setting)
    {
        switch (setting)
        {
            case "issuer": options.ValidateIssuer = false; break;
            case "audience": options.ValidateAudience = false; break;
            case "lifetime": options.ValidateLifetime = false; break;
            case "signing": options.ValidateIssuerSigningKey = false; break;
            case "shared-key": options.SymmetricSigningKey = TestSigningKey; break;
            default: throw new ArgumentOutOfRangeException(nameof(setting));
        }
    }

}
