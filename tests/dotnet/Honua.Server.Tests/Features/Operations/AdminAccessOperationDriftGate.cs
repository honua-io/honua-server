// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// Schema and exclusion-roster drift gate for the WS2 lane-C access family (#3361). The family is
/// defined by the Admin OpenAPI tags, not by a snapshot of today's catalog, so a new access
/// operation, a hand-forked descriptor, an unaudited secret-bearing operation or an unexpected MCP
/// publication each produce a finding without anyone updating an expected list.
/// </summary>
internal static class AdminAccessOperationDriftGate
{
    /// <summary>Admin OpenAPI tags that define the lane-C access family.</summary>
    public static readonly IReadOnlySet<string> AccessTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "API Keys", "Roles", "Users", "Tenants", "OIDC", "OAuth Clients", "OAuth Scopes",
        "Rate Limits", "Field Mask Policies", "RLS Policies",
    };

    /// <summary>One Admin OpenAPI operation as the gate reads it from the contract.</summary>
    public sealed record OpenApiOperation(
        string OperationId,
        string Method,
        string Path,
        IReadOnlyList<string> Tags,
        IReadOnlySet<string> RequiredInputs,
        bool ReturnsPlaintextSecret,
        bool AcceptsWriteOnlySecret)
    {
        public bool IsAccessFamily => Tags.Any(AccessTags.Contains);
    }

    public static JsonElement LoadAdminOpenApi()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<OpenApiOperation> ReadOperations(JsonElement root)
    {
        var operations = new List<OpenApiOperation>();
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject().Where(static method =>
                         method.Value.ValueKind == JsonValueKind.Object &&
                         method.Value.TryGetProperty("operationId", out _)))
            {
                var operation = method.Value;
                var tags = operation.TryGetProperty("tags", out var tagArray)
                    ? tagArray.EnumerateArray().Select(static tag => tag.GetString()!).ToArray()
                    : [];
                var requestSchema = RequestBodySchema(root, operation);
                operations.Add(new OpenApiOperation(
                    operation.GetProperty("operationId").GetString()!,
                    method.Name.ToUpperInvariant(),
                    path.Name,
                    tags,
                    RequiredInputs(root, operation, requestSchema),
                    SuccessSchemas(operation).Any(schema => Walk(root, schema).Any(IsPlaintextSecret)),
                    requestSchema is { } body && Walk(root, body).Any(static node =>
                        node.TryGetProperty("writeOnly", out var writeOnly) && writeOnly.ValueKind == JsonValueKind.True)));
            }
        }

        return operations;
    }

    /// <summary>Returns every drift finding; an empty list means the gate passes.</summary>
    public static IReadOnlyList<string> Evaluate(
        JsonElement root,
        IReadOnlyList<AdminAccessOperationCatalog.Definition> definitions,
        IReadOnlyList<OperationDescriptor> descriptors,
        IReadOnlyList<AdminMcpOperationExclusions.Entry> exclusions,
        IEnumerable<string> publishedToolNames)
    {
        var findings = new List<string>();
        var operations = ReadOperations(root).ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);

        foreach (var operation in operations.Values
                     .Where(static operation => operation.IsAccessFamily)
                     .Where(operation => !definitions.Any(definition => definition.OpenApiOperationId == operation.OperationId)))
        {
            findings.Add($"missing: access operation '{operation.OperationId}' ({operation.Method} {operation.Path}) has no lane-C descriptor or executor.");
        }

        foreach (var group in definitions.GroupBy(static definition => definition.OpenApiOperationId).Where(static group => group.Count() > 1))
            findings.Add($"duplicate: OpenAPI operation '{group.Key}' is projected {group.Count()} times.");

        foreach (var definition in definitions)
            EvaluateDefinition(root, definition, descriptors, operations, findings);

        EvaluateExclusions(definitions, exclusions, operations, findings);
        EvaluatePublication(definitions, exclusions, publishedToolNames, findings);
        return findings;
    }

    private static void EvaluateDefinition(
        JsonElement root,
        AdminAccessOperationCatalog.Definition definition,
        IReadOnlyList<OperationDescriptor> descriptors,
        Dictionary<string, OpenApiOperation> operations,
        List<string> findings)
    {
        if (!operations.TryGetValue(definition.OpenApiOperationId, out var operation))
        {
            findings.Add($"unknown: descriptor '{definition.OperationId}' names OpenAPI operation '{definition.OpenApiOperationId}', which the contract does not define.");
            return;
        }

        if (!operation.IsAccessFamily)
            findings.Add($"out-of-family: descriptor '{definition.OperationId}' projects non-access operation '{operation.OperationId}'.");
        if (!string.Equals(definition.Method.Method, operation.Method, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(definition.Path, operation.Path, StringComparison.Ordinal))
        {
            findings.Add($"hand-forked: descriptor '{definition.OperationId}' binds {definition.Method.Method} {definition.Path}, but the contract declares {operation.Method} {operation.Path}.");
        }

        var matching = descriptors.Where(descriptor => descriptor.OperationId == definition.OperationId).ToArray();
        if (matching.Length != 1)
        {
            findings.Add($"missing: operation '{definition.OperationId}' has {matching.Length} generated descriptors.");
            return;
        }

        var actual = matching[0];
        var generated = AdminOperateOperationCatalog.BuildDescriptor(root, definition);
        if (Serialize(actual.InputSchema) != Serialize(generated.InputSchema))
            findings.Add($"hand-forked: descriptor '{definition.OperationId}' input schema diverges from the Admin OpenAPI contract.");
        if (Serialize(actual.OutputSchema) != Serialize(generated.OutputSchema))
            findings.Add($"hand-forked: descriptor '{definition.OperationId}' output schema diverges from the Admin OpenAPI contract.");
        var required = actual.InputSchema.Where(static input => input.Required).Select(static input => input.Name).ToHashSet(StringComparer.Ordinal);
        if (!required.SetEquals(operation.RequiredInputs))
            findings.Add($"hand-forked: descriptor '{definition.OperationId}' requires [{string.Join(", ", required.Order())}], but the contract requires [{string.Join(", ", operation.RequiredInputs.Order())}].");

        var sideEffect = actual.Policy.SideEffectClass;
        if (operation.Method == "GET" && sideEffect != OperationSideEffectClass.ReadOnly)
            findings.Add($"hand-forked: GET operation '{definition.OperationId}' is not classified read-only.");
        if (operation.Method == "DELETE" && sideEffect != OperationSideEffectClass.DestroysState)
            findings.Add($"hand-forked: DELETE operation '{definition.OperationId}' is not classified destructive.");
        if (sideEffect != OperationSideEffectClass.ReadOnly && actual.ApprovalModel != OperationApprovalModel.OperatorGate)
            findings.Add($"ungated: mutation '{definition.OperationId}' does not route through the operator approval gate.");

        if (!OperationScopeMapping.TryResolve(new OperationRequest { OperationId = definition.OperationId }, out var scope))
        {
            findings.Add($"scope: operation '{definition.OperationId}' has no canonical OAuth scope mapping.");
        }
        else if ((sideEffect == OperationSideEffectClass.ReadOnly) != (scope == OperatorOperation.Read) ||
                 (sideEffect == OperationSideEffectClass.DestroysState && scope != OperatorOperation.Delete))
        {
            findings.Add($"scope: operation '{definition.OperationId}' ({sideEffect}) maps to OAuth operation {scope}.");
        }
    }

    private static void EvaluateExclusions(
        IReadOnlyList<AdminAccessOperationCatalog.Definition> definitions,
        IReadOnlyList<AdminMcpOperationExclusions.Entry> exclusions,
        Dictionary<string, OpenApiOperation> operations,
        List<string> findings)
    {
        foreach (var exclusion in exclusions)
        {
            if (string.IsNullOrWhiteSpace(exclusion.ReasonCode) || string.IsNullOrWhiteSpace(exclusion.Explanation))
                findings.Add($"exclusion-drift: '{exclusion.OperationId}' has no audited reason.");
            if (exclusion.ToolName != PublishedOperationTool.ProjectName(exclusion.OperationId))
                findings.Add($"exclusion-drift: '{exclusion.OperationId}' names tool '{exclusion.ToolName}', not '{PublishedOperationTool.ProjectName(exclusion.OperationId)}'.");
            if (!operations.TryGetValue(exclusion.OpenApiOperationId, out var operation))
            {
                findings.Add($"stale-exclusion: '{exclusion.OperationId}' names OpenAPI operation '{exclusion.OpenApiOperationId}', which the contract does not define.");
                continue;
            }

            if (!operation.IsAccessFamily)
                continue;
            var definition = definitions.FirstOrDefault(item => item.OpenApiOperationId == exclusion.OpenApiOperationId);
            if (definition is null || definition.OperationId != exclusion.OperationId)
                findings.Add($"exclusion-drift: excluded access operation '{exclusion.OpenApiOperationId}' must stay catalogued for REST/CLI as '{exclusion.OperationId}'.");
            if (!operation.ReturnsPlaintextSecret && !operation.AcceptsWriteOnlySecret)
                findings.Add($"over-excluded: access operation '{exclusion.OperationId}' carries no one-time or write-only secret, so it must stay MCP-eligible.");
        }

        foreach (var operation in operations.Values.Where(static operation => operation.IsAccessFamily))
        {
            var exclusion = exclusions.FirstOrDefault(entry => entry.OpenApiOperationId == operation.OperationId);
            if (operation.ReturnsPlaintextSecret && exclusion?.ReasonCode != AdminMcpOperationExclusions.OneTimeSecretReasonCode)
                findings.Add($"unaudited-secret: '{operation.OperationId}' returns plaintext secret material but is not excluded as '{AdminMcpOperationExclusions.OneTimeSecretReasonCode}'.");
            if (operation.AcceptsWriteOnlySecret && !operation.ReturnsPlaintextSecret &&
                exclusion?.ReasonCode != AdminMcpOperationExclusions.SecretInputReasonCode)
            {
                findings.Add($"unaudited-secret: '{operation.OperationId}' accepts write-only secret input but is not excluded as '{AdminMcpOperationExclusions.SecretInputReasonCode}'.");
            }
        }
    }

    private static void EvaluatePublication(
        IReadOnlyList<AdminAccessOperationCatalog.Definition> definitions,
        IReadOnlyList<AdminMcpOperationExclusions.Entry> exclusions,
        IEnumerable<string> publishedToolNames,
        List<string> findings)
    {
        var published = publishedToolNames.ToHashSet(StringComparer.Ordinal);
        var excludedTools = exclusions.Select(static entry => entry.ToolName).ToHashSet(StringComparer.Ordinal);
        var expected = definitions
            .Where(definition => !exclusions.Any(entry => entry.OperationId == definition.OperationId))
            .Select(static definition => PublishedOperationTool.ProjectName(definition.OperationId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in published.Order(StringComparer.Ordinal))
        {
            if (excludedTools.Contains(name))
                findings.Add($"unexpectedly-published: audited exclusion '{name}' is advertised over MCP.");
            else if (!expected.Contains(name))
                findings.Add($"unexpectedly-published: '{name}' is advertised without a lane-C descriptor.");
        }

        foreach (var name in expected.Where(name => !published.Contains(name)).Order(StringComparer.Ordinal))
            findings.Add($"unpublished: eligible access operation '{name}' is absent from the MCP projection.");
    }

    private static HashSet<string> RequiredInputs(JsonElement root, JsonElement operation, JsonElement? requestSchema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (operation.TryGetProperty("parameters", out var parameters))
        {
            foreach (var parameter in parameters.EnumerateArray()
                         .Select(parameter => Resolve(root, parameter))
                         .Where(static parameter => parameter.TryGetProperty("required", out var isRequired) && isRequired.ValueKind == JsonValueKind.True))
            {
                required.Add(parameter.GetProperty("name").GetString()!);
            }
        }

        if (requestSchema is { } body)
        {
            if (!body.TryGetProperty("properties", out _))
            {
                required.Add("body");
            }
            else if (body.TryGetProperty("required", out var names))
            {
                foreach (var name in names.EnumerateArray())
                    required.Add(name.GetString()!);
            }
        }

        return required;
    }

    private static JsonElement? RequestBodySchema(JsonElement root, JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out var requestBody))
            return null;
        requestBody = Resolve(root, requestBody);
        if (!requestBody.TryGetProperty("content", out var content))
            return null;
        foreach (var mediaType in content.EnumerateObject())
        {
            if (mediaType.Value.TryGetProperty("schema", out var schema))
                return Resolve(root, schema);
        }

        return null;
    }

    private static IEnumerable<JsonElement> SuccessSchemas(JsonElement operation)
        => operation.GetProperty("responses").EnumerateObject()
            .Where(static response => response.Name.StartsWith('2') && response.Value.TryGetProperty("content", out _))
            .SelectMany(static response => response.Value.GetProperty("content").EnumerateObject())
            .Where(static mediaType => mediaType.Value.TryGetProperty("schema", out _))
            .Select(static mediaType => mediaType.Value.GetProperty("schema"));

    // A one-time secret is a string leaf the contract documents as plaintext material; object-level
    // descriptions such as "never contains plaintext secret material" do not qualify.
    private static bool IsPlaintextSecret(JsonElement schema)
        => schema.TryGetProperty("type", out var type) && type.GetString() == "string" &&
           schema.TryGetProperty("description", out var description) &&
           description.GetString()!.StartsWith("Plaintext", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<JsonElement> Walk(JsonElement root, JsonElement schema, int depth = 0)
    {
        schema = Resolve(root, schema);
        if (schema.ValueKind != JsonValueKind.Object || depth > 8)
            yield break;
        yield return schema;
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                foreach (var nested in Walk(root, property.Value, depth + 1))
                    yield return nested;
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            foreach (var nested in Walk(root, items, depth + 1))
                yield return nested;
        }

        foreach (var nested in new[] { "allOf", "oneOf", "anyOf" }
                     .Where(combinator => schema.TryGetProperty(combinator, out _))
                     .SelectMany(combinator => schema.GetProperty(combinator).EnumerateArray())
                     .SelectMany(variant => Walk(root, variant, depth + 1)))
        {
            yield return nested;
        }
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        while (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var reference))
        {
            var current = root;
            foreach (var segment in reference.GetString()![2..].Split('/'))
                current = current.GetProperty(segment);
            schema = current;
        }

        return schema;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
}
