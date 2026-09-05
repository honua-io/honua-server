// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Lane-B admin operations, projected directly from the shipped Admin OpenAPI contract.
/// Keeping the OpenAPI document as the schema source prevents the agent surface from
/// acquiring a hand-maintained fork of the REST request and response shapes.
/// </summary>
internal static class AdminApiOperationCatalog
{
    internal sealed record Definition(
        string OperationId,
        string Title,
        HttpMethod Method,
        string Path,
        string OpenApiOperationId,
        bool Destructive,
        bool SupportsDryRun = false,
        string? DryRunPath = null,
        IReadOnlySet<string>? QueryParameters = null,
        bool RawBody = false);

    public static IReadOnlyList<Definition> Definitions { get; } =
    [
        new("admin.layer.publish", "Publish layer", HttpMethod.Post, "/connections/{connectionId}/layers", "publishLayer", true, true, "/connections/{connectionId}/tables/validate"),
        new("admin.layer.set-enabled", "Set layer enabled", HttpMethod.Put, "/connections/{connectionId}/layers/{layerId}/enabled", "setLayerEnabled", true, QueryParameters: new HashSet<string>(["serviceName"], StringComparer.Ordinal)),
        new("admin.layer.fields.get", "Get layer fields", HttpMethod.Get, "/metadata/layers/{layerId}/fields", "getAdminLayerFields", false),
        new("admin.layer.fields.set", "Set layer fields", HttpMethod.Put, "/metadata/layers/{layerId}/fields", "updateAdminLayerFields", true),
        new("admin.layer.filter.get", "Get layer filter", HttpMethod.Get, "/metadata/layers/{layerId}/filter", "getAdminLayerFilter", false),
        new("admin.layer.filter.set", "Set layer filter", HttpMethod.Put, "/metadata/layers/{layerId}/filter", "updateAdminLayerFilter", true),
        new("admin.layer.popup-info.get", "Get layer popup info", HttpMethod.Get, "/metadata/layers/{layerId}/popup-info", "getAdminLayerPopupInfo", false),
        new("admin.layer.popup-info.set", "Set layer popup info", HttpMethod.Put, "/metadata/layers/{layerId}/popup-info", "setAdminLayerPopupInfo", true, RawBody: true),
        new("admin.layer.drawing-info.get", "Get layer drawing info", HttpMethod.Get, "/metadata/layers/{layerId}/drawing-info", "getAdminLayerDrawingInfo", false),
        new("admin.layer.drawing-info.set", "Set layer drawing info", HttpMethod.Put, "/metadata/layers/{layerId}/drawing-info", "setAdminLayerDrawingInfo", true, RawBody: true),
        new("admin.layer.style.get", "Get layer style", HttpMethod.Get, "/metadata/layers/{layerId}/style", "getAdminLayerStyle", false),
        new("admin.layer.style.set", "Set layer style", HttpMethod.Put, "/metadata/layers/{layerId}/style", "updateAdminLayerStyle", true),
        new("admin.layer.style.import-sld", "Import layer SLD", HttpMethod.Post, "/metadata/layers/{layerId}/style/import-sld", "importLayerSldStyle", true),
        new("admin.layer.style.export-sld", "Export layer SLD", HttpMethod.Get, "/metadata/layers/{layerId}/style/export-sld", "exportLayerSldStyle", false),
        new("admin.services.list", "List services", HttpMethod.Get, "/services", "listServices", false),
        new("admin.services.settings.get", "Get service settings", HttpMethod.Get, "/services/{serviceName}/settings", "getServiceSettings", false),
        new("admin.services.protocols.set", "Set service protocols", HttpMethod.Put, "/services/{serviceName}/protocols", "updateServiceProtocols", true),
        new("admin.services.access-policy.set", "Set service access policy", HttpMethod.Put, "/services/{serviceName}/access-policy", "updateServiceAccessPolicy", true),
        new("admin.services.timeinfo.set", "Set service time info", HttpMethod.Put, "/services/{serviceName}/timeinfo", "updateServiceTimeInfo", true),
        new("admin.services.layer-metadata.set", "Set service layer metadata", HttpMethod.Put, "/services/{serviceName}/layers/{layerId}/metadata", "updateLayerMetadata", true)
    ];

    public static IReadOnlyList<OperationDescriptor> Descriptors { get; } = BuildDescriptors();

    private static OperationDescriptor[] BuildDescriptors()
    {
        using var stream = typeof(AdminApiOperationCatalog).Assembly.GetManifestResourceStream("Honua.Server.admin-api.json")
            ?? throw new InvalidOperationException("Embedded admin-api.json contract was not found.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return Definitions.Select(definition => BuildDescriptor(root, definition)).ToArray();
    }

    private static OperationDescriptor BuildDescriptor(JsonElement root, Definition definition)
    {
        var operation = FindOperation(root, definition.OpenApiOperationId);
        var inputs = new List<OperationParameterDescriptor>();
        if (operation.TryGetProperty("parameters", out var parameters))
        {
            foreach (var resolvedParameter in parameters.EnumerateArray().Select(parameter => Resolve(root, parameter)))
            {
                var parameterName = resolvedParameter.GetProperty("name").GetString()!;
                if (parameterName == "id" && definition.Path.StartsWith("/connections/", StringComparison.Ordinal))
                {
                    parameterName = "connectionId";
                }

                inputs.Add(new OperationParameterDescriptor
                {
                    Name = parameterName,
                    Title = resolvedParameter.TryGetProperty("description", out var parameterDescription) ? parameterDescription.GetString()! : parameterName,
                    Required = resolvedParameter.TryGetProperty("required", out var required) && required.GetBoolean(),
                    Schema = ConvertSchema(root, resolvedParameter.GetProperty("schema"))
                });
            }
        }

        if (TryGetContentSchema(operation, "requestBody", out var requestSchema))
        {
            var resolved = Resolve(root, requestSchema);
            var requiredNames = resolved.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(static value => value.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
            if (resolved.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    inputs.Add(new OperationParameterDescriptor
                    {
                        Name = property.Name,
                        Title = property.Value.TryGetProperty("description", out var propertyDescription) ? propertyDescription.GetString()! : property.Name,
                        Required = requiredNames.Contains(property.Name),
                        Schema = ConvertSchema(root, property.Value)
                    });
                }
            }
            else
            {
                inputs.Add(new OperationParameterDescriptor { Name = "body", Title = "Request body", Required = true, Schema = ConvertSchema(root, resolved) });
            }
        }

        return new OperationDescriptor
        {
            OperationId = definition.OperationId,
            ProviderId = ServicePublishOperation.ProviderId,
            Title = definition.Title,
            Description = operation.TryGetProperty("description", out var description) ? description.GetString()! : definition.Title,
            Category = "admin",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = definition.Destructive ? OperationApprovalModel.OperatorGate : OperationApprovalModel.None,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = definition.Path.StartsWith("/services", StringComparison.Ordinal) ? OperationBlastRadiusClass.ServiceScope : OperationBlastRadiusClass.ResourceScope,
                SideEffectClass = definition.Destructive ? OperationSideEffectClass.MutatesMetadata : OperationSideEffectClass.ReadOnly,
                Determinism = OperationDeterminism.RuntimeDynamic,
                SupportsDryRun = definition.SupportsDryRun
            },
            InputSchema = inputs,
            OutputSchema = BuildOutputSchema(root, operation)
        };
    }

    internal static JsonElement FindOperation(JsonElement root, string operationId)
    {
        foreach (var method in root.GetProperty("paths").EnumerateObject()
                     .SelectMany(static path => path.Value.EnumerateObject())
                     .Where(static method => method.Value.ValueKind == JsonValueKind.Object)
                     .Where(method => method.Value.TryGetProperty("operationId", out var id) && id.GetString() == operationId))
            return method.Value;
        throw new InvalidOperationException($"Admin OpenAPI operation '{operationId}' was not found.");
    }

    private static IReadOnlyList<OperationParameterDescriptor> BuildOutputSchema(JsonElement root, JsonElement operation)
    {
        foreach (var response in operation.GetProperty("responses").EnumerateObject()
                     .OrderBy(static item => item.Name, StringComparer.Ordinal)
                     .Where(static response => response.Name[0] == '2')
                     .Select(static response => TryGetContentSchema(response.Value, "content", out var schema) ? schema : (JsonElement?)null)
                     .Where(static schema => schema.HasValue))
            return [new OperationParameterDescriptor { Name = "response", Title = "Admin API response", Required = true, Schema = ConvertSchema(root, response.GetValueOrDefault()) }];
        return [];
    }

    private static bool TryGetContentSchema(JsonElement owner, string property, out JsonElement schema)
    {
        schema = default;
        if (!owner.TryGetProperty(property, out var container)) return false;
        var content = property == "content" ? container : container.TryGetProperty("content", out var nested) ? nested : default;
        if (content.ValueKind != JsonValueKind.Object) return false;
        foreach (var candidate in content.EnumerateObject()
                     .Where(static mediaType => mediaType.Value.ValueKind == JsonValueKind.Object)
                     .Select(static mediaType => mediaType.Value.TryGetProperty("schema", out var value) ? value : (JsonElement?)null)
                     .Where(static value => value.HasValue))
        {
            schema = candidate.GetValueOrDefault();
            return true;
        }
        return false;
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        if (schema.TryGetProperty("allOf", out var allOf) && allOf.GetArrayLength() == 1)
            return Resolve(root, allOf[0]);
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
            Type = type switch
            {
                "integer" => WorkflowSchemaValueType.WholeNumber,
                "number" => WorkflowSchemaValueType.DecimalNumber,
                "boolean" => WorkflowSchemaValueType.Flag,
                "array" => WorkflowSchemaValueType.List,
                "object" => WorkflowSchemaValueType.Structured,
                _ => WorkflowSchemaValueType.Text
            },
            Format = resolved.TryGetProperty("format", out var format) ? format.GetString() : null,
            EnumValues = resolved.TryGetProperty("enum", out var values) ? values.EnumerateArray().Select(static value => value.ToString()).ToArray() : [],
            Items = resolved.TryGetProperty("items", out var items) ? ConvertSchema(root, items) : null,
            Properties = resolved.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().ToDictionary(static property => property.Name, property => ConvertSchema(root, property.Value), StringComparer.Ordinal)
                : new Dictionary<string, WorkflowSchemaDefinition>()
        };
    }
}

internal sealed class AdminApiOperationDescriptorProvider : IOperationDescriptorProvider
{
    public string ProviderId => ServicePublishOperation.ProviderId;

    public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IOperationDescriptor>>(AdminApiOperationCatalog.Descriptors);
}

internal sealed class AdminApiOperationApprovalRequestMapper(
    AdminApiOperationCatalog.Definition definition) : IOperationApprovalRequestMapper
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
        var payload = AdminApiOperationApprovalPayload.From(request, context);
        var serialized = JsonSerializer.Serialize(payload,
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload);
        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = AdminOperationReview.Create(descriptor, request, context, definition.Method.Method,
                definition.Path, ProposalRiskLevel.Medium, serialized)
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
            throw new InvalidOperationException("The persisted admin operation replay identity does not match its mapper.");
        return new OperationApprovalReplayMapping
        {
            Request = payload.ToOperationRequest(),
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName
        };
    }
}

internal sealed record AdminApiOperationApprovalPayload
{
    public required string OperationId { get; init; }
    public required Dictionary<string, string?> Parameters { get; init; }
    public string? ConnectionId { get; init; }
    public string? ServiceName { get; init; }
    public string[] Fields { get; init; } = [];
    public bool DryRun { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }

    public static AdminApiOperationApprovalPayload From(OperationRequest request, OperationPolicyContext context) => new()
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
[JsonSerializable(typeof(AdminApiOperationApprovalPayload))]
internal sealed partial class AdminApiOperationApprovalJsonContext : JsonSerializerContext;
