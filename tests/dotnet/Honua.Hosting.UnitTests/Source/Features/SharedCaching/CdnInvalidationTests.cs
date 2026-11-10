// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class CdnInvalidationTests
{
    private readonly RecordingInvalidator _invalidator = new();
    private readonly ManualTimeProvider _time = new();

    [UnitTest]
    public async Task InvalidateCoalescedAsync_ManyEditsInOneWindow_TellTheCdnOnceNowAndOnceWhenTheWindowCloses()
    {
        var service = Service();
        string[] paths = ["/tiles/3/*"];

        for (var edit = 0; edit < 100; edit++)
        {
            await service.InvalidateCoalescedAsync("layer:3", paths, CancellationToken.None);
        }

        _invalidator.Calls.Should().HaveCount(1);

        _time.Advance(CdnInvalidationService.CoalescingWindow);

        await _invalidator.SecondCall.WaitAsync(TimeSpan.FromSeconds(5));
        _invalidator.Calls.Should().HaveCount(2);
    }

    [UnitTest]
    public async Task InvalidateCoalescedAsync_AnotherLayer_IsNotHeldBack()
    {
        var service = Service();

        await service.InvalidateCoalescedAsync("layer:3", ["/tiles/3/*"], CancellationToken.None);
        await service.InvalidateCoalescedAsync("layer:4", ["/tiles/4/*"], CancellationToken.None);

        _invalidator.Calls.Should().HaveCount(2);
    }

    [UnitTheory]
    [InlineData(CdnInvalidationOutcome.Failed, "failed")]
    [InlineData(CdnInvalidationOutcome.Submitted, "submitted")]
    public async Task InvalidateNowAsync_DuringAnAdminRequest_ReportsTheOutcomeOnTheResponse(
        CdnInvalidationOutcome outcome, string expected)
    {
        _invalidator.Result = new CdnInvalidationResult(outcome);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        await Service(accessor).InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None);

        accessor.HttpContext.Response.Headers[CdnInvalidationService.ResultHeader].ToString().Should().Be(expected);
    }

    [UnitTest]
    public async Task InvalidateServiceCatalogAsync_ServiceSecured_InvalidatesEverythingBeforeReturning()
    {
        await Invalidation().InvalidateServiceCatalogAsync("Parcels", null, CancellationToken.None);

        _invalidator.Calls.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateLayerAsync_StyleOrPolicyChanged_InvalidatesEverythingBeforeReturning()
    {
        await Invalidation().InvalidateLayerAsync("Parcels", 3, CancellationToken.None);

        _invalidator.Calls.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_ManyEdits_EnqueueOnePurgeOfThatLayersTileAndQueryPrefixes()
    {
        var invalidation = Invalidation();

        for (var edit = 0; edit < 50; edit++)
        {
            await invalidation.InvalidateEditedLayerAsync("Parcels", 3, CancellationToken.None);
        }

        _invalidator.Calls.Should().ContainSingle().Which.Should()
            .Contain("/tiles/3/*")
            .And.Contain("/rest/services/Parcels/*")
            .And.NotContain("/*");
    }

    [UnitTest]
    public void ForService_IdentifierWithReservedCharacters_IsEscaped()
        => CdnPaths.ForService("land use").Should().Contain("/rest/services/land%20use/*");

    private CdnInvalidationService Service(IHttpContextAccessor? accessor = null)
        => new(_invalidator, _time, NullLogger<CdnInvalidationService>.Instance, accessor);

    private OutputCacheInvalidationService Invalidation()
        => new(null, null, null, ScopeFactory(), null, NullLogger<OutputCacheInvalidationService>.Instance, Service());

    private static IServiceScopeFactory ScopeFactory()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private sealed class RecordingInvalidator : ICdnInvalidator
    {
        private readonly TaskCompletionSource _secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IReadOnlyCollection<string>> Calls { get; } = [];

        public Task SecondCall => _secondCall.Task;

        public CdnInvalidationResult Result { get; set; } = new(CdnInvalidationOutcome.Submitted);

        public Task<CdnInvalidationResult> InvalidateAsync(
            IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            Calls.Add(paths);
            if (Calls.Count == 2)
            {
                _secondCall.TrySetResult();
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.Where(timer => timer.DueAt <= _now).ToArray())
            {
                _timers.Remove(timer);
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            public DateTimeOffset DueAt { get; } = dueAt;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
