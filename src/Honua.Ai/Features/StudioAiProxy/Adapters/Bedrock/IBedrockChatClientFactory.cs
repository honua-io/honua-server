// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.AI;

namespace Honua.Ai.StudioAiProxy.Adapters.Bedrock;

internal interface IBedrockChatClientFactory
{
    IChatClient Create(string model, string region, string? apiKey);
}

internal sealed class BedrockChatClientFactory : IBedrockChatClientFactory
{
    public IChatClient Create(string model, string region, string? apiKey)
        => BedrockChatClientAdapter.Create(model, region, apiKey);
}
