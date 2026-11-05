// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class FileBackedLicenseServiceStopTests
{
    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(2);

    [UnitTest]
    public async Task StopAsync_RevalidationWaitingForNextTick_Completes()
    {
        await using var running = await RunningLicenseService.StartAsync();
        var revalidation = running.Revalidation;
        revalidation.IsCompleted.Should().BeFalse();

        var stopping = running.Service.StopAsync(CancellationToken.None);

        (await Task.WhenAny(stopping, Task.Delay(StopBound))).Should().Be(stopping);
        await stopping;
        revalidation.IsCompletedSuccessfully.Should().BeTrue();
    }

    [UnitTest]
    public async Task StopAsync_HostShutdownAlreadyCancelled_Completes()
    {
        await using var running = await RunningLicenseService.StartAsync();
        var revalidation = running.Revalidation;
        revalidation.IsCompleted.Should().BeFalse();
        using var hostShutdown = new CancellationTokenSource();
        hostShutdown.Cancel();

        var stopping = running.Service.StopAsync(hostShutdown.Token);

        (await Task.WhenAny(stopping, Task.Delay(StopBound))).Should().Be(stopping);
        await stopping;
        (await Task.WhenAny(revalidation, Task.Delay(StopBound))).Should().Be(revalidation);
        revalidation.IsCompletedSuccessfully.Should().BeTrue();
    }

    [UnitTest]
    public async Task StopAsync_HostShutdownAlreadyCancelledWhileRevalidationStillWaiting_Completes()
    {
        await using var running = await RunningLicenseService.StartAsync();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running.ReplaceRevalidation(waiting.Task);
        using var hostShutdown = new CancellationTokenSource();
        hostShutdown.Cancel();

        var stopping = running.Service.StopAsync(hostShutdown.Token);

        (await Task.WhenAny(stopping, Task.Delay(StopBound))).Should().Be(stopping);
        await stopping;
        waiting.Task.IsCompleted.Should().BeFalse();
    }

    [UnitTest]
    public async Task StopAsync_RevalidationCanceledByStopSignal_Completes()
    {
        await using var running = await RunningLicenseService.StartAsync();
        running.ReplaceRevalidation(Task.FromCanceled(new CancellationToken(canceled: true)));

        var stopping = running.Service.StopAsync(CancellationToken.None);

        (await Task.WhenAny(stopping, Task.Delay(StopBound))).Should().Be(stopping);
        await stopping;
    }

    public static TheoryData<Exception, CancellationToken> RevalidationFaults { get; } = new()
    {
        { new InvalidOperationException("license source failed"), CancellationToken.None },
        { new OperationCanceledException("license source canceled the read"), CancellationToken.None },
        { new InvalidOperationException("license source failed"), new CancellationToken(canceled: true) },
        { new OperationCanceledException("license source canceled the read"), new CancellationToken(canceled: true) },
    };

    [UnitTheory]
    [MemberData(nameof(RevalidationFaults))]
    public async Task StopAsync_RevalidationFault_SurfacesException(Exception failure, CancellationToken hostShutdown)
    {
        await using var running = await RunningLicenseService.StartAsync();
        running.ReplaceRevalidation(Task.FromException(failure));

        var thrown = await Assert.ThrowsAsync(failure.GetType(), () => running.Service.StopAsync(hostShutdown));

        thrown.Should().BeSameAs(failure);
    }

    [UnitTest]
    public async Task StopAsync_HostShutdownTimesOutWhileRevalidationRuns_Returns()
    {
        await using var running = await RunningLicenseService.StartAsync();
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running.ReplaceRevalidation(hung.Task);
        using var hostShutdown = new CancellationTokenSource();

        var stopping = running.Service.StopAsync(hostShutdown.Token);
        (await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromMilliseconds(200)))).Should().NotBe(stopping);

        hostShutdown.Cancel();

        (await Task.WhenAny(stopping, Task.Delay(StopBound))).Should().Be(stopping);
        await stopping;
        hung.Task.IsCompleted.Should().BeFalse();
    }

    private sealed class RunningLicenseService : IAsyncDisposable
    {
        private static readonly FieldInfo RevalidationField = typeof(FileBackedLicenseService).GetField(
            "_revalidationTask",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("License revalidation task is not available to the test.");

        private readonly Task _startedLoop;

        private RunningLicenseService(FileBackedLicenseService service, Task startedLoop)
        {
            Service = service;
            _startedLoop = startedLoop;
        }

        public FileBackedLicenseService Service { get; }

        public Task Revalidation =>
            (Task)RevalidationField.GetValue(Service)!;

        public static async Task<RunningLicenseService> StartAsync()
        {
            var service = new FileBackedLicenseService(
                Options.Create(new LicenseOptions()),
                new BouncyCastleEd25519Verifier(),
                NullLogger<FileBackedLicenseService>.Instance);
            await service.StartAsync(CancellationToken.None);
            return new RunningLicenseService(service, (Task)RevalidationField.GetValue(service)!);
        }

        public void ReplaceRevalidation(Task revalidation) =>
            RevalidationField.SetValue(Service, revalidation);

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await _startedLoop;
        }
    }
}
