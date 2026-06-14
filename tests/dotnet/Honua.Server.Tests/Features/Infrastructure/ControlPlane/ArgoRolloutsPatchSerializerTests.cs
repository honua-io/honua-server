// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ArgoRolloutsPatchSerializerTests
{
    [Fact]
    public void SerializeSetImage_KeysByContainerName()
    {
        var patch = ArgoRolloutsPatchSerializer.SerializeSetImage("honua", "ghcr.io/honua/server:sha-42");

        using var document = JsonDocument.Parse(patch);
        var container = document.RootElement
            .GetProperty("spec")
            .GetProperty("template")
            .GetProperty("spec")
            .GetProperty("containers")[0];

        container.GetProperty("name").GetString().Should().Be("honua");
        container.GetProperty("image").GetString().Should().Be("ghcr.io/honua/server:sha-42");
    }

    [Fact]
    public void SerializeClearPauseStatus_NullsPauseConditionsAndClearsAbort()
    {
        var patch = ArgoRolloutsPatchSerializer.SerializeClearPauseStatus();

        using var document = JsonDocument.Parse(patch);
        var status = document.RootElement.GetProperty("status");

        status.GetProperty("pauseConditions").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("abort").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void SerializeAbortStatus_SetsAbortTrue()
    {
        var patch = ArgoRolloutsPatchSerializer.SerializeAbortStatus();

        using var document = JsonDocument.Parse(patch);
        document.RootElement.GetProperty("status").GetProperty("abort").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SerializeUnpauseSpec_SetsPausedFalse()
    {
        var patch = ArgoRolloutsPatchSerializer.SerializeUnpauseSpec();

        using var document = JsonDocument.Parse(patch);
        document.RootElement.GetProperty("spec").GetProperty("paused").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ParseRollout_ReadsPhaseWeightPauseAndHashes()
    {
        const string json = """
        {
          "metadata": { "name": "honua-server" },
          "spec": {
            "template": {
              "spec": {
                "containers": [ { "name": "honua", "image": "ghcr.io/honua/server:sha-42" } ]
              }
            }
          },
          "status": {
            "phase": "Paused",
            "message": "waiting for manual promotion",
            "abort": false,
            "pauseConditions": [ { "reason": "CanaryPauseStep" } ],
            "currentPodHash": "abc123",
            "stableRS": "stable999",
            "canary": { "weights": { "canary": { "weight": 25 } } }
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        var state = ArgoRolloutsPatchSerializer.ParseRollout(document.RootElement, "fallback");

        state.Name.Should().Be("honua-server");
        state.Phase.Should().Be(ArgoRolloutPhase.Paused);
        state.Message.Should().Be("waiting for manual promotion");
        state.IsPaused.Should().BeTrue();
        state.IsAborted.Should().BeFalse();
        state.CanaryWeight.Should().Be(25);
        state.CurrentPodHash.Should().Be("abc123");
        state.StableRevisionHash.Should().Be("stable999");
        state.PodTemplateImage.Should().Be("ghcr.io/honua/server:sha-42");
    }

    [Fact]
    public void ParseRollout_HealthyWithoutPauseOrCanary_ReportsHealthy()
    {
        const string json = """
        {
          "metadata": { "name": "honua-server" },
          "status": {
            "phase": "Healthy",
            "currentPodHash": "abc123",
            "stableRS": "abc123"
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        var state = ArgoRolloutsPatchSerializer.ParseRollout(document.RootElement, "fallback");

        state.Phase.Should().Be(ArgoRolloutPhase.Healthy);
        state.IsPaused.Should().BeFalse();
        state.CanaryWeight.Should().BeNull();
        state.CurrentPodHash.Should().Be(state.StableRevisionHash);
    }

    [Fact]
    public void ParseRollout_AbortedDegraded_ReportsAbortAndDegraded()
    {
        const string json = """
        {
          "status": {
            "phase": "Degraded",
            "abort": true,
            "message": "analysis run failed"
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        var state = ArgoRolloutsPatchSerializer.ParseRollout(document.RootElement, "honua-server");

        state.Name.Should().Be("honua-server");
        state.Phase.Should().Be(ArgoRolloutPhase.Degraded);
        state.IsAborted.Should().BeTrue();
    }

    [Fact]
    public void ParseRollout_MissingStatus_UsesUnknownPhaseAndFallbackName()
    {
        using var document = JsonDocument.Parse("{}");

        var state = ArgoRolloutsPatchSerializer.ParseRollout(document.RootElement, "honua-server");

        state.Name.Should().Be("honua-server");
        state.Phase.Should().Be(ArgoRolloutPhase.Unknown);
        state.IsPaused.Should().BeFalse();
        state.IsAborted.Should().BeFalse();
    }

    [Fact]
    public void PatchBodies_AreValidUtf8Json()
    {
        foreach (var patch in new[]
        {
            ArgoRolloutsPatchSerializer.SerializeSetImage("honua", "image:tag"),
            ArgoRolloutsPatchSerializer.SerializeClearPauseStatus(),
            ArgoRolloutsPatchSerializer.SerializeUnpauseSpec(),
            ArgoRolloutsPatchSerializer.SerializeAbortStatus()
        })
        {
            var act = () => JsonDocument.Parse(Encoding.UTF8.GetString(patch));
            act.Should().NotThrow();
        }
    }
}
