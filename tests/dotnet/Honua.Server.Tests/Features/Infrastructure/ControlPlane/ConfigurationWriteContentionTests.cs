// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ConfigurationWriteContentionTests
{
    [UnitTest]
    public void PauseAsync_AfterTheLastAttempt_DoesNotWait()
    {
        var pause = ConfigurationWriteContention.PauseAsync(ConfigurationWriteContention.MaxAttempts - 1, CancellationToken.None);

        pause.IsCompletedSuccessfully.Should().BeTrue();
    }

    [UnitTest]
    public async Task PauseAsync_BetweenAttempts_HonoursCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var pause = () => ConfigurationWriteContention.PauseAsync(0, cancelled.Token);

        await pause.Should().ThrowAsync<OperationCanceledException>();
    }
}
