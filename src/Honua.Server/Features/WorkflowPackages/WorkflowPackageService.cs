// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.Orchestration;

namespace Honua.Server.Features.WorkflowPackages;

internal sealed class WorkflowPackageService(
    IWorkflowPackageStore store,
    IWorkflowNodeRegistry nodeRegistry,
    IGeoprocessingJobService geoprocessingJobService,
    TimeProvider clock,
    ILogger<WorkflowPackageService> logger,
    IWorkflowDefinitionStore? workflowDefinitionStore = null,
    WorkflowOrchestrationEngine? orchestrationEngine = null)
{
    public Task<IReadOnlyList<WorkflowPackage>> ListPackagesAsync(CancellationToken cancellationToken)
        => store.ListPackagesAsync(cancellationToken);

    public Task<WorkflowPackage?> GetPackageAsync(string packageId, CancellationToken cancellationToken)
        => store.GetPackageAsync(packageId, cancellationToken);

    public Task<IReadOnlyList<WorkflowPackageVersion>> ListVersionsAsync(
        string packageId,
        CancellationToken cancellationToken)
        => store.ListVersionsAsync(packageId, cancellationToken);

    public Task<WorkflowPackageVersion?> GetVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken)
        => store.GetVersionAsync(packageId, version, cancellationToken);

    public async Task<WorkflowPackage> SavePackageAsync(
        SaveWorkflowPackageRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Workflow package name is required.", nameof(request));
        }

        if (request.Graph is null)
        {
            throw new ArgumentException("Workflow package graph is required.", nameof(request));
        }

        var now = clock.GetUtcNow();
        var packageId = string.IsNullOrWhiteSpace(request.PackageId)
            ? CreatePackageId(request.Name)
            : request.PackageId.Trim();
        var existing = await store.GetPackageAsync(packageId, cancellationToken).ConfigureAwait(false);

        var package = new WorkflowPackage
        {
            PackageId = packageId,
            Name = request.Name.Trim(),
            Description = Normalize(request.Description),
            Namespace = Normalize(request.Namespace),
            Graph = request.Graph,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            CreatedBy = existing?.CreatedBy ?? ResolveActor(principal),
            UpdatedBy = ResolveActor(principal),
            LatestVersion = existing?.LatestVersion,
            Metadata = NormalizeMetadata(request.Metadata)
        };

        var saved = await store.SavePackageAsync(package, cancellationToken).ConfigureAwait(false);
        WorkflowPackageLog.PackageSaved(logger, saved.PackageId, saved.Graph.Nodes.Count);
        return saved;
    }

    public async Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var package = await RequirePackageAsync(packageId, cancellationToken).ConfigureAwait(false);
        var validation = await ValidateGraphAsync(package.Graph, principal, target: null, cancellationToken).ConfigureAwait(false);
        var hash = ComputePackageHash(package.Graph);
        validation = validation with { PackageHash = hash };

        if (!validation.IsValid)
        {
            throw new WorkflowPackageValidationException(validation);
        }

        var version = await store
            .CreateVersionAsync(packageId, hash, validation, ResolveActor(principal), cancellationToken)
            .ConfigureAwait(false);

        WorkflowPackageLog.PackageVersionCreated(logger, packageId, version.Version, hash);
        return version;
    }

    public async Task<WorkflowPackageValidationResult> ValidateVersionAsync(
        string packageId,
        int version,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var packageVersion = await RequireVersionAsync(packageId, version, cancellationToken).ConfigureAwait(false);
        return await ValidateGraphAsync(packageVersion.Graph, principal, target: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowDryRunResult> DryRunVersionAsync(
        string packageId,
        int version,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var packageVersion = await RequireVersionAsync(packageId, version, cancellationToken).ConfigureAwait(false);
        var validation = await ValidateGraphAsync(packageVersion.Graph, principal, target: null, cancellationToken).ConfigureAwait(false);
        var outputSchemas = await ResolveOutputSchemasAsync(packageVersion.Graph, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        if (!validation.IsValid)
        {
            return new WorkflowDryRunResult
            {
                Validation = validation,
                OutputSchemas = outputSchemas,
                Logs =
                [
                    new WorkflowDryRunLogEntry
                    {
                        Timestamp = now,
                        Level = "Warning",
                        Message = "Dry run skipped because validation failed."
                    }
                ],
                PackageHash = packageVersion.PackageHash
            };
        }

        await geoprocessingJobService.EnsureCallerAuthorizedAsync(
            principal,
            OperatorResourceType.Process,
            OperatorOperation.Execute,
            cancellationToken).ConfigureAwait(false);

        var hasDataBindings = WorkflowPackageGraphCompiler.HasCrossNodeDataBindings(packageVersion.Graph);
        ArtifactKind[] estimatedArtifacts;
        int estimatedSeconds;
        if (hasDataBindings)
        {
            estimatedArtifacts = ResolveArtifactKinds(outputSchemas);
            estimatedSeconds = Math.Max(1, packageVersion.Graph.Nodes.Count);
        }
        else
        {
            var plan = await CompileAnalysisPlanAsync(packageVersion, cancellationToken).ConfigureAwait(false);
            var dryRun = geoprocessingJobService.DryRunPlan(plan, principal);
            estimatedArtifacts = dryRun.EstimatedArtifacts.ToArray();
            estimatedSeconds = (int)Math.Ceiling(dryRun.EstimatedDurationSeconds);
            if (estimatedSeconds <= 0)
            {
                estimatedSeconds = Math.Max(1, packageVersion.Graph.Nodes.Count);
            }
        }

        var result = new WorkflowDryRunResult
        {
            Validation = validation,
            EstimatedDurationSeconds = estimatedSeconds,
            EstimatedCostWeight = Math.Max(1, packageVersion.Graph.Nodes.Count),
            OutputSchemas = outputSchemas,
            Logs =
            [
                new WorkflowDryRunLogEntry
                {
                    Timestamp = now,
                    Level = "Info",
                    Message = hasDataBindings
                        ? "Dry run completed against workflow graph bindings."
                        : "Dry run completed against the geoprocessing runtime."
                }
            ],
            Artifacts = estimatedArtifacts
                .Select(kind => new WorkflowDryRunArtifact
                {
                    ArtifactId = $"preview-{kind.ToString().ToLowerInvariant()}",
                    Kind = kind.ToString(),
                    Label = $"{kind} preview",
                    Schema = WorkflowSchemaMappings.FromArtifactKind(kind)
                })
                .ToArray(),
            PackageHash = packageVersion.PackageHash
        };

        WorkflowPackageLog.PackageDryRunCompleted(
            logger,
            packageId,
            version,
            result.EstimatedDurationSeconds);
        return result;
    }

    public async Task<WorkflowPublication> PublishVersionAsync(
        string packageId,
        int version,
        PublishWorkflowPackageRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packageVersion = await RequireVersionAsync(packageId, version, cancellationToken).ConfigureAwait(false);
        var eligibility = await ValidateGraphAsync(packageVersion.Graph, principal, request.Target, cancellationToken).ConfigureAwait(false);
        if (!eligibility.IsValid)
        {
            throw new WorkflowPackageValidationException(eligibility);
        }

        var publicationId = string.IsNullOrWhiteSpace(request.PublicationId)
            ? $"wfp-{Guid.NewGuid():N}"
            : request.PublicationId.Trim();
        var existingPublication = await store.GetPublicationAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (existingPublication != null)
        {
            throw new WorkflowPublicationConflictException(publicationId);
        }

        var schedule = request.Target == WorkflowPublicationTarget.Schedule
            ? request.Schedule ?? packageVersion.Graph.Schedule
            : null;
        if (request.Target == WorkflowPublicationTarget.Schedule && schedule is null)
        {
            var result = WorkflowPackageValidationResult.Failed(
                [
                    new WorkflowPackageValidationFailure
                    {
                        Code = "MISSING_SCHEDULE",
                        Message = "Schedule publication requires a schedule declaration.",
                        FieldPath = "schedule"
                    }
                ],
                packageVersion.PackageHash);
            throw new WorkflowPackageValidationException(result);
        }

        var workflowDefinitionId = request.Target == WorkflowPublicationTarget.Schedule
            ? $"workflow-package:{packageId}:v{version}"
            : null;
        if (request.Target == WorkflowPublicationTarget.Schedule && workflowDefinitionStore != null)
        {
            var definition = await CompileWorkflowDefinitionAsync(
                packageVersion,
                workflowDefinitionId!,
                packageId,
                schedule!,
                cancellationToken).ConfigureAwait(false);
            await workflowDefinitionStore.SetAsync(definition, cancellationToken).ConfigureAwait(false);
        }

        var processId = request.Target == WorkflowPublicationTarget.ProcessEndpoint
            ? Normalize(request.ProcessId) ?? $"workflow.{Slug(packageId)}.v{version.ToString(CultureInfo.InvariantCulture)}"
            : null;

        var provenance = BuildProvenance(packageVersion, publicationId, request.Target, processId);
        var publication = new WorkflowPublication
        {
            PublicationId = publicationId,
            PackageId = packageId,
            PackageVersion = version,
            PackageHash = packageVersion.PackageHash,
            Target = request.Target,
            Status = request.Enabled ? WorkflowPublicationStatus.Active : WorkflowPublicationStatus.Disabled,
            ProcessId = processId,
            Schedule = schedule,
            WorkflowDefinitionId = workflowDefinitionId,
            EndpointPath = $"/api/v1/console/workflow-publications/{publicationId}/runs",
            Eligibility = eligibility,
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = ResolveActor(principal),
            Provenance = provenance
        };

        var saved = await store.SavePublicationAsync(publication, cancellationToken).ConfigureAwait(false);
        WorkflowPackageLog.PackagePublished(logger, packageId, version, publicationId, request.Target.ToString());
        return saved;
    }

    public Task<IReadOnlyList<WorkflowPublication>> ListPublicationsAsync(
        string? packageId,
        CancellationToken cancellationToken)
        => store.ListPublicationsAsync(packageId, cancellationToken);

    public async Task<WorkflowPublicationRunResult> RunPublicationAsync(
        string publicationId,
        RunWorkflowPublicationRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var publication = await store.GetPublicationAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workflow publication '{publicationId}' was not found.");

        if (publication.Status != WorkflowPublicationStatus.Active)
        {
            throw new InvalidOperationException($"Workflow publication '{publicationId}' is disabled.");
        }

        var version = await RequireVersionAsync(
            publication.PackageId,
            publication.PackageVersion,
            cancellationToken).ConfigureAwait(false);

        var provenance = new Dictionary<string, string>(publication.Provenance, StringComparer.Ordinal);
        if (request.Parameters is { Count: > 0 } requestParameters)
        {
            foreach (var entry in requestParameters)
            {
                // Reserved workflow provenance keys are stamped server-side from the
                // publication. Skip caller-supplied overrides so jobs/runs keep accurate
                // package/version/hash traceability.
                if (string.IsNullOrWhiteSpace(entry.Key) || WorkflowPackageMetadataKeys.IsReserved(entry.Key))
                {
                    continue;
                }

                provenance[entry.Key] = entry.Value;
            }
        }

        if (publication.Target == WorkflowPublicationTarget.Schedule && orchestrationEngine != null)
        {
            var definition = await CompileWorkflowDefinitionAsync(
                version,
                publication.WorkflowDefinitionId ?? $"workflow-package:{publication.PackageId}:v{publication.PackageVersion}",
                publication.PackageId,
                publication.Schedule ?? version.Graph.Schedule ?? new WorkflowSchedule { CronExpression = "0 0 * * *" },
                cancellationToken).ConfigureAwait(false);
            var run = await orchestrationEngine
                .CreateRunAsync(definition, WorkflowTriggerKind.Manual, principal, provenance, cancellationToken)
                .ConfigureAwait(false);

            WorkflowPackageLog.PackagePublicationRunStarted(
                logger,
                publication.PackageId,
                publication.PackageVersion,
                publication.PublicationId,
                run.RunId);

            return new WorkflowPublicationRunResult
            {
                PublicationId = publication.PublicationId,
                PackageId = publication.PackageId,
                PackageVersion = publication.PackageVersion,
                Target = publication.Target,
                WorkflowRunId = run.RunId,
                Provenance = provenance
            };
        }

        await geoprocessingJobService.EnsureCallerAuthorizedAsync(
            principal,
            OperatorResourceType.Process,
            OperatorOperation.Execute);
        var plan = await CompileAnalysisPlanAsync(version, cancellationToken).ConfigureAwait(false);
        var job = await geoprocessingJobService
            .SubmitJobAsync(plan, request.IdempotencyKey, principal, provenance, cancellationToken)
            .ConfigureAwait(false);

        WorkflowPackageLog.PackagePublicationRunStarted(
            logger,
            publication.PackageId,
            publication.PackageVersion,
            publication.PublicationId,
            job.OperationId);

        return new WorkflowPublicationRunResult
        {
            PublicationId = publication.PublicationId,
            PackageId = publication.PackageId,
            PackageVersion = publication.PackageVersion,
            Target = publication.Target,
            JobId = job.OperationId,
            Provenance = provenance
        };
    }

    private async Task<WorkflowPackageValidationResult> ValidateGraphAsync(
        WorkflowGraph graph,
        ClaimsPrincipal principal,
        WorkflowPublicationTarget? target,
        CancellationToken cancellationToken)
    {
        var hash = ComputePackageHash(graph);
        var definitions = await GetNodeDefinitionLookupAsync(cancellationToken).ConfigureAwait(false);
        var structural = WorkflowPackageGraphValidator.Validate(graph, definitions, hash);
        var failures = structural.Failures.ToList();
        var warnings = structural.Warnings.ToList();

        ValidateTargetEligibility(graph, definitions, target, failures);

        if (failures.Count == 0)
        {
            try
            {
                await geoprocessingJobService.EnsureCallerAuthorizedAsync(
                    principal,
                    OperatorResourceType.Process,
                    OperatorOperation.Execute,
                    cancellationToken).ConfigureAwait(false);
                var plan = await CompileAnalysisPlanAsync(graph, hash, cancellationToken).ConfigureAwait(false);
                var planValidation = geoprocessingJobService.ValidatePlan(plan, principal);
                var dataBoundInputFieldPaths = WorkflowPackageGraphCompiler.BuildDataBoundInputFieldPaths(graph);
                warnings.AddRange(planValidation.Warnings);
                failures.AddRange(planValidation.Violations
                    .Where(violation => !IsDataBoundInputTypeValidation(violation, dataBoundInputFieldPaths))
                    .Select(violation => new WorkflowPackageValidationFailure
                    {
                        Code = violation.Code,
                        Message = violation.Message,
                        FieldPath = violation.FieldPath
                    }));
            }
            catch (WorkflowPackageValidationException ex)
            {
                failures.AddRange(ex.Result.Failures);
                warnings.AddRange(ex.Result.Warnings);
            }
        }

        return failures.Count == 0
            ? WorkflowPackageValidationResult.Success(hash, warnings)
            : WorkflowPackageValidationResult.Failed(failures, hash, warnings);
    }

    private static bool IsDataBoundInputTypeValidation(
        GeoprocessingValidationFailure violation,
        HashSet<string> dataBoundInputFieldPaths)
        => string.Equals(violation.Code, "INVALID_PARAMETER_VALUE", StringComparison.Ordinal)
           && violation.FieldPath != null
           && dataBoundInputFieldPaths.Contains(violation.FieldPath);

    private static void ValidateTargetEligibility(
        WorkflowGraph graph,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> definitions,
        WorkflowPublicationTarget? target,
        List<WorkflowPackageValidationFailure> failures)
    {
        if (target is null)
        {
            return;
        }

        // Job and process-endpoint publications compile to a single dispatched geoprocessing
        // job, which cannot resolve cross-step artifact bindings. Graphs that wire data between
        // nodes must publish to a Schedule target so the orchestration engine can chain outputs.
        if (target is WorkflowPublicationTarget.Job or WorkflowPublicationTarget.ProcessEndpoint
            && WorkflowPackageGraphCompiler.HasCrossNodeDataBindings(graph))
        {
            failures.Add(new WorkflowPackageValidationFailure
            {
                Code = "UNSUPPORTED_DATA_BINDING_TARGET",
                Message = $"Publication target '{target}' cannot resolve cross-node data bindings; publish data-wired workflows to a Schedule target.",
                FieldPath = "graph.edges"
            });
        }

        foreach (var node in graph.Nodes)
        {
            if (!definitions.TryGetValue(node.NodeTypeId, out var definition))
            {
                continue;
            }

            var supported = target switch
            {
                WorkflowPublicationTarget.Job => definition.CapabilityFlags.SupportsJob,
                WorkflowPublicationTarget.Schedule => definition.CapabilityFlags.SupportsSchedule,
                WorkflowPublicationTarget.ProcessEndpoint => definition.CapabilityFlags.SupportsProcessEndpoint,
                _ => false
            };

            if (!supported)
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "UNSUPPORTED_PUBLICATION_TARGET",
                    Message = $"Node '{node.NodeId}' cannot be published to target '{target}'.",
                    FieldPath = $"graph.nodes[{node.NodeId}].nodeTypeId"
                });
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, WorkflowNodeDefinition>> GetNodeDefinitionLookupAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await nodeRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Nodes.ToDictionary(node => node.NodeTypeId, StringComparer.Ordinal);
    }

    private async Task<AnalysisPlan> CompileAnalysisPlanAsync(
        WorkflowPackageVersion version,
        CancellationToken cancellationToken)
        => await CompileAnalysisPlanAsync(version.Graph, version.PackageHash, cancellationToken).ConfigureAwait(false);

    private async Task<AnalysisPlan> CompileAnalysisPlanAsync(
        WorkflowGraph graph,
        string packageHash,
        CancellationToken cancellationToken)
    {
        var definitions = await GetNodeDefinitionLookupAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<WorkflowPackageValidationFailure>();
        var steps = new List<AnalysisPlanStep>();
        var outputs = new HashSet<ArtifactKind>();

        foreach (var node in graph.Nodes)
        {
            if (!definitions.TryGetValue(node.NodeTypeId, out var definition) ||
                !ProcessCatalogWorkflowNodeProvider.TryGetProcessId(node.NodeTypeId, out var processId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "UNSUPPORTED_NODE_COMPILER",
                    Message = $"Node '{node.NodeId}' does not have a compiler for geoprocessing execution.",
                    FieldPath = $"graph.nodes[{node.NodeId}].nodeTypeId"
                });
                continue;
            }

            foreach (var output in definition.OutputSchemas)
            {
                if (Enum.TryParse<ArtifactKind>(output.Name, ignoreCase: false, out var kind))
                {
                    outputs.Add(kind);
                }
            }

            steps.Add(new AnalysisPlanStep
            {
                StepId = node.NodeId,
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = processId,
                Inputs = WorkflowPackageGraphCompiler.BuildStepInputs(node, graph),
                DependsOn = graph.Edges
                    .Where(edge => edge.Kind != WorkflowEdgeKind.Failure &&
                                   string.Equals(edge.TargetNodeId, node.NodeId, StringComparison.Ordinal))
                    .Select(edge => edge.SourceNodeId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            });
        }

        if (failures.Count > 0)
        {
            throw new WorkflowPackageValidationException(
                WorkflowPackageValidationResult.Failed(failures, packageHash));
        }

        return new AnalysisPlan
        {
            PlanId = $"workflow-package:{packageHash[..Math.Min(packageHash.Length, 16)]}",
            IntentId = "workflow-package",
            Steps = steps,
            Outputs = outputs.Count == 0 ? [ArtifactKind.File] : outputs.ToArray()
        };
    }

    private async Task<WorkflowDefinition> CompileWorkflowDefinitionAsync(
        WorkflowPackageVersion version,
        string workflowDefinitionId,
        string packageName,
        WorkflowSchedule schedule,
        CancellationToken cancellationToken)
    {
        var definitions = await GetNodeDefinitionLookupAsync(cancellationToken).ConfigureAwait(false);
        var steps = new List<WorkflowStepDefinition>();
        var failures = new List<WorkflowPackageValidationFailure>();

        foreach (var node in version.Graph.Nodes)
        {
            if (!definitions.TryGetValue(node.NodeTypeId, out var definition) ||
                !ProcessCatalogWorkflowNodeProvider.TryGetProcessId(node.NodeTypeId, out var processId))
            {
                failures.Add(new WorkflowPackageValidationFailure
                {
                    Code = "UNSUPPORTED_NODE_COMPILER",
                    Message = $"Node '{node.NodeId}' cannot be compiled into a scheduled workflow definition.",
                    FieldPath = $"graph.nodes[{node.NodeId}].nodeTypeId"
                });
                continue;
            }

            var outputs = definition.OutputSchemas
                .Select(output => Enum.TryParse<ArtifactKind>(output.Name, out var kind) ? kind : ArtifactKind.File)
                .Distinct()
                .ToArray();
            steps.Add(new WorkflowStepDefinition
            {
                StepId = node.NodeId,
                Plan = new AnalysisPlan
                {
                    PlanId = $"{workflowDefinitionId}:{node.NodeId}",
                    IntentId = "workflow-package",
                    Steps =
                    [
                        new AnalysisPlanStep
                        {
                            StepId = node.NodeId,
                            Kind = AnalysisPlanStepKind.Geoprocess,
                            ProcessId = processId,
                            Inputs = WorkflowPackageGraphCompiler.BuildStepInputs(node, version.Graph)
                        }
                    ],
                    Outputs = outputs.Length == 0 ? [ArtifactKind.File] : outputs
                },
                DependsOn = version.Graph.Edges
                    .Where(edge => edge.Kind != WorkflowEdgeKind.Failure &&
                                   string.Equals(edge.TargetNodeId, node.NodeId, StringComparison.Ordinal))
                    .Select(edge => edge.SourceNodeId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                InputBindings = WorkflowPackageGraphCompiler.BuildStepInputBindings(version.Graph, node.NodeId),
                FailurePolicy = WorkflowStepFailurePolicy.Fail
            });
        }

        if (failures.Count > 0)
        {
            throw new WorkflowPackageValidationException(
                WorkflowPackageValidationResult.Failed(failures, version.PackageHash));
        }

        var now = clock.GetUtcNow();
        return new WorkflowDefinition
        {
            WorkflowId = workflowDefinitionId,
            Name = packageName,
            Description = $"Workflow package {version.PackageId} v{version.Version}",
            Steps = steps,
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Cron,
                CronExpression = schedule.CronExpression,
                TimeZone = schedule.TimeZone,
                Enabled = schedule.Enabled
            },
            CreatedAt = now,
            UpdatedAt = now,
            Metadata = BuildProvenance(version, workflowDefinitionId, WorkflowPublicationTarget.Schedule, processId: null)
        };
    }

    private async Task<IReadOnlyList<WorkflowNodePortSchema>> ResolveOutputSchemasAsync(
        WorkflowGraph graph,
        CancellationToken cancellationToken)
    {
        var definitions = await GetNodeDefinitionLookupAsync(cancellationToken).ConfigureAwait(false);
        return graph.Nodes
            .SelectMany(node => definitions.TryGetValue(node.NodeTypeId, out var definition)
                ? definition.OutputSchemas
                : Array.Empty<WorkflowNodePortSchema>())
            .ToArray();
    }

    private static ArtifactKind[] ResolveArtifactKinds(
        IReadOnlyList<WorkflowNodePortSchema> outputSchemas)
    {
        var kinds = new HashSet<ArtifactKind>();
        foreach (var schema in outputSchemas)
        {
            if (Enum.TryParse<ArtifactKind>(schema.Name, ignoreCase: false, out var kind))
            {
                kinds.Add(kind);
            }
        }

        return kinds.Count == 0 ? [ArtifactKind.File] : kinds.ToArray();
    }

    private async Task<WorkflowPackage> RequirePackageAsync(
        string packageId,
        CancellationToken cancellationToken)
        => await store.GetPackageAsync(packageId, cancellationToken).ConfigureAwait(false)
           ?? throw new KeyNotFoundException($"Workflow package '{packageId}' was not found.");

    private async Task<WorkflowPackageVersion> RequireVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken)
        => await store.GetVersionAsync(packageId, version, cancellationToken).ConfigureAwait(false)
           ?? throw new KeyNotFoundException($"Workflow package '{packageId}' version {version} was not found.");

    private static Dictionary<string, string> BuildProvenance(
        WorkflowPackageVersion version,
        string publicationId,
        WorkflowPublicationTarget target,
        string? processId)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WorkflowPackageMetadataKeys.PackageId] = version.PackageId,
            [WorkflowPackageMetadataKeys.PackageVersion] = version.Version.ToString(CultureInfo.InvariantCulture),
            [WorkflowPackageMetadataKeys.PublicationId] = publicationId,
            [WorkflowPackageMetadataKeys.PackageHash] = version.PackageHash,
            [WorkflowPackageMetadataKeys.Target] = target.ToString()
        };

        if (!string.IsNullOrWhiteSpace(processId))
        {
            metadata[WorkflowPackageMetadataKeys.ProcessId] = processId;
        }

        return metadata;
    }

    private static string ComputePackageHash(WorkflowGraph graph)
    {
        var builder = new StringBuilder();
        builder.Append(graph.SchemaVersion).Append('|').Append(graph.WorkerProfile).Append('|');
        foreach (var node in graph.Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            builder.Append("node:")
                .Append(node.NodeId)
                .Append(':')
                .Append(node.NodeTypeId)
                .Append(':')
                .Append(node.WorkerProfile);

            foreach (var parameter in node.Parameters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                builder.Append(':').Append(parameter.Key).Append('=').Append(parameter.Value);
            }
        }

        foreach (var edge in graph.Edges
                     .OrderBy(edge => edge.SourceNodeId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.Kind))
        {
            builder.Append("|edge:")
                .Append(edge.SourceNodeId)
                .Append("->")
                .Append(edge.TargetNodeId)
                .Append(':')
                .Append(edge.Kind)
                .Append(':')
                .Append(edge.SourcePort)
                .Append(':')
                .Append(edge.TargetPort);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreatePackageId(string name)
    {
        var candidate = $"pkg-{Slug(name)}-{Guid.NewGuid():N}";
        return candidate[..Math.Min(52, candidate.Length)];
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "workflow" : slug;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string>? metadata)
        => metadata == null
            ? new Dictionary<string, string>()
            : metadata
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static string? ResolveActor(ClaimsPrincipal principal)
        => principal.Identity?.Name
           ?? principal.FindFirst("sub")?.Value
           ?? principal.FindFirst("client_id")?.Value;
}

internal sealed class WorkflowPackageValidationException(WorkflowPackageValidationResult result)
    : InvalidOperationException("Workflow package validation failed.")
{
    public WorkflowPackageValidationResult Result { get; } = result;
}

/// <summary>
/// Raised when a publish request reuses an existing publication id. Derives from
/// <see cref="InvalidOperationException"/> so the Console endpoint maps it to 409 Conflict.
/// </summary>
internal sealed class WorkflowPublicationConflictException(string publicationId)
    : InvalidOperationException($"Workflow publication '{publicationId}' already exists.")
{
    public string PublicationId { get; } = publicationId;
}
