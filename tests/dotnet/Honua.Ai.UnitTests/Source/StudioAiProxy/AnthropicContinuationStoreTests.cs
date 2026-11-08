// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.StudioAiProxy;

[Protocol(TestProtocols.TestQuality)]
public sealed class AnthropicContinuationStoreTests
{
    [UnitTest]
    public void Restore_ExpiredTurn_RejectsContinuation()
    {
        var clock = new ContinuationClock();
        var store = new AnthropicContinuationStore(clock);
        using var content = JsonDocument.Parse("[{\"type\":\"thinking\",\"thinking\":\"private\",\"signature\":\"signed\"}]");
        store.Save("turn", content.RootElement);
        store.Restore("turn").Should().NotBeNull();

        clock.UtcNow += TimeSpan.FromMinutes(15);

        var restore = () => store.Restore("turn");
        restore.Should().Throw<InvalidOperationException>().WithMessage("*start a new conversation*");
    }

    [UnitTest]
    public void Save_TurnCapacityReached_EvictsOldestTurn()
    {
        var clock = new ContinuationClock();
        var store = new AnthropicContinuationStore(clock);
        using var content = JsonDocument.Parse("[]");
        for (var index = 0; index <= 128; index++)
        {
            store.Save(index.ToString(System.Globalization.CultureInfo.InvariantCulture), content.RootElement);
            clock.UtcNow += TimeSpan.FromSeconds(1);
        }

        var oldest = () => store.Restore("0");
        oldest.Should().Throw<InvalidOperationException>();
        store.Restore("1").Should().NotBeNull();
        store.Restore("128").Should().NotBeNull();
    }

    [UnitTest]
    public void Save_CharacterCapacityReached_EvictsOldestTurn()
    {
        var clock = new ContinuationClock();
        var store = new AnthropicContinuationStore(clock);
        using var content = JsonDocument.Parse(JsonSerializer.Serialize(new string('x', 250_000)));
        for (var index = 0; index < 17; index++)
        {
            store.Save(index.ToString(System.Globalization.CultureInfo.InvariantCulture), content.RootElement);
            clock.UtcNow += TimeSpan.FromSeconds(1);
        }

        var oldest = () => store.Restore("0");
        oldest.Should().Throw<InvalidOperationException>();
        store.Restore("1").Should().NotBeNull();
        store.Restore("16").Should().NotBeNull();
    }

    private sealed class ContinuationClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
