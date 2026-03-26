// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Alerts;

public sealed class AlertOptionsValidatorTests
{
    private readonly AlertOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_WithPublicHttpsDefaultWebhookUrl_ReturnsSuccess()
    {
        var options = new AlertOptions
        {
            Dispatch = new AlertDispatchOptions
            {
                DefaultWebhookUrl = "https://hooks.example.com/alerts",
                DefaultWebhookSecret = "signing-secret"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [UnitTest]
    public void Validate_WithDefaultWebhookUrlAndMissingSecret_ReturnsFailure()
    {
        var options = new AlertOptions
        {
            Dispatch = new AlertDispatchOptions
            {
                DefaultWebhookUrl = "https://hooks.example.com/alerts"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("DefaultWebhookSecret", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_WithPrivateDefaultWebhookUrl_ReturnsFailure()
    {
        var options = new AlertOptions
        {
            Dispatch = new AlertDispatchOptions
            {
                DefaultWebhookUrl = "https://localhost/webhook",
                DefaultWebhookSecret = "signing-secret"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("DefaultWebhookUrl") &&
            failure.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_WithInsecureDefaultWebhookUrl_ReturnsFailure()
    {
        var options = new AlertOptions
        {
            Dispatch = new AlertDispatchOptions
            {
                DefaultWebhookUrl = "http://hooks.example.com/alerts",
                DefaultWebhookSecret = "signing-secret"
            }
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), failure => failure.Contains("DefaultWebhookUrl") &&
            failure.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
    }
}
