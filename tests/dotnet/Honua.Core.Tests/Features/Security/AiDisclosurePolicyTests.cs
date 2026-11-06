// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Security;

public sealed class AiDisclosurePolicyTests
{
    private static AiDisclosureRequest Request => new("primary", "openai", "https://models.example/v1", "model-a", "studio-chat");

    [UnitTest]
    public void IsAllowed_WithoutApproval_DeniesExternalUnclassifiedPayload()
        => AiDisclosurePolicy.IsAllowed(new(), Request).Should().BeFalse();

    [UnitTest]
    public void IsAllowed_ExactApproval_RequiresEveryEffectiveBoundary()
    {
        var policy = new AiDisclosurePolicyOptions
        {
            Approvals = [new() { Provider = "primary", Kind = "openai", Destination = "https://models.example/v1", Model = "model-a", UseCase = "studio-chat", DataClassification = "unclassified" }]
        };
        AiDisclosurePolicy.IsAllowed(policy, Request).Should().BeTrue();
        foreach (var changed in new[] { Request with { Provider = "other" }, Request with { Kind = "anthropic" }, Request with { Destination = "https://models.example/other" }, Request with { Destination = "https://models.example/v1/" }, Request with { Model = "model-b" }, Request with { UseCase = "reporting-narrative" } })
            AiDisclosurePolicy.IsAllowed(policy, changed).Should().BeFalse();
        policy.WorkloadDataClassification = "restricted";
        AiDisclosurePolicy.IsAllowed(policy, Request).Should().BeFalse();
    }

    [UnitTest]
    public void IsAllowed_LocalException_OnlyPermitsTrueLoopbackHttpAdapters()
    {
        foreach (var local in new[] { "http://localhost:11434/v1", "http://127.0.0.1/v1", "http://[::1]/v1" })
            AiDisclosurePolicy.IsAllowed(new(), Request with { Destination = local }).Should().BeTrue();
        foreach (var remote in new[] { "http://10.0.0.1/v1", "https://models.example/v1", "http://localhost.example/v1", "https://user@localhost/v1", "https://localhost/v1?secret=x" })
            AiDisclosurePolicy.IsAllowed(new(), Request with { Destination = remote }).Should().BeFalse();
        AiDisclosurePolicy.IsAllowed(new(), Request with { Kind = "bedrock", Destination = "http://localhost" }).Should().BeFalse();
    }

    [UnitTest]
    public void IsAllowed_ExplicitApproval_CannotAuthorizeUnsafeOrMalformedDestination()
    {
        foreach (var destination in new[]
        {
            "http://models.example/v1", "https://user:secret@models.example/v1",
            "https://models.example/v1?token=secret", "https://models.example/v1#fragment",
            "https://localhost/v1?token=secret", "file:///models/example", "not-a-uri"
        })
        {
            var request = Request with { Destination = destination };
            var policy = new AiDisclosurePolicyOptions
            {
                Approvals =
                [
                    new()
                    {
                        Provider = request.Provider,
                        Kind = request.Kind,
                        Destination = destination,
                        Model = request.Model,
                        UseCase = request.UseCase,
                        DataClassification = "unclassified"
                    }
                ]
            };
            AiDisclosurePolicy.IsAllowed(policy, request).Should().BeFalse(destination);
        }
    }

    [UnitTest]
    public void IsAllowed_Bedrock_RequiresExplicitRegionAndModelApproval()
    {
        var request = Request with { Kind = "bedrock", Destination = "aws-bedrock:us-west-2" };
        var policy = new AiDisclosurePolicyOptions { Approvals = [new() { Provider = "primary", Kind = "bedrock", Destination = request.Destination, Model = request.Model, UseCase = request.UseCase, DataClassification = "unclassified" }] };
        AiDisclosurePolicy.IsAllowed(policy, request).Should().BeTrue();
        AiDisclosurePolicy.IsAllowed(policy, request with { Destination = "aws-bedrock:us-east-1" }).Should().BeFalse();
    }
}
