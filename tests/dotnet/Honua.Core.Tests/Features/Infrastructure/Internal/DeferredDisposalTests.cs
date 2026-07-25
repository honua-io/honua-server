// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Internal;
using Xunit;

namespace Honua.Core.Tests.Features.Infrastructure.Internal;

public sealed class DeferredDisposalTests
{
    [Fact]
    public void Dispose_NullResource_DoesNotThrow()
    {
        var act = () => DeferredDisposal.Dispose(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_Resource_DisposesOnce()
    {
        var disposalCount = 0;
        var resource = new CallbackDisposable(() => disposalCount++);

        DeferredDisposal.Dispose(resource);

        disposalCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_ResourceThrows_PropagatesSameException()
    {
        var expected = new InvalidOperationException("dispose failed");
        var resource = new CallbackDisposable(() => throw expected);

        var act = () => DeferredDisposal.Dispose(resource);

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
    }

    [Fact]
    public void DisposeAll_Resources_DisposesInEnumerationOrder()
    {
        var disposalOrder = new List<int>();
        var resources = new[]
        {
            new CallbackDisposable(() => disposalOrder.Add(1)),
            new CallbackDisposable(() => disposalOrder.Add(2)),
            new CallbackDisposable(() => disposalOrder.Add(3)),
        };

        DeferredDisposal.DisposeAll(resources);

        disposalOrder.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void DisposeAll_NullResources_ThrowsArgumentNullException()
    {
        var act = () => DeferredDisposal.DisposeAll<IDisposable>(null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("resources");
    }

    [Fact]
    public void DisposeAll_ResourceThrows_StopsAndPropagatesSameException()
    {
        var expected = new InvalidOperationException("dispose failed");
        var disposalOrder = new List<int>();
        var resources = new[]
        {
            new CallbackDisposable(() => disposalOrder.Add(1)),
            new CallbackDisposable(() =>
            {
                disposalOrder.Add(2);
                throw expected;
            }),
            new CallbackDisposable(() => disposalOrder.Add(3)),
        };

        var act = () => DeferredDisposal.DisposeAll(resources);

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
        disposalOrder.Should().Equal(1, 2);
    }

    [Fact]
    public async Task DisposeAsync_NullResource_Completes()
    {
        var act = async () => await DeferredDisposal.DisposeAsync(null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_Resource_AwaitsDisposal()
    {
        var disposed = false;
        var resource = new CallbackAsyncDisposable(() =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });

        await DeferredDisposal.DisposeAsync(resource);

        disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_ResourceThrows_PropagatesSameException()
    {
        var expected = new InvalidOperationException("async dispose failed");
        var resource = new CallbackAsyncDisposable(
            () => ValueTask.FromException(expected));

        var act = async () => await DeferredDisposal.DisposeAsync(resource);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(expected);
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }

    private sealed class CallbackAsyncDisposable(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return dispose();
        }
    }
}
