// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class DeferredOperationExecutorTests
{
    [UnitTest]
    public async Task Registration_OnlyConstructsSelectedActuator_AndScopeOwnsItsLifetime()
    {
        var services = new ServiceCollection();
        var constructions = 0;
        var disposals = 0;
        services.AddDeferredOperationExecutor("healthy", _ =>
        {
            constructions++;
            return new DisposableExecutor(() => disposals++);
        });
        services.AddDeferredOperationExecutor("broken", _ =>
            throw new InvalidOperationException("Private dependency details"));
        using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var executors = scope.ServiceProvider.GetServices<IOperationExecutor>().ToArray();
            executors.Select(executor => executor.OperationId).Should().Equal("healthy", "broken");
            constructions.Should().Be(0, "discovery must not instantiate actuators");
            var healthy = executors[0];
            (await healthy.ValidateAsync(new OperationRequest { OperationId = "healthy" })).IsValid.Should().BeTrue();
            await healthy.ValidateAsync(new OperationRequest { OperationId = "healthy" });
            constructions.Should().Be(1, "one scope reuses the same selected actuator");
            disposals.Should().Be(0);
        }
        disposals.Should().Be(1, "DI must dispose a lazily constructed actuator with its scope");
    }

    [UnitTest]
    public async Task PreparationSubmitAndStatus_PreserveCanonicalInputsAndResults()
    {
        var inner = Substitute.For<IOperationExecutor, IOperationRequestPreparer>();
        inner.OperationId.Returns("prepared");
        var request = new OperationRequest { OperationId = "prepared" };
        var context = new OperationPolicyContext { PrincipalId = "operator" };
        var prepared = request with { Parameters = new Dictionary<string, string?> { ["target"] = "sealed" } };
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        ((IOperationRequestPreparer)inner).PrepareAsync(request, context, token).Returns(prepared);
        var handle = new OperationHandle
        {
            OperationId = "prepared", OperationInstanceId = "instance", CorrelationId = "correlation",
            Status = OperationHandleStatus.Completed, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        var status = new OperationStatus
        {
            OperationId = "prepared", OperationInstanceId = "instance", CorrelationId = "correlation",
            Status = OperationHandleStatus.Completed, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        inner.SubmitAsync(prepared, context, token).Returns(handle);
        inner.GetStatusAsync(handle, token).Returns(status);
        var executor = new DeferredOperationExecutor("prepared", () => inner);

        (await executor.PrepareAsync(request, context, token)).Should().BeSameAs(prepared);
        (await executor.SubmitAsync(prepared, context, token)).Should().BeSameAs(handle);
        (await executor.GetStatusAsync(handle, token)).Should().BeSameAs(status);
    }

    [UnitTest]
    public async Task MismatchedRegistration_RefusesBeforeActuation()
    {
        var inner = Substitute.For<IOperationExecutor>();
        inner.OperationId.Returns("wrong");
        var executor = new DeferredOperationExecutor("expected", () => inner);
        var submit = () => executor.SubmitAsync(
            new OperationRequest { OperationId = "expected" }, new OperationPolicyContext());
        await submit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*mismatched identity*");
        await inner.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    private sealed class DisposableExecutor(Action dispose) : IOperationExecutor, IDisposable
    {
        public string OperationId => "healthy";
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });
        public Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void Dispose() => dispose();
    }
}
