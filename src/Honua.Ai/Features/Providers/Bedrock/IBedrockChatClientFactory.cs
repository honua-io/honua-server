// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.AI;

namespace Honua.Ai.Providers.Bedrock;

/// <summary>
/// Builds an <see cref="IChatClient"/> targeting AWS Bedrock for the studio generation flows.
/// Abstracted so the generation services can be unit-tested against a fake chat client without an
/// AWS account, while production resolves the real Converse-API-backed
/// <see cref="BedrockChatClientAdapter"/>.
/// </summary>
public interface IBedrockChatClientFactory
{
    /// <summary>
    /// Creates a chat client for the supplied model and region. <paramref name="apiKey"/> is an
    /// optional Bedrock bearer token; when null the AWS credential chain (IAM) is used.
    /// </summary>
    IChatClient Create(string model, string region, string? apiKey);
}

/// <summary>
/// Production <see cref="IBedrockChatClientFactory"/> backed by <see cref="BedrockChatClientAdapter"/>.
/// </summary>
internal sealed class BedrockChatClientFactory : IBedrockChatClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(string model, string region, string? apiKey)
        => BedrockChatClientAdapter.Create(model, region, apiKey);
}
