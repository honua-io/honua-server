// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class MultiNodeWithoutRedisStartupTests
{
    private static readonly Dictionary<string, string?> _multiNodeSettings = new()
    {
        ["Deployment:Mode"] = "MultiNode",
        ["FileStorage:Provider"] = "AwsS3",
        ["FileStorage:AwsS3:BucketName"] = "honua-multi-node-startup",
        ["FileStorage:AwsS3:Region"] = "us-west-2",
    };

    [IntegrationTest]
    public void ProductionValidation_MultiNodeWithObjectStorageAndNoRedis_ReportsNoDeploymentError()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(_multiNodeSettings).Build();

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration,
            NullLogger.Instance,
            isDevelopment: false);

        errors.Should().NotContain(error => error.Contains("Multi-node", StringComparison.OrdinalIgnoreCase));
        errors.Should().NotContain(error => error.Contains("redis", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    public void ProductionValidation_MultiNodeWithLocalFileStorage_StillRefusesToStart()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Deployment:Mode"] = "MultiNode" })
            .Build();

        var errors = ConfigurationValidationService.ValidateConfiguration(
            configuration,
            NullLogger.Instance,
            isDevelopment: false);

        errors.Should().Contain(error => error.Contains("shared cloud file storage provider", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    public async Task Host_MultiNodeWithObjectStorageAndNoRedis_StartsAndAnswersLiveness()
    {
        var fixture = new WebAppFixture()
            .WithProductionJobSubstrate()
            .ConfigureWebHost(builder =>
            {
                foreach (var setting in _multiNodeSettings)
                {
                    builder.UseSetting(setting.Key, setting.Value);
                }
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();

            using var response = await client.GetAsync("/healthz/live");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            fixture.Services.GetService(typeof(StackExchange.Redis.IConnectionMultiplexer)).Should().BeNull();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
