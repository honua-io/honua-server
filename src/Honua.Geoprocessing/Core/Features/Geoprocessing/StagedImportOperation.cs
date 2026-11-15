// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

// An agent previews a verified job artifact, then proposes a create-only import
// into a server-owned fresh table. A human approves; approved replay imports only the pinned binding.
internal static class StagedImportOperation
{
    public const string ProviderId = "honua.geoprocessing.staged-import";

    public const string PreviewOperationId = "dataset.stage.preview";

    public const string ImportOperationId = "dataset.stage.import";

    public const string PreviewFingerprintParameter = "previewFingerprint";

    internal static readonly TimeSpan ApprovalWindow = TimeSpan.FromMinutes(30);

    private static readonly string[] SourceParameters =
        ["intentId", "schema", "targetId", "jobId", "resultPackageId", "artifactId", "referenceSha256", "targetSrid"];

    public static IReadOnlyList<IOperationDescriptor> BuildDescriptors() =>
    [
        Descriptor(new DescriptorSpec(PreviewOperationId, "Preview staged dataset import",
            "Verifies a completed job artifact and returns its content binding and server-owned staging table without importing.")
        {
            Approval = OperationApprovalModel.None,
            SideEffect = OperationSideEffectClass.ReadOnly,
        }),
        Descriptor(new DescriptorSpec(ImportOperationId, "Import previewed dataset into fresh staging",
            "Imports a previewed job artifact into a new server-owned staging table after human approval. Never writes to an existing table.")
        {
            Approval = OperationApprovalModel.OperatorGate,
            SideEffect = OperationSideEffectClass.MutatesData,
            Extra = [RequiredParameter(PreviewFingerprintParameter)],
        }),
    ];

    // The server owns the destination: callers cannot name, reuse or overwrite a table.
    public static string DestinationTable(string tenantId, string actorId, string intentId)
        => "staged_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", "staged-import/v1", tenantId, actorId, intentId))))[..24];

    public static ServerJobArtifactImportCommand ParseCommand(OperationRequest request, string tenantId, string actorId)
    {
        if (request.Parameters.ContainsKey("table"))
        {
            throw new GeoprocessingValidationException("The staging table is server-owned; do not supply 'table'.");
        }

        var intentId = Required(request, "intentId");
        var targetSrid = ParseInt(Required(request, "targetSrid"), "targetSrid");
        int? sourceSrid = request.Parameters.TryGetValue("sourceSrid", out var source) && !string.IsNullOrWhiteSpace(source)
            ? ParseInt(source, "sourceSrid") : null;
        return new ServerJobArtifactImportCommand(
            intentId, tenantId, actorId, Required(request, "schema"),
            DestinationTable(tenantId, actorId, intentId),
            new DatasetImportServerJobArtifactReference(
                Required(request, "targetId"), Required(request, "jobId"), Required(request, "resultPackageId"),
                Required(request, "artifactId"), Required(request, "referenceSha256")),
            targetSrid, sourceSrid);
    }

    public static ServerJobArtifactImportContext ImportContext(IServiceProvider services, ClaimsPrincipal principal)
        => new(principal, services.GetRequiredService<IGeoprocessingJobService>(),
            services.GetRequiredService<IFileImportService>())
        {
            Services = services,
        };

    // The current request's authenticated principal reads the artifact: the requester on proposal,
    // the approving human on replay. Each must hold import authority through the shared admin policy;
    // authority is never synthesized from a stored principal id.
    public static async Task<ClaimsPrincipal> AuthorizedPrincipalAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (context?.User is not { Identity.IsAuthenticated: true } user)
        {
            throw new GeoprocessingPreconditionFailedException(
                "A staged import needs an authenticated request; it cannot run without one.");
        }

        if (!await ImportAdminAuthorization.IsAuthorizedAsync(context, user, cancellationToken).ConfigureAwait(false))
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "Caller requires admin write authority to stage a dataset import.");
        }

        return user;
    }

    public static (string TenantId, string ActorId) RequireScope(OperationPolicyContext context)
        => string.IsNullOrWhiteSpace(context.TenantId) || string.IsNullOrWhiteSpace(context.PrincipalId)
            ? throw new GeoprocessingValidationException("A tenant and requesting principal are required for a staged import.")
            : (context.TenantId, context.PrincipalId);

    private sealed record DescriptorSpec(string Id, string Title, string Description)
    {
        public required OperationApprovalModel Approval { get; init; }
        public required OperationSideEffectClass SideEffect { get; init; }
        public IReadOnlyList<OperationParameterDescriptor> Extra { get; init; } = [];
    }

    private static OperationDescriptor Descriptor(DescriptorSpec spec) => new()
    {
        OperationId = spec.Id,
        ProviderId = ProviderId,
        Title = spec.Title,
        Description = spec.Description,
        Category = "import",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = spec.Approval,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ResourceScope,
            SideEffectClass = spec.SideEffect,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = false,
        },
        InputSchema =
        [
            .. SourceParameters.Select(RequiredParameter),
            OptionalParameter("sourceSrid"),
            .. spec.Extra,
        ],
        OutputSchema = [],
    };

    private static OperationParameterDescriptor RequiredParameter(string name) => ParameterFor(name) with { Required = true };

    private static OperationParameterDescriptor OptionalParameter(string name) => ParameterFor(name) with { Required = false };

    private static OperationParameterDescriptor ParameterFor(string name) => new()
    {
        Name = name,
        Title = name,
        Required = true,
        Schema = new WorkflowSchemaDefinition
        {
            Type = name is "targetSrid" or "sourceSrid" ? WorkflowSchemaValueType.WholeNumber : WorkflowSchemaValueType.Text,
        },
    };

    internal static string Required(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new GeoprocessingValidationException($"Required operation parameter '{name}' is missing.");

    private static int ParseInt(string value, string name)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new GeoprocessingValidationException($"'{name}' must be a 32-bit integer.");
}
