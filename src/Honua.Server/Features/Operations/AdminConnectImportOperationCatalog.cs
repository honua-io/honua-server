// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Operations;

internal static class AdminConnectImportOperationCatalog
{
    internal sealed record Definition(string OperationId, string Title, HttpMethod Method, string Path,
        string OpenApiOperationId, OperationSideEffectClass SideEffect, string? ContentType = null,
        bool SupportsDryRun = false, string? DryRunPath = null);

    public static IReadOnlyList<Definition> Definitions { get; } =
    [
        Read("admin.connections.list", "List connections", "/connections", "getConnections"),
        Write("admin.connections.create", "Create connection", HttpMethod.Post, "/connections", "createConnection", OperationSideEffectClass.CreatesMetadata),
        Read("admin.connections.get", "Get connection", "/connections/{id}", "getConnection"),
        Write("admin.connections.update", "Update connection", HttpMethod.Put, "/connections/{id}", "updateConnection", OperationSideEffectClass.MutatesMetadata),
        Write("admin.connections.delete", "Delete connection", HttpMethod.Delete, "/connections/{id}", "deleteConnection", OperationSideEffectClass.DestroysState),
        Read("admin.connections.tables.discover", "Discover connection tables", "/connections/{id}/tables", "getConnectionTables"),
        new("admin.connections.test-draft", "Test draft connection", HttpMethod.Post, "/connections/test", "testDraftConnection", OperationSideEffectClass.ReadOnly, SupportsDryRun: true, DryRunPath: "/connections/test"),
        new("admin.connections.test", "Test connection", HttpMethod.Post, "/connections/{id}/test", "testConnection", OperationSideEffectClass.ReadOnly, SupportsDryRun: true, DryRunPath: "/connections/{id}/test"),
        new("admin.connections.tables.validate", "Validate connection table", HttpMethod.Post, "/connections/{id}/tables/validate", "validateConnectionTableForPublish", OperationSideEffectClass.ReadOnly, SupportsDryRun: true, DryRunPath: "/connections/{id}/tables/validate"),
        Write("admin.connections.extents.refresh", "Refresh layer extents", HttpMethod.Post, "/connections/{id}/layers/extents/refresh", "refreshConnectionLayerExtents", OperationSideEffectClass.MutatesMetadata),
        Write("admin.connections.features.refresh", "Refresh layer features", HttpMethod.Post, "/connections/{id}/layers/{layerId}/features/refresh", "refreshConnectionLayerFeatures", OperationSideEffectClass.MutatesMetadata),
        Read("admin.import.formats", "Get import formats", "/import/formats", "getImportFormats"),
        new("admin.import.preview", "Preview import file", HttpMethod.Post, "/import/preview", "previewImportFile", OperationSideEffectClass.ReadOnly, "multipart/form-data", true, "/import/preview"),
        new("admin.import.preview-url", "Preview import URL", HttpMethod.Post, "/import/preview-url", "previewImportFileFromUrl", OperationSideEffectClass.ReadOnly, SupportsDryRun: true, DryRunPath: "/import/preview-url"),
        Write("admin.import.upload", "Import uploaded file", HttpMethod.Post, "/import/upload", "uploadImportFile", OperationSideEffectClass.CreatesMetadata, "multipart/form-data"),
        Write("admin.import.upload-url", "Import file from URL", HttpMethod.Post, "/import/upload-url", "uploadImportFileFromUrl", OperationSideEffectClass.CreatesMetadata),
        Read("admin.import.limits", "Get import limits", "/import/limits", "getImportLimits"),
        Read("admin.import.jobs.list", "List import jobs", "/import/jobs", "getActiveImportJobs"),
        Read("admin.import.jobs.get", "Get import job", "/import/jobs/{jobId}", "getImportJobStatus"),
        Write("admin.import.jobs.cancel", "Cancel import job", HttpMethod.Post, "/import/jobs/{jobId}/cancel", "cancelImportJob", OperationSideEffectClass.DestroysState),
        Read("admin.import.uploads.progress", "Get import upload progress", "/import/uploads/{uploadId}/progress", "getImportUploadProgress")
    ];

    public static IReadOnlyList<OperationDescriptor> Descriptors { get; } = BuildDescriptors();

    private static Definition Read(string id, string title, string path, string openApiId) =>
        new(id, title, HttpMethod.Get, path, openApiId, OperationSideEffectClass.ReadOnly);

    private static Definition Write(string id, string title, HttpMethod method, string path, string openApiId,
        OperationSideEffectClass sideEffect, string? contentType = null) => new(id, title, method, path, openApiId, sideEffect, contentType);

    private static OperationDescriptor[] BuildDescriptors()
    {
        using var stream = typeof(AdminConnectImportOperationCatalog).Assembly.GetManifestResourceStream("Honua.Server.admin-api.json")
            ?? throw new InvalidOperationException("Embedded admin-api.json contract was not found.");
        using var document = JsonDocument.Parse(stream);
        return Definitions.Select(definition => BuildDescriptor(document.RootElement, definition)).ToArray();
    }

    private static OperationDescriptor BuildDescriptor(JsonElement root, Definition definition)
    {
        var operation = FindOperation(root, definition.OpenApiOperationId);
        return new OperationDescriptor
        {
            OperationId = definition.OperationId,
            ProviderId = ServicePublishOperation.ProviderId,
            Title = definition.Title,
            Description = operation.TryGetProperty("description", out var description) ? description.GetString()! : definition.Title,
            Category = "admin",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = definition.SideEffect == OperationSideEffectClass.ReadOnly ? OperationApprovalModel.None : OperationApprovalModel.OperatorGate,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = OperationBlastRadiusClass.ResourceScope,
                SideEffectClass = definition.SideEffect,
                Determinism = OperationDeterminism.RuntimeDynamic,
                SupportsDryRun = definition.SupportsDryRun
            },
            InputSchema = BuildInputs(root, operation),
            OutputSchema = BuildOutputs(root, operation)
        };
    }

    private static List<OperationParameterDescriptor> BuildInputs(JsonElement root, JsonElement operation)
    {
        var result = new List<OperationParameterDescriptor>();
        if (operation.TryGetProperty("parameters", out var parameters))
        {
            foreach (var parameter in parameters.EnumerateArray().Select(value => Resolve(root, value)))
                result.Add(Parameter(parameter.GetProperty("name").GetString()!, parameter, parameter.TryGetProperty("required", out var required) && required.GetBoolean(), root));
        }
        if (TryContentSchema(operation, "requestBody", out var body))
        {
            var schema = Resolve(root, body);
            var required = schema.TryGetProperty("required", out var names)
                ? names.EnumerateArray().Select(static value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject().Where(property => result.All(item => item.Name != property.Name)))
                    result.Add(Parameter(property.Name, property.Value, required.Contains(property.Name), root));
            }
            else result.Add(new OperationParameterDescriptor { Name = "body", Title = "Request body", Required = true, Schema = ConvertSchema(root, schema) });
        }
        if (result.Any(static parameter => parameter.Name == "file") &&
            result.All(static parameter => parameter.Name != "fileName"))
        {
            result.Add(new OperationParameterDescriptor
            {
                Name = "fileName",
                Title = "Original filename, including its supported extension.",
                Required = true,
                Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            });
        }
        return result;
    }

    private static OperationParameterDescriptor Parameter(string name, JsonElement value, bool required, JsonElement root) => new()
    {
        Name = name,
        Title = value.TryGetProperty("description", out var description) ? description.GetString()! : name,
        Required = required,
        Schema = ConvertSchema(root, value.TryGetProperty("schema", out var schema) ? schema : value)
    };

    private static IReadOnlyList<OperationParameterDescriptor> BuildOutputs(JsonElement root, JsonElement operation)
    {
        foreach (var response in operation.GetProperty("responses").EnumerateObject().Where(static item => item.Name[0] == '2').OrderBy(static item => item.Name, StringComparer.Ordinal))
            if (TryContentSchema(response.Value, "content", out var schema))
                return [new OperationParameterDescriptor { Name = "response", Title = "Admin API response", Required = true, Schema = ConvertSchema(root, schema) }];
        return [];
    }

    internal static JsonElement FindOperation(JsonElement root, string operationId)
    {
        foreach (var operation in root.GetProperty("paths").EnumerateObject().SelectMany(static path => path.Value.EnumerateObject()))
            if (operation.Value.ValueKind == JsonValueKind.Object && operation.Value.TryGetProperty("operationId", out var id) && id.GetString() == operationId) return operation.Value;
        throw new InvalidOperationException($"Admin OpenAPI operation '{operationId}' was not found.");
    }

    private static bool TryContentSchema(JsonElement owner, string property, out JsonElement schema)
    {
        schema = default;
        if (!owner.TryGetProperty(property, out var container)) return false;
        var content = property == "content" ? container : container.TryGetProperty("content", out var nested) ? nested : default;
        if (content.ValueKind != JsonValueKind.Object) return false;
        foreach (var media in content.EnumerateObject()) if (media.Value.TryGetProperty("schema", out schema)) return true;
        return false;
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        if (schema.TryGetProperty("allOf", out var allOf) && allOf.GetArrayLength() == 1) return Resolve(root, allOf[0]);
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
            Properties = resolved.TryGetProperty("properties", out var properties) ? properties.EnumerateObject().ToDictionary(static item => item.Name, item => ConvertSchema(root, item.Value), StringComparer.Ordinal) : new Dictionary<string, WorkflowSchemaDefinition>()
        };
    }
}

internal sealed class AdminConnectImportOperationDescriptorProvider : IOperationDescriptorProvider
{
    public string ProviderId => ServicePublishOperation.ProviderId;
    public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IOperationDescriptor>>(AdminConnectImportOperationCatalog.Descriptors);
}

internal sealed class AdminConnectImportApprovalRequestMapper(
    AdminConnectImportOperationCatalog.Definition definition) : IOperationApprovalRequestMapper
{
    public string OperationId => definition.OperationId;

    public OperationGatewayRequest Map(IOperationDescriptor descriptor, OperationRequest request,
        OperationPolicyContext context, PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId)
            throw new ArgumentException($"The mapper only accepts {OperationId} requests.", nameof(request));
        var payload = AdminConnectImportApprovalPayload.From(request, context);
        var serialized = JsonSerializer.Serialize(payload,
            AdminConnectImportApprovalJsonContext.Default.AdminConnectImportApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = new OperationProposalPlan
            {
                Summary = $"Execute {OperationId} through the canonical admin operation runtime.",
                RiskLevel = definition.SideEffect == OperationSideEffectClass.DestroysState
                    ? ProposalRiskLevel.High : ProposalRiskLevel.Medium,
                ExecutionPayload = serialized
            }
        };
    }

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Deserialize(
            request.Plan?.ExecutionPayload ?? request.ExecutionPayload
                ?? throw new InvalidOperationException("The persisted admin operation replay payload is unavailable."),
            AdminConnectImportApprovalJsonContext.Default.AdminConnectImportApprovalPayload)
            ?? throw new InvalidOperationException("The persisted admin operation replay payload is invalid.");
        if (payload.OperationId != OperationId)
            throw new InvalidOperationException("The persisted admin operation replay identity does not match its mapper.");
        return new OperationApprovalReplayMapping
        {
            Request = payload.ToOperationRequest(),
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName
        };
    }
}

internal sealed record AdminConnectImportApprovalPayload
{
    public required string OperationId { get; init; }
    public Dictionary<string, string?> Parameters { get; init; } = new(StringComparer.Ordinal);
    public string? ConnectionId { get; init; }
    public string? ServiceName { get; init; }
    public string[] Fields { get; init; } = [];
    public bool DryRun { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }

    public static AdminConnectImportApprovalPayload From(OperationRequest request, OperationPolicyContext context)
    {
        if (request.Parameters.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Inline passwords cannot be persisted for approval; use secretReference.");
        return new AdminConnectImportApprovalPayload
        {
            OperationId = request.OperationId,
            Parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal),
            ConnectionId = request.ConnectionId,
            ServiceName = request.ServiceName,
            Fields = request.Fields.ToArray(),
            DryRun = request.DryRun,
            TenantId = context.TenantId,
            SchemaName = context.SchemaName
        };
    }

    public OperationRequest ToOperationRequest() => new()
    {
        OperationId = OperationId,
        Parameters = Parameters,
        ConnectionId = ConnectionId,
        ServiceName = ServiceName,
        Fields = Fields,
        DryRun = DryRun
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdminConnectImportApprovalPayload))]
internal sealed partial class AdminConnectImportApprovalJsonContext : JsonSerializerContext;
