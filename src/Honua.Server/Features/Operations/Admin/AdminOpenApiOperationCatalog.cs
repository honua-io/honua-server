// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Operations.Admin;

/// <summary>
/// Resolves the semantic admin-operation manifest against the checked-in Admin API OpenAPI
/// bundle. The bundle remains the only copy of path, method, request, and response schemas.
/// </summary>
internal sealed class AdminOpenApiOperationCatalog
{
    private const string ApiPrefix = "/api/v1/admin";
    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];
    private static readonly string[] PreferredRequestContentTypes =
        [
            "application/json",
            "multipart/form-data",
            "application/x-www-form-urlencoded",
            "application/xml",
            "text/xml",
            "application/octet-stream",
            "text/plain"
        ];
    private static readonly ConditionalWeakTable<JsonObject, Dictionary<string, JsonNode>> ResolvedSchemaCaches = new();
    private static readonly ConcurrentDictionary<string, Lazy<AdminOpenApiCatalogContent>> CatalogCache =
        new(StringComparer.OrdinalIgnoreCase);

    public AdminOpenApiOperationCatalog(IHostEnvironment environment)
        : this(ResolveSpecPath(environment))
    {
    }

    internal AdminOpenApiOperationCatalog(string specPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specPath);
        SpecPath = Path.GetFullPath(specPath);
        var file = new FileInfo(SpecPath);
        var cacheKey = $"{SpecPath}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var content = CatalogCache.GetOrAdd(
            cacheKey,
            _ => new Lazy<AdminOpenApiCatalogContent>(
                () => BuildCatalogContent(SpecPath),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        OpenApiOperationIds = content.OpenApiOperationIds;
        Definitions = content.Definitions;
    }

    public string SpecPath { get; }

    public IReadOnlyList<string> OpenApiOperationIds { get; }

    public IReadOnlyList<AdminOpenApiOperationDefinition> Definitions { get; }

    public AdminOpenApiOperationDefinition GetRequired(string operationId)
        => Definitions.FirstOrDefault(definition =>
                string.Equals(definition.Descriptor.OperationId, operationId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Admin operation '{operationId}' is not registered.");

    private static AdminOpenApiCatalogContent BuildCatalogContent(string specPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(specPath)) as JsonObject
            ?? throw new InvalidOperationException($"Admin OpenAPI document '{specPath}' is not a JSON object.");
        var operationIndex = BuildOperationIndex(root);
        return new AdminOpenApiCatalogContent(
            operationIndex.Keys.Order(StringComparer.Ordinal).ToArray(),
            BuildDefinitions(root, operationIndex));
    }

    private static Dictionary<string, OpenApiOperationSource> BuildOperationIndex(JsonObject root)
    {
        var paths = root["paths"] as JsonObject
            ?? throw new InvalidOperationException("Admin OpenAPI document has no paths object.");
        var byOpenApiId = new Dictionary<string, OpenApiOperationSource>(StringComparer.Ordinal);

        foreach (var (path, pathNode) in paths)
        {
            if (pathNode is not JsonObject pathItem)
            {
                continue;
            }

            var pathParameters = pathItem["parameters"] as JsonArray;
            foreach (var method in HttpMethods)
            {
                if (pathItem[method] is not JsonObject operation
                    || operation["operationId"] is not JsonValue operationIdValue
                    || !operationIdValue.TryGetValue<string>(out var operationId)
                    || string.IsNullOrWhiteSpace(operationId))
                {
                    continue;
                }

                if (!byOpenApiId.TryAdd(
                        operationId,
                        new OpenApiOperationSource(path, method.ToUpperInvariant(), operation, pathParameters)))
                {
                    throw new InvalidOperationException($"Admin OpenAPI operationId '{operationId}' is duplicated.");
                }
            }
        }

        return byOpenApiId;
    }

    private static List<AdminOpenApiOperationDefinition> BuildDefinitions(
        JsonObject root,
        IReadOnlyDictionary<string, OpenApiOperationSource> byOpenApiId)
    {
        var semanticIds = new HashSet<string>(StringComparer.Ordinal);
        var completeManifest = AdminOperationManifest.Complete(byOpenApiId.Keys);
        var definitions = new List<AdminOpenApiOperationDefinition>(completeManifest.Count);
        foreach (var entry in completeManifest)
        {
            ValidateSemanticId(entry.OperationId);
            if (!semanticIds.Add(entry.OperationId))
            {
                throw new InvalidOperationException($"Admin operation id '{entry.OperationId}' is duplicated.");
            }

            if (!byOpenApiId.TryGetValue(entry.OpenApiOperationId, out var source))
            {
                throw new InvalidOperationException(
                    $"Admin manifest operation '{entry.OperationId}' references missing OpenAPI operationId '{entry.OpenApiOperationId}'.");
            }

            definitions.Add(BuildDefinition(root, entry, source));
        }

        return definitions;
    }

    private static AdminOpenApiOperationDefinition BuildDefinition(
        JsonObject root,
        AdminOperationManifestEntry entry,
        OpenApiOperationSource source)
    {
        var parameters = new List<AdminOperationParameterBinding>();
        var inputProperties = new JsonObject();
        var required = new JsonArray();

        AddParameters(root, source.PathParameters, parameters, inputProperties, required);
        AddParameters(root, source.Operation["parameters"] as JsonArray, parameters, inputProperties, required);

        var (requestContentType, requestSchema) = ReadRequestSchema(root, source.Operation);
        var projectedRequestSchema = BuildProjectedRequestSchema(requestContentType, requestSchema);
        if (projectedRequestSchema is not null)
        {
            inputProperties["body"] = projectedRequestSchema;
            var bodyRequired = source.Operation["requestBody"] is JsonObject body
                && body["required"]?.GetValue<bool>() == true;
            if (bodyRequired)
            {
                required.Add("body");
            }
        }

        var inputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = inputProperties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
        var outputSchema = ReadResponseSchema(root, source.Operation) ?? new JsonObject { ["type"] = "object" };

        var isReadOnly = string.Equals(source.Method, "GET", StringComparison.Ordinal)
            || string.Equals(source.Method, "HEAD", StringComparison.Ordinal);
        var destructive = IsDestructive(source.Method, entry.OperationId);
        var isIdempotent = isReadOnly
            || string.Equals(source.Method, "PUT", StringComparison.Ordinal)
            || string.Equals(source.Method, "DELETE", StringComparison.Ordinal)
            || entry.OperationId.Contains(".cancel-", StringComparison.Ordinal)
            || entry.OperationId.EndsWith(".invalidate", StringComparison.Ordinal);
        var supportsDryRun = !string.IsNullOrWhiteSpace(entry.DryRunOperationId);

        var descriptor = new OperationDescriptor
        {
            OperationId = entry.OperationId,
            ProviderId = AdminOperationManifest.ProviderId,
            Title = source.Operation["summary"]?.GetValue<string>() ?? entry.OperationId,
            Description = source.Operation["description"]?.GetValue<string>()
                ?? source.Operation["summary"]?.GetValue<string>()
                ?? entry.OperationId,
            Category = entry.OperationId.Split('.')[1],
            InputSchema = BuildLegacyInput(parameters, projectedRequestSchema, source.Operation),
            OutputSchema =
            [
                new OperationParameterDescriptor
                {
                    Name = "response",
                    Title = "Admin API response",
                    Required = true,
                    Schema = ToWorkflowSchema(outputSchema),
                }
            ],
            InputJsonSchema = ToElement(inputSchema),
            OutputJsonSchema = ToElement(outputSchema),
            ExecutionKind = HasAcceptedResponse(source.Operation)
                ? OperationExecutionKind.Job
                : OperationExecutionKind.Synchronous,
            ApprovalModel = isReadOnly ? OperationApprovalModel.None : OperationApprovalModel.OperatorGate,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = ResolveBlastRadius(entry.OperationId, source.Path, isReadOnly),
                SideEffectClass = isReadOnly
                    ? OperationSideEffectClass.ReadOnly
                    : destructive
                        ? OperationSideEffectClass.DestroysState
                        : entry.OperationId.Contains(".create", StringComparison.Ordinal)
                            || entry.OperationId.Contains(".upload", StringComparison.Ordinal)
                            || entry.OperationId.EndsWith(".publish", StringComparison.Ordinal)
                            ? OperationSideEffectClass.CreatesMetadata
                            : OperationSideEffectClass.MutatesMetadata,
                Determinism = OperationDeterminism.Deterministic,
                SupportsDryRun = supportsDryRun,
                IsIdempotent = isIdempotent,
                DryRunOperationId = entry.DryRunOperationId,
            },
        };

        return new AdminOpenApiOperationDefinition(
            descriptor,
            entry.OpenApiOperationId,
            entry.Lane,
            source.Method,
            ApiPrefix + source.Path,
            requestContentType,
            projectedRequestSchema is null ? null : ToElement(projectedRequestSchema),
            projectedRequestSchema is not null,
            parameters);
    }

    private static JsonNode? BuildProjectedRequestSchema(string? contentType, JsonNode? requestSchema)
    {
        if (requestSchema is null)
        {
            return null;
        }

        var projected = requestSchema.DeepClone();
        if (projected is not JsonObject schema)
        {
            return projected;
        }

        if (string.Equals(contentType, "application/octet-stream", StringComparison.Ordinal)
            && IsBinarySchema(schema))
        {
            schema["contentEncoding"] = "base64";
            schema["description"] = AppendDescription(
                schema["description"]?.GetValue<string>(),
                "Supply the raw request bytes as a base64-encoded string.");
        }
        else if (string.Equals(contentType, "multipart/form-data", StringComparison.Ordinal)
                 && schema["properties"] is JsonObject properties)
        {
            var binaryPropertyNames = properties
                .Where(property => property.Value is JsonObject propertySchema && IsBinarySchema(propertySchema))
                .Select(property => property.Key)
                .ToArray();
            foreach (var propertyName in binaryPropertyNames)
            {
                var propertySchema = (JsonObject)properties[propertyName]!;
                propertySchema["contentEncoding"] = "base64";
                propertySchema["description"] = AppendDescription(
                    propertySchema["description"]?.GetValue<string>(),
                    "Supply this file's bytes as a base64-encoded string.");
                properties[propertyName + "FileName"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = $"File name sent with the '{propertyName}' multipart file.",
                };
            }
        }

        return projected;
    }

    private static bool IsBinarySchema(JsonObject schema)
        => string.Equals(schema["type"]?.GetValue<string>(), "string", StringComparison.Ordinal)
           && string.Equals(schema["format"]?.GetValue<string>(), "binary", StringComparison.Ordinal);

    private static string AppendDescription(string? existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";

    private static void AddParameters(
        JsonObject root,
        JsonArray? parameterNodes,
        List<AdminOperationParameterBinding> bindings,
        JsonObject properties,
        JsonArray required)
    {
        if (parameterNodes is null)
        {
            return;
        }

        foreach (var parameterNode in parameterNodes)
        {
            var parameter = ResolveObject(root, parameterNode)!;
            var wireName = parameter["name"]?.GetValue<string>();
            var location = parameter["in"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(wireName) || string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            var name = NormalizeParameterName(wireName);
            var schema = ResolveSchema(root, parameter["schema"])
                ?? new JsonObject { ["type"] = "string" };
            properties[name] = schema;
            var isRequired = parameter["required"]?.GetValue<bool>() == true;
            if (isRequired)
            {
                required.Add(name);
            }

            bindings.Add(new AdminOperationParameterBinding(name, wireName, location, isRequired));
        }
    }

    private static (string? ContentType, JsonNode? Schema) ReadRequestSchema(JsonObject root, JsonObject operation)
    {
        if (ResolveObject(root, operation["requestBody"], required: false) is not JsonObject requestBody
            || requestBody["content"] is not JsonObject content)
        {
            return (null, null);
        }

        foreach (var contentType in PreferredRequestContentTypes)
        {
            if (content[contentType] is JsonObject media && media["schema"] is not null)
            {
                return (contentType, ResolveSchema(root, media["schema"]));
            }
        }

        var first = content.FirstOrDefault(pair => pair.Value is JsonObject);
        return first.Value is JsonObject firstMedia
            ? (first.Key, ResolveSchema(root, firstMedia["schema"]))
            : (null, null);
    }

    private static JsonNode? ReadResponseSchema(JsonObject root, JsonObject operation)
    {
        if (operation["responses"] is not JsonObject responses)
        {
            return null;
        }

        foreach (var (_, responseNode) in responses
                     .Where(pair => pair.Key.Length == 3 && pair.Key[0] == '2')
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var response = ResolveObject(root, responseNode, required: false);
            if (response?["content"] is not JsonObject content)
            {
                continue;
            }

            foreach (var (_, mediaNode) in content)
            {
                if (mediaNode is JsonObject media && media["schema"] is not null)
                {
                    return ResolveSchema(root, media["schema"]);
                }
            }
        }

        return null;
    }

    private static List<OperationParameterDescriptor> BuildLegacyInput(
        IReadOnlyList<AdminOperationParameterBinding> bindings,
        JsonNode? bodySchema,
        JsonObject operation)
    {
        var result = bindings.Select(binding => new OperationParameterDescriptor
        {
            Name = binding.Name,
            Title = binding.WireName,
            Required = binding.Required,
            Schema = WorkflowText,
        }).ToList();

        if (bodySchema is not null)
        {
            result.Add(new OperationParameterDescriptor
            {
                Name = "body",
                Title = "Request body",
                Required = operation["requestBody"] is JsonObject requestBody
                    && requestBody["required"]?.GetValue<bool>() == true,
                Schema = ToWorkflowSchema(bodySchema),
            });
        }

        return result;
    }

    private static WorkflowSchemaDefinition ToWorkflowSchema(JsonNode schema)
    {
        var type = schema["type"]?.GetValue<string>();
        return new WorkflowSchemaDefinition
        {
            Type = type switch
            {
                "integer" => WorkflowSchemaValueType.WholeNumber,
                "number" => WorkflowSchemaValueType.DecimalNumber,
                "boolean" => WorkflowSchemaValueType.Flag,
                "array" => WorkflowSchemaValueType.List,
                "object" => WorkflowSchemaValueType.Structured,
                _ => WorkflowSchemaValueType.Text,
            },
            Format = schema["format"]?.GetValue<string>(),
            EnumValues = schema["enum"] is JsonArray enumValues
                ? enumValues.Select(value => value?.ToString() ?? string.Empty).ToArray()
                : [],
            Items = schema["items"] is { } items ? ToWorkflowSchema(items) : null,
            Properties = schema["properties"] is JsonObject properties
                ? properties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value is null ? WorkflowText : ToWorkflowSchema(pair.Value),
                    StringComparer.Ordinal)
                : new Dictionary<string, WorkflowSchemaDefinition>(StringComparer.Ordinal),
        };
    }

    private static JsonNode? ResolveSchema(JsonObject root, JsonNode? node)
        => ResolveSchema(
            root,
            node,
            new HashSet<string>(StringComparer.Ordinal),
            ResolvedSchemaCaches.GetOrCreateValue(root));

    private static JsonNode? ResolveSchema(
        JsonObject root,
        JsonNode? node,
        HashSet<string> referenceStack,
        Dictionary<string, JsonNode> resolvedReferences)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj
            && obj["$ref"] is JsonValue referenceValue
            && referenceValue.TryGetValue<string>(out var reference))
        {
            if (!referenceStack.Add(reference))
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = $"Recursive OpenAPI reference {reference}",
                };
            }

            if (resolvedReferences.TryGetValue(reference, out var cached))
            {
                referenceStack.Remove(reference);
                return cached.DeepClone();
            }

            var resolved = ResolveSchema(root, ResolvePointer(root, reference), referenceStack, resolvedReferences);
            referenceStack.Remove(reference);
            if (resolved is not null)
            {
                resolvedReferences[reference] = resolved.DeepClone();
            }

            return resolved;
        }

        if (node is JsonObject sourceObject)
        {
            var clone = new JsonObject();
            foreach (var (key, value) in sourceObject)
            {
                clone[key] = ResolveSchema(root, value, referenceStack, resolvedReferences);
            }

            if (clone["properties"] is JsonObject properties)
            {
                foreach (var (propertyName, propertySchema) in properties)
                {
                    if (propertySchema is not JsonObject propertyObject)
                    {
                        continue;
                    }

                    if (propertyName.Contains("secretReference", StringComparison.OrdinalIgnoreCase))
                    {
                        propertyObject["format"] = "secret_ref";
                    }
                    else if (propertyName.Contains("password", StringComparison.OrdinalIgnoreCase)
                             || propertyName.Contains("clientSecret", StringComparison.OrdinalIgnoreCase))
                    {
                        propertyObject["format"] ??= "password";
                        propertyObject["writeOnly"] = true;
                    }
                }
            }

            return clone;
        }

        if (node is JsonArray sourceArray)
        {
            var clone = new JsonArray();
            foreach (var item in sourceArray)
            {
                clone.Add(ResolveSchema(root, item, referenceStack, resolvedReferences));
            }

            return clone;
        }

        return node.DeepClone();
    }

    private static JsonObject? ResolveObject(JsonObject root, JsonNode? node, bool required = true)
    {
        if (node is JsonObject obj
            && obj["$ref"] is JsonValue referenceValue
            && referenceValue.TryGetValue<string>(out var reference))
        {
            node = ResolvePointer(root, reference);
        }

        if (node is JsonObject resolved)
        {
            return resolved;
        }

        return required
            ? throw new InvalidOperationException("Expected an OpenAPI object.")
            : null;
    }

    private static JsonNode ResolvePointer(JsonObject root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Only local OpenAPI references are supported: '{reference}'.");
        }

        JsonNode current = root;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current[segment]
                ?? throw new InvalidOperationException($"OpenAPI reference '{reference}' was not found.");
        }

        return current;
    }

    private static string NormalizeParameterName(string wireName)
    {
        var buffer = new char[wireName.Length];
        var length = 0;
        var upperNext = false;
        foreach (var value in wireName)
        {
            if (!char.IsAsciiLetterOrDigit(value))
            {
                upperNext = length > 0;
                continue;
            }

            buffer[length++] = upperNext ? char.ToUpperInvariant(value) : value;
            upperNext = false;
        }

        return length == 0 ? wireName : new string(buffer, 0, length);
    }

    private static void ValidateSemanticId(string operationId)
    {
        var segments = operationId.Split('.');
        if (segments.Length != 3
            || !string.Equals(segments[0], "admin", StringComparison.Ordinal)
            || segments.Any(segment => segment.Length == 0
                || segment.Any(value => !(char.IsAsciiLetterOrDigit(value) || value == '-'))))
        {
            throw new InvalidOperationException(
                $"Admin operation id '{operationId}' must follow admin.<area>.<verb> using lowercase kebab-case segments.");
        }
    }

    private static bool IsDestructive(string method, string operationId)
        => string.Equals(method, "DELETE", StringComparison.Ordinal)
           || operationId.Contains(".revoke", StringComparison.Ordinal)
           || operationId.Contains(".cancel", StringComparison.Ordinal)
           || operationId.Contains(".invalidate", StringComparison.Ordinal)
           || operationId.Contains(".rollback", StringComparison.Ordinal)
           || operationId.Contains(".suspend", StringComparison.Ordinal);

    private static OperationBlastRadiusClass ResolveBlastRadius(
        string operationId,
        string path,
        bool isReadOnly)
    {
        if (isReadOnly)
        {
            return OperationBlastRadiusClass.None;
        }

        var semanticArea = operationId.Split('.')[1];
        var pathArea = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var area = string.Equals(semanticArea, "openapi", StringComparison.Ordinal)
            ? pathArea
            : semanticArea;
        return area switch
        {
            "service" => OperationBlastRadiusClass.ServiceScope,
            "server" or "configuration" or "license" or "release" or "coordinated-release" or "tenant"
                => OperationBlastRadiusClass.DeploymentScope,
            _ => OperationBlastRadiusClass.ResourceScope,
        };
    }

    private static bool HasAcceptedResponse(JsonObject operation)
        => operation["responses"] is JsonObject responses && responses.ContainsKey("202");

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string ResolveSpecPath(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var candidates = new[]
        {
            Path.Combine(environment.ContentRootPath, "admin-openapi.json"),
            Path.Combine(AppContext.BaseDirectory, "admin-openapi.json"),
            Path.Combine(environment.ContentRootPath, "docs", "developer", "api-specs", "admin-api.json"),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "The Admin API OpenAPI bundle was not found. Expected admin-openapi.json in the content root or output directory.");
    }

    private static WorkflowSchemaDefinition WorkflowText { get; } = new() { Type = WorkflowSchemaValueType.Text };

    private sealed record OpenApiOperationSource(
        string Path,
        string Method,
        JsonObject Operation,
        JsonArray? PathParameters);

    private sealed record AdminOpenApiCatalogContent(
        IReadOnlyList<string> OpenApiOperationIds,
        IReadOnlyList<AdminOpenApiOperationDefinition> Definitions);
}

internal sealed record AdminOpenApiOperationDefinition(
    OperationDescriptor Descriptor,
    string OpenApiOperationId,
    string Lane,
    string Method,
    string Path,
    string? RequestContentType,
    JsonElement? RequestBodyJsonSchema,
    bool HasRequestBody,
    IReadOnlyList<AdminOperationParameterBinding> Parameters);

internal sealed record AdminOperationParameterBinding(
    string Name,
    string WireName,
    string Location,
    bool Required);

/// <summary>Contributes the OpenAPI-derived 2026.1 admin operation inventory.</summary>
internal sealed class AdminOperationDescriptorProvider(AdminOpenApiOperationCatalog catalog)
    : IOperationDescriptorProvider
{
    public string ProviderId => AdminOperationManifest.ProviderId;

    public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IOperationDescriptor>>(
            catalog.Definitions.Select(definition => (IOperationDescriptor)definition.Descriptor).ToArray());
}
