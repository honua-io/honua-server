// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class KubernetesJobManifestSecurityContextTests
{
    [UnitTest]
    public void SerializeJobManifest_EmitsRestrictedPodSecurityContext()
    {
        using var document = Serialize();
        var podSecurity = PodSpec(document).GetProperty("securityContext");

        podSecurity.GetProperty("runAsNonRoot").GetBoolean().Should().BeTrue();
        podSecurity.GetProperty("runAsUser").GetInt64().Should().Be(1001);
        podSecurity.GetProperty("runAsGroup").GetInt64().Should().Be(1001);
        podSecurity.GetProperty("seccompProfile").GetProperty("type").GetString().Should().Be("RuntimeDefault");
    }

    [UnitTest]
    public void SerializeJobManifest_EmitsRestrictedContainerSecurityContext()
    {
        using var document = Serialize();
        var containerSecurity = PodSpec(document).GetProperty("containers")[0].GetProperty("securityContext");

        containerSecurity.GetProperty("runAsNonRoot").GetBoolean().Should().BeTrue();
        containerSecurity.GetProperty("allowPrivilegeEscalation").GetBoolean().Should().BeFalse();
        containerSecurity.GetProperty("privileged").GetBoolean().Should().BeFalse();
        containerSecurity.GetProperty("capabilities").GetProperty("drop").EnumerateArray()
            .Select(entry => entry.GetString())
            .Should().Equal("ALL");
    }

    [UnitTest]
    public void SerializeJobManifest_KeepsImagePullSecretsAfterExtraction()
    {
        using var document = Serialize(["registry-a", "registry-b"]);

        PodSpec(document).GetProperty("imagePullSecrets").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .Should().Equal("registry-a", "registry-b");
    }

    private static JsonDocument Serialize(IReadOnlyList<string>? imagePullSecrets = null)
    {
        var manifest = new KubernetesJobManifest
        {
            Namespace = "honua",
            Name = "honua-job-1",
            Image = "ghcr.io/honua-io/worker:1",
            ImagePullSecrets = imagePullSecrets ?? Array.Empty<string>(),
        };

        return JsonDocument.Parse(KubernetesJobManifestSerializer.SerializeJobManifest(manifest));
    }

    private static JsonElement PodSpec(JsonDocument document)
        => document.RootElement.GetProperty("spec").GetProperty("template").GetProperty("spec");
}
