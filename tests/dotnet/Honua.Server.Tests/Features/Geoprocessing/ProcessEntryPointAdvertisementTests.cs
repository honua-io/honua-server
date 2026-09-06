// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Enforces the 2026-09-06 catalog entry-point ruling (#4409): the whole
/// <c>BuiltInProcessCatalog</c> stays GA with no per-operation carve-out, and GA is
/// defined PER ENTRY POINT. Each operation declares the entry points it is callable
/// through (<see cref="ProcessDefinition.SupportedEntryPoints"/>), and every
/// advertisement surface must offer an operation on exactly those entry points —
/// never fewer (a callable capability nobody can discover) and never more (a listing
/// that leads to a submission the runtime refuses).
///
/// <para>
/// Before this contract, GPServer advertised <c>raster.interpolate-kriging</c> as a
/// task it could never run, and the workflow node provider offered protocol-only
/// operations as graph nodes with no dispatcher executor behind them.
/// </para>
/// </summary>
public sealed class ProcessEntryPointAdvertisementTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    public void EveryCatalogOperation_DeclaresAtLeastOneEntryPoint()
    {
        foreach (var process in _catalog.ListProcesses())
        {
            process.SupportedEntryPoints.Should().NotBe(
                ProcessEntryPoints.None,
                $"'{process.ProcessId}' is advertised in the catalog, so it must be callable through at "
                + "least one entry point; the ruling admits no advertised-but-unexecutable third state");
        }
    }

    /// <summary>
    /// The shared job-entry predicate is what OGC API Processes, WPS 2.0, GPServer and
    /// the MCP process tools all project through, so it must agree exactly with the
    /// per-operation declaration.
    /// </summary>
    [UnitTest]
    public void JobEntryPointPredicates_AgreeWithTheCatalogDeclaration()
    {
        foreach (var process in _catalog.ListProcesses())
        {
            var declaresJob = ProcessExecutionEligibility.Declares(process, ProcessEntryPoints.Job);

            ProcessExecutionEligibility.IsJobCallable(process).Should().Be(
                declaresJob,
                $"the shared job-runtime predicate must match '{process.ProcessId}'s declared entry points");
            ProcessExecutionCapabilityCatalog.IsOgcCallable(process).Should().Be(
                declaresJob,
                $"the OGC API Processes projection must match '{process.ProcessId}'s declared entry points");
            GPServerExecutionPolicy.IsJobCallable(process).Should().Be(
                declaresJob,
                $"the GPServer execution policy must match '{process.ProcessId}'s declared entry points");
        }
    }

    [UnitTest]
    public void GPServerTaskList_AdvertisesExactlyTheJobEntryPointOperations()
    {
        var advertised = GPServerEndpoints.BuildPublishedTaskNames(_catalog).ToHashSet(StringComparer.Ordinal);

        foreach (var process in _catalog.ListProcesses())
        {
            var declaresJob = ProcessExecutionEligibility.Declares(process, ProcessEntryPoints.Job);
            advertised.Contains(process.ProcessId).Should().Be(
                declaresJob,
                declaresJob
                    ? $"'{process.ProcessId}' declares the job entry point, so GPServer must publish it"
                    : $"'{process.ProcessId}' does not declare the job entry point, so GPServer must not "
                        + "publish a task that cannot be submitted there");
        }
    }

    [UnitTest]
    public void McpProcessTools_AdvertiseExactlyTheJobEntryPointOperations()
    {
        var advertised = McpToolSchemas.JobCallableProcessIdNames.ToHashSet(StringComparer.Ordinal);

        foreach (var process in _catalog.ListProcesses())
        {
            advertised.Contains(process.ProcessId).Should().Be(
                ProcessExecutionEligibility.Declares(process, ProcessEntryPoints.Job),
                $"the MCP execute tool's process-id domain must match '{process.ProcessId}'s declared entry points");
        }
    }

    [UnitTest]
    public async Task WorkflowNodeProvider_AdvertisesExactlyTheWorkflowEntryPointOperations()
    {
        IProcessCatalog catalog = _catalog;
        var nodes = await new ProcessCatalogWorkflowNodeProvider(catalog).ListNodesAsync();
        var advertised = nodes.Select(node => node.NodeTypeId).ToHashSet(StringComparer.Ordinal);

        foreach (var process in _catalog.ListProcesses())
        {
            var declaresWorkflow = ProcessExecutionEligibility.Declares(process, ProcessEntryPoints.Workflow);
            advertised.Contains(ProcessCatalogWorkflowNodeProvider.ToNodeTypeId(process.ProcessId)).Should().Be(
                declaresWorkflow,
                declaresWorkflow
                    ? $"'{process.ProcessId}' declares the workflow entry point, so it must surface as a node"
                    : $"'{process.ProcessId}' does not declare the workflow entry point, so offering it as a "
                        + "graph node would advertise a step the dispatcher cannot execute");
        }

        // Every advertised node is executable: the provider no longer publishes
        // inspect-only nodes, which is what made "advertised" and "runnable" diverge.
        nodes.Should().OnlyContain(node => node.CapabilityFlags.Executable);
    }

    [UnitTest]
    public void ProtocolOnlyOperations_AreAdvertisedOnNoJobOrWorkflowSurface()
    {
        var gpServerTasks = GPServerEndpoints.BuildPublishedTaskNames(_catalog).ToHashSet(StringComparer.Ordinal);
        var protocolOnly = _catalog.ListProcesses()
            .Where(process => process.SupportedEntryPoints == ProcessEntryPoints.Protocol)
            .ToList();

        protocolOnly.Should().NotBeEmpty("the catalog still owns protocol-entry-point operations");
        foreach (var process in protocolOnly)
        {
            ProcessExecutionCapabilityCatalog.IsOgcCallable(process).Should().BeFalse(process.ProcessId);
            ProcessExecutionCapabilityCatalog.IsWorkflowComposable(process).Should().BeFalse(process.ProcessId);
            gpServerTasks.Should().NotContain(process.ProcessId);
            McpToolSchemas.JobCallableProcessIdNames.Should().NotContain(process.ProcessId);
        }
    }
}
