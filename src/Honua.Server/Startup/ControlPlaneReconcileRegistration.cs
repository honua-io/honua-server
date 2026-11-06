// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Startup;

internal static class ControlPlaneReconcileRegistration
{
    public static IServiceCollection AddHonuaControlPlaneReconcilers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Decorate the workflow-operation store so every persisted deploy transition fans out through a
        // single seam. TryCreate/Set/TrySet are the choke point for every persisted transition (service +
        // reconciler), so decorating them once covers them all without scattering notification calls
        // across the reconciler. Two decorators compose over the durable Redis store:
        //   * TransitionObservingWorkflowOperationStore raises IWorkflowOperationTransitionListener
        //     observers (created / submitted / promoted / rolled-back / manual-intervention). This ticket
        //     registers the operate-timeline release producer; the realtime hub (#2554) attaches to the
        //     same seam as a second listener.
        //   * NotifyingWorkflowOperationStore raises ops-alert notifications on terminal transitions (it
        //     no-ops when ops notifications are disabled, the default).
        services.AddSingleton<RedisWorkflowOperationStore>();
        services.AddSingleton<Honua.Infrastructure.Monitoring.ReleaseTimelineBuffer>();
        services.AddSingleton<IWorkflowOperationTransitionListener>(sp =>
            new Honua.Infrastructure.Monitoring.ReleaseTimelineTransitionListener(
                sp.GetRequiredService<Honua.Infrastructure.Monitoring.ReleaseTimelineBuffer>()));
        services.AddSingleton<IWorkflowOperationStore>(sp => new Honua.Alerts.Ops.NotifyingWorkflowOperationStore(
            new Honua.ControlPlane.TransitionObservingWorkflowOperationStore(
                sp.GetRequiredService<RedisWorkflowOperationStore>(),
                sp.GetServices<IWorkflowOperationTransitionListener>(),
                sp.GetRequiredService<ILogger<Honua.ControlPlane.TransitionObservingWorkflowOperationStore>>()),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<Honua.Core.Features.Alerts.Domain.AlertOptions>>(),
            sp.GetRequiredService<ILogger<Honua.Alerts.Ops.NotifyingWorkflowOperationStore>>()));
        services.AddSingleton<IWorkflowOperationReconciler, DeployWorkflowReconciler>();
        services.AddSingleton<IExecutionJobReconciler, ExecutionJobReconciler>();

        // Additive metadata-release layer-evolution lifecycle: create service, stage-action services,
        // reconciler, and its background loop. Sibling to the deploy reconciler; processes only
        // WorkflowOperationKind.MetadataRelease.
        services.AddSingleton<MetadataReleaseControlService>();
        services.AddSingleton<IMetadataReleasePreflightGate, MetadataReleasePreflightGate>();
        services.AddSingleton<IMetadataReleaseScriptExecutor, MetadataReleaseScriptExecutor>();
        services.AddSingleton<IMetadataReleaseDataJobDispatcher, MetadataReleaseDataJobDispatcher>();
        services.AddSingleton<IMetadataReleaseActivator, MetadataReleaseActivator>();
        services.AddSingleton<IMetadataReleaseSmokeChecker, MetadataReleaseSmokeChecker>();
        services.AddSingleton<IMetadataReleaseOperationReconciler, MetadataReleaseReconciler>();

        // Coordinated platform-upgrade release lifecycle (Demo C): conducts a container rollout, an
        // additive DB script migration, and a metadata-v2 activation as one ordered, gated,
        // rollback-able operation. Composes the deploy and metadata-release services via thin step
        // executors rather than a parallel lifecycle.
        services.AddSingleton<CoordinatedReleaseControlService>();
        services.AddSingleton<ICoordinatedContainerStepExecutor, CoordinatedContainerStepExecutor>();
        services.AddSingleton<ICoordinatedMetadataStepExecutor, CoordinatedMetadataStepExecutor>();
        services.AddSingleton<ICoordinatedReleaseReconciler, CoordinatedReleaseReconciler>();

        // Control-plane hybrid-trigger seam (design option C).
        // Phase 0: one dispatcher routes a reconcile-once request to the typed reconciler that owns the
        // operation kind. Both the poll loops and the Phase 1 event handler call this single method.
        // Phase 1: the event handler is the cloud (EventBridge -> Lambda) entrypoint, and the backstop
        // sweep self-heals dropped events; the TriggerMode flag chooses poll (on-prem) vs event (cloud).
        services.Configure<ControlPlaneTriggerOptions>(
            configuration.GetSection(ControlPlaneTriggerOptions.SectionName));
        services.AddSingleton<IOperationReconcileDispatcher, OperationReconcileDispatcher>();
        services.AddSingleton<ControlPlaneEventHandler>();

        // The backstop sweep is registered as a resolvable singleton so the EventBridge-Scheduler
        // entrypoint (ControlPlaneEventEndpoints) can drive a single sweep on demand. The long-running
        // host additionally registers it as a hosted timer in Poll mode (see Program.cs); in Event mode
        // the scheduler drives SweepOnceAsync instead, so the timer is not hosted.
        services.AddSingleton<ExecutionJobBackstopSweepService>();

        // The workflow-operation backstop is resolvable for the same reason: a control-plane
        // event-handler Lambda hosts neither backstop timer, so its backstop invocation drives both
        // sweeps through IControlPlaneLambdaEventTargets.
        services.AddSingleton<WorkflowOperationBackstopSweepService>();
        services.AddHonuaControlPlaneLambdaEventTargets();

        return services;
    }

    public static IServiceCollection AddHonuaControlPlaneLambdaEventTargets(this IServiceCollection services)
    {
        services.TryAddSingleton<IScheduledTickDispatcher, ScheduledTickDispatcher>();
        services.TryAddSingleton<IControlPlaneLambdaEventTargets, ControlPlaneLambdaEventTargets>();
        return services;
    }
}
