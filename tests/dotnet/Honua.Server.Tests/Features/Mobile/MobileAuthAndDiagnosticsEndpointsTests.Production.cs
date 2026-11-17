// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Honua.Server.Features.Mobile.Auth;

namespace Honua.Server.Tests.Features.Mobile;

public sealed partial class MobileAuthAndDiagnosticsEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /oauth/token")]
    public async Task TokenRefresh_ConfiguredFixtureTokensInProduction_ReturnsUnavailable()
    {
        var environment = _fixture.Services.GetRequiredService<IHostEnvironment>();
        var options = _fixture.Services.GetRequiredService<IOptions<MobileAuthOptions>>().Value;
        var originalEnvironment = environment.EnvironmentName;
        options.RefreshToken = "configured-refresh";
        options.AccessToken = "configured-access";
        environment.EnvironmentName = Environments.Production;
        try
        {
            using var content = Json("""{"refreshToken":"configured-refresh"}""");
            using var response = await _anonymousClient.PostAsync("/oauth/token", content);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            environment.EnvironmentName = originalEnvironment;
        }
    }
}
