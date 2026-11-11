// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Tests.Features.Orchestration;

public sealed class StepRetryPolicyJsonTests
{
    [Fact]
    [Trait("Tier", "Fast")]
    public void ReadWrite_DefaultOptions_PreservesPascalCaseContract()
    {
        var policy = JsonSerializer.Deserialize<StepRetryPolicy>("""{"MaxAttempts":3,"InitialDelaySeconds":2}""")!;
        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(2, policy.InitialDelaySeconds);
        Assert.Equal(2, policy.BackoffMultiplier);
        Assert.Equal(300, policy.MaxDelaySeconds);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(policy));
        Assert.Equal(3, json.RootElement.GetProperty("MaxAttempts").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void ReadWrite_WebOptions_HonorsCaseAndNumericStringHandling()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
        };
        var policy = JsonSerializer.Deserialize<StepRetryPolicy>("""
            {"MAXATTEMPTS":"3","INITIALDELAYSECONDS":"2","BACKOFFMULTIPLIER":"1.5","MAXDELAYSECONDS":"25"}
            """, options)!;
        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(2, policy.InitialDelaySeconds);
        Assert.Equal(1.5, policy.BackoffMultiplier);
        Assert.Equal(25, policy.MaxDelaySeconds);
        var json = JsonSerializer.Serialize(policy, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("3", document.RootElement.GetProperty("maxAttempts").GetString());
        Assert.Equal(policy, JsonSerializer.Deserialize<StepRetryPolicy>(json, options));
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void ReadWrite_SnakeCaseOptions_UsesConfiguredNames()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var policy = JsonSerializer.Deserialize<StepRetryPolicy>("""{"max_attempts":2,"initial_delay_seconds":1}""", options)!;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(policy, options));
        Assert.Equal(2, document.RootElement.GetProperty("max_attempts").GetInt32());
    }
}
