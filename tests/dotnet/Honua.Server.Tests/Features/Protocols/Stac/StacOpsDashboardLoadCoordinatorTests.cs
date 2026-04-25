// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Regression coverage for the STAC ops demo reload coordination helper.
/// </summary>
public sealed class StacOpsDashboardLoadCoordinatorTests
{
    [Fact]
    public void BeginLoad_NewLoadCancelsPreviousLoadAndKeepsOnlyCurrentActive()
    {
        var coordinator = CreateCoordinator();
        using var firstLoad = BeginLoad(coordinator);
        using var secondLoad = BeginLoad(coordinator);

        firstLoad.Token.IsCancellationRequested.Should().BeTrue();
        firstLoad.IsActive.Should().BeFalse();
        secondLoad.Token.IsCancellationRequested.Should().BeFalse();
        secondLoad.IsActive.Should().BeTrue();
    }

    [Fact]
    public void CancelActiveLoad_CancelsCurrentLoadAndClearsActiveState()
    {
        var coordinator = CreateCoordinator();
        using var load = BeginLoad(coordinator);

        CancelActiveLoad(coordinator);

        load.Token.IsCancellationRequested.Should().BeTrue();
        load.IsActive.Should().BeFalse();
    }

    private static object CreateCoordinator()
    {
        return Activator.CreateInstance(CoordinatorType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create the dashboard load coordinator.");
    }

    private static LoadHandleProxy BeginLoad(object coordinator)
    {
        var handle = BeginLoadMethod.Invoke(coordinator, parameters: null)
            ?? throw new InvalidOperationException("BeginLoad returned null.");
        return new LoadHandleProxy(handle);
    }

    private static void CancelActiveLoad(object coordinator)
    {
        CancelActiveLoadMethod.Invoke(coordinator, parameters: null);
    }

    private static readonly Type CoordinatorType =
        Assembly.Load("Honua.StacOpsDemo")
            .GetType("Honua.StacOpsDemo.Services.DashboardLoadCoordinator", throwOnError: true)!;

    private static readonly MethodInfo BeginLoadMethod =
        CoordinatorType.GetMethod("BeginLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find BeginLoad.");

    private static readonly MethodInfo CancelActiveLoadMethod =
        CoordinatorType.GetMethod("CancelActiveLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find CancelActiveLoad.");

    private readonly record struct LoadHandleProxy(object Value) : IDisposable
    {
        public CancellationToken Token =>
            (CancellationToken)(Value.GetType().GetProperty("Token")?.GetValue(Value)
                ?? throw new InvalidOperationException("Could not read Token."));

        public bool IsActive =>
            (bool)(Value.GetType().GetProperty("IsActive")?.GetValue(Value)
                ?? throw new InvalidOperationException("Could not read IsActive."));

        public void Dispose()
        {
            ((IDisposable)Value).Dispose();
        }
    }
}
