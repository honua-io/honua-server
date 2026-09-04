// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

internal interface IAdminHttpOperationDefinition
{
    string OperationId { get; }
    string Title { get; }
    HttpMethod Method { get; }
    string Path { get; }
    string OpenApiOperationId { get; }
    OperationSideEffectClass SideEffect { get; }
    OperationBlastRadiusClass BlastRadius { get; }
    bool SupportsDryRun { get; }
    string? DryRunPath { get; }
    HttpMethod? DryRunMethod { get; }
    string? ContentType { get; }
    OperationApprovalModel? ApprovalModel { get; }
    OperationClass OperationClass { get; }
}

/// <summary>Release and operate descriptors projected from the shipped Admin OpenAPI contract.</summary>
internal static class AdminOperateOperationCatalog
{
    internal sealed record Definition(
        string OperationId,
        string Title,
        HttpMethod Method,
        string Path,
        string OpenApiOperationId,
        OperationSideEffectClass SideEffect,
        OperationBlastRadiusClass BlastRadius,
        bool SupportsDryRun = false,
        string? DryRunPath = null,
        HttpMethod? DryRunMethod = null,
        string? ContentType = null,
        OperationApprovalModel? ApprovalModel = null,
        OperationClass OperationClass = OperationClass.AdminConfigChange) : IAdminHttpOperationDefinition;

    public static IReadOnlyList<Definition> Definitions { get; } =
    [
        Read("admin.metadata.release-packages.list", "List metadata release packages", "/metadata/release-packages", "listMetadataReleasePackages"),
        Read("admin.metadata.release-packages.get", "Get metadata release package", "/metadata/release-packages/{packageId}", "getMetadataReleasePackage"),
        Read("admin.metadata.release-packages.gitops-manifest", "Get metadata release GitOps manifest", "/metadata/release-packages/{packageId}/gitops-manifest", "getMetadataReleaseGitOpsManifest"),
        Write("admin.metadata.release-packages.create", "Create metadata release package", HttpMethod.Post, "/metadata/release-packages", "createMetadataReleasePackage", OperationSideEffectClass.CreatesMetadata, operationClass: OperationClass.MetadataRelease),
        new("admin.metadata.prevalidate", "Prevalidate metadata release", HttpMethod.Post, "/metadata/prevalidate", "prevalidateMetadataReleasePackageCompatibility", OperationSideEffectClass.CreatesMetadata, OperationBlastRadiusClass.ResourceScope, true, "/metadata/prevalidate", HttpMethod.Post, ApprovalModel: OperationApprovalModel.None),
        Write("admin.metadata.releases.activate", "Activate metadata release", HttpMethod.Post, "/metadata/releases/operations", "createMetadataReleaseOperation", OperationSideEffectClass.MutatesMetadata, OperationBlastRadiusClass.DeploymentScope, OperationClass.MetadataRelease),
        Read("admin.metadata.releases.status", "Get metadata release status", "/metadata/releases/{packageId}/operation", "getMetadataReleaseOperationByPackageId"),
        Write("admin.metadata.coordinated-releases.rollback", "Roll back coordinated release", HttpMethod.Post, "/metadata/coordinated-releases/operations/{operationId}/rollback", "rollbackCoordinatedReleaseOperation", OperationSideEffectClass.DestroysState, OperationBlastRadiusClass.DeploymentScope, OperationClass.MetadataRelease),
        Read("admin.cache.status", "Get cache status", "/cache/status", "getAdminCacheStatus"),
        Write("admin.cache.invalidate", "Invalidate cache", HttpMethod.Post, "/cache/invalidate", "invalidateAdminCache", OperationSideEffectClass.DestroysState),
        Read("admin.license.status", "Get license status", "/license/status", "getPlatformLicenseStatus"),
        new("admin.license.upload", "Upload license", HttpMethod.Post, "/license/upload", "uploadLicenseFile", OperationSideEffectClass.MutatesMetadata, OperationBlastRadiusClass.DeploymentScope, false, ContentType: "application/octet-stream"),
        Read("admin.license.entitlements", "Get license entitlements", "/license/entitlements", "getLicenseEntitlements"),
        Read("admin.configuration.summary", "Get configuration summary", "/configuration/summary", "getConfigurationSummary"),
        new("admin.configuration.secrets.validate", "Validate configuration secrets", HttpMethod.Get, "/configuration/secrets/validate", "validateConfigurationSecrets", OperationSideEffectClass.ReadOnly, OperationBlastRadiusClass.ResourceScope, true, "/configuration/secrets/validate", HttpMethod.Get),
        Read("admin.server.capabilities", "Get server capabilities", "/capabilities", "getAdminCapabilities"),
        Read("admin.server.features", "Get server features", "/features", "getFeatureOverview")
    ];

    public static IReadOnlyList<OperationDescriptor> Descriptors { get; } = BuildDescriptors();

    private static Definition Read(string id, string title, string path, string openApiId) =>
        new(id, title, HttpMethod.Get, path, openApiId, OperationSideEffectClass.ReadOnly, OperationBlastRadiusClass.ResourceScope);

    private static Definition Write(string id, string title, HttpMethod method, string path, string openApiId,
        OperationSideEffectClass sideEffect, OperationBlastRadiusClass blastRadius = OperationBlastRadiusClass.ResourceScope,
        OperationClass operationClass = OperationClass.AdminConfigChange) =>
        new(id, title, method, path, openApiId, sideEffect, blastRadius, OperationClass: operationClass);

    private static OperationDescriptor[] BuildDescriptors()
    {
        using var stream = typeof(AdminOperateOperationCatalog).Assembly.GetManifestResourceStream("Honua.Server.admin-api.json")
            ?? throw new InvalidOperationException("Embedded admin-api.json contract was not found.");
        using var document = JsonDocument.Parse(stream);
        return Definitions.Select(definition => BuildDescriptor(document.RootElement, definition)).ToArray();
    }

    internal static OperationDescriptor BuildDescriptor(JsonElement root, IAdminHttpOperationDefinition definition)
    {
        var operation = FindOperation(root, definition.OpenApiOperationId);
        var inputs = BuildInputs(root, operation);
        return new OperationDescriptor
        {
            OperationId = definition.OperationId,
            ProviderId = ServicePublishOperation.ProviderId,
            Title = definition.Title,
            Description = operation.TryGetProperty("description", out var description) ? description.GetString()! : definition.Title,
            Category = "admin",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = definition.ApprovalModel
                ?? (definition.SideEffect == OperationSideEffectClass.ReadOnly ? OperationApprovalModel.None : OperationApprovalModel.OperatorGate),
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = definition.BlastRadius,
                SideEffectClass = definition.SideEffect,
                Determinism = OperationDeterminism.RuntimeDynamic,
                SupportsDryRun = definition.SupportsDryRun
            },
            InputSchema = inputs,
            OutputSchema = BuildOutputSchema(root, operation)
        };
    }

    private static List<OperationParameterDescriptor> BuildInputs(JsonElement root, JsonElement operation)
    {
        var inputs = new List<OperationParameterDescriptor>();
        if (operation.TryGetProperty("parameters", out var parameters))
        {
            foreach (var parameter in parameters.EnumerateArray().Select(parameter => Resolve(root, parameter)))
            {
                var name = parameter.GetProperty("name").GetString()!;
                inputs.Add(Parameter(name, parameter.TryGetProperty("description", out var d) ? d.GetString()! : name,
                    parameter.TryGetProperty("required", out var required) && required.GetBoolean(), ConvertSchema(root, parameter.GetProperty("schema"))));
            }
        }

        if (TryGetContentSchema(operation, "requestBody", out var bodySchema))
        {
            var resolved = Resolve(root, bodySchema);
            var requiredNames = resolved.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(static value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            if (resolved.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject().Where(property => !inputs.Any(input => input.Name == property.Name)))
                    inputs.Add(Parameter(property.Name, Description(property.Value, property.Name), requiredNames.Contains(property.Name), ConvertSchema(root, property.Value)));
            }
            else
            {
                inputs.Add(Parameter("body", "Request body", true, ConvertSchema(root, resolved)));
            }
        }
        return inputs;
    }

    private static OperationParameterDescriptor Parameter(string name, string title, bool required, WorkflowSchemaDefinition schema) =>
        new() { Name = name, Title = title, Required = required, Schema = schema };

    private static string Description(JsonElement value, string fallback) =>
        value.TryGetProperty("description", out var description) ? description.GetString()! : fallback;

    internal static JsonElement FindOperation(JsonElement root, string operationId)
    {
        foreach (var method in root.GetProperty("paths").EnumerateObject()
                     .SelectMany(static path => path.Value.EnumerateObject())
                     .Where(method => method.Value.ValueKind == JsonValueKind.Object &&
                         method.Value.TryGetProperty("operationId", out var id) && id.GetString() == operationId))
            return method.Value;
        throw new InvalidOperationException($"Admin OpenAPI operation '{operationId}' was not found.");
    }

    private static IReadOnlyList<OperationParameterDescriptor> BuildOutputSchema(JsonElement root, JsonElement operation)
    {
        foreach (var response in operation.GetProperty("responses").EnumerateObject()
                     .Where(static response => response.Name[0] == '2')
                     .OrderBy(static item => item.Name, StringComparer.Ordinal)
                     .Where(static response => TryGetContentSchema(response.Value, "content", out _)))
        {
            _ = TryGetContentSchema(response.Value, "content", out var schema);
            return [Parameter("response", "Admin API response", true, ConvertSchema(root, schema))];
        }
        return [];
    }

    private static bool TryGetContentSchema(JsonElement owner, string property, out JsonElement schema)
    {
        schema = default;
        if (!owner.TryGetProperty(property, out var container)) return false;
        var content = property == "content" ? container : container.TryGetProperty("content", out var nested) ? nested : default;
        if (content.ValueKind != JsonValueKind.Object) return false;
        foreach (var mediaType in content.EnumerateObject().Where(static mediaType => mediaType.Value.TryGetProperty("schema", out _)))
        {
            schema = mediaType.Value.GetProperty("schema");
            return true;
        }
        return false;
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference)) return schema;
        var current = root;
        foreach (var segment in reference.GetString()![2..].Split('/')) current = current.GetProperty(segment);
        return current;
    }

    internal static WorkflowSchemaDefinition ConvertSchema(JsonElement root, JsonElement schema)
    {
        var resolved = Resolve(root, schema);
        var type = resolved.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "object";
        return new WorkflowSchemaDefinition
        {
            Type = type switch { "integer" => WorkflowSchemaValueType.WholeNumber, "number" => WorkflowSchemaValueType.DecimalNumber, "boolean" => WorkflowSchemaValueType.Flag, "array" => WorkflowSchemaValueType.List, "object" => WorkflowSchemaValueType.Structured, _ => WorkflowSchemaValueType.Text },
            Format = resolved.TryGetProperty("format", out var format) ? format.GetString() : null,
            EnumValues = resolved.TryGetProperty("enum", out var values) ? values.EnumerateArray().Select(static value => value.ToString()).ToArray() : [],
            Items = resolved.TryGetProperty("items", out var items) ? ConvertSchema(root, items) : null,
            Properties = resolved.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().ToDictionary(static property => property.Name, property => ConvertSchema(root, property.Value), StringComparer.Ordinal)
                : new Dictionary<string, WorkflowSchemaDefinition>(),
            RequiredProperties = resolved.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(static value => value.GetString()!).ToArray()
                : []
        };
    }
}

internal sealed class AdminOperateOperationApprovalRequestMapper(
    IAdminHttpOperationDefinition definition) : IOperationApprovalRequestMapper
{
    public string OperationId => definition.OperationId;

    public OperationGatewayRequest Map(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId)
        {
            throw new ArgumentException($"The mapper only accepts {OperationId} requests.", nameof(request));
        }

        var payload = AdminApiOperationApprovalPayload.From(request, context);
        var serialized = JsonSerializer.Serialize(
            payload,
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = definition.OperationClass,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = new OperationProposalPlan
            {
                Summary = $"Execute {OperationId} through the canonical admin operation runtime.",
                RiskLevel = definition.SideEffect == OperationSideEffectClass.DestroysState
                    ? ProposalRiskLevel.High
                    : ProposalRiskLevel.Medium,
                ExecutionPayload = serialized,
            },
        };
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Deserialize(
            request.Plan?.ExecutionPayload ?? request.ExecutionPayload
                ?? throw new InvalidOperationException("The persisted admin operation replay payload is unavailable."),
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
            ?? throw new InvalidOperationException("The persisted admin operation replay payload is invalid.");
        if (payload.OperationId != OperationId)
        {
            throw new InvalidOperationException("The persisted admin operation replay identity does not match its mapper.");
        }

        return new OperationApprovalReplayMapping
        {
            Request = payload.ToOperationRequest(),
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
    }
}
