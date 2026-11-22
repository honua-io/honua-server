// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace Honua.Server.Features.Operations;

internal readonly record struct AdminOperationRouteBinding(
    IAdminHttpOperationDefinition Definition,
    string GroupPrefix,
    string? RelativeTemplate = null)
{
    public string RoutePattern => AdminHttpOperationRouteBinding.ResolveValidatedRelativeTemplate(this);
    public string[] Methods => [Definition.Method.Method];
}

internal sealed record AdminHttpOperationBindingViolation(
    string OperationId,
    string Kind,
    string Detail);

internal static partial class AdminHttpOperationRouteBinding
{
    private const string VersionedAdminPrefix = "/api/v{version:apiVersion}/admin";
    private const string ConcreteAdminPrefix = "/api/v1/admin";

    [GeneratedRegex(@"\{([^{}:]+):[^{}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteConstraintRegex();

    public static AdminOperationRouteBinding ForApi(
        string operationId,
        string groupPrefix,
        string? relativeTemplate = null) =>
        ForDefinition(
            AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            groupPrefix,
            relativeTemplate);

    public static AdminOperationRouteBinding ForOperate(
        string operationId,
        string groupPrefix,
        string? relativeTemplate = null) =>
        ForDefinition(
            AdminOperateOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            groupPrefix,
            relativeTemplate);

    public static AdminOperationRouteBinding ForAccess(
        string operationId,
        string groupPrefix,
        string? relativeTemplate = null) =>
        ForDefinition(
            AdminAccessOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            groupPrefix,
            relativeTemplate);

    private static AdminOperationRouteBinding ForDefinition(
        IAdminHttpOperationDefinition definition,
        string groupPrefix,
        string? relativeTemplate)
    {
        var binding = new AdminOperationRouteBinding(definition, groupPrefix, relativeTemplate);
        _ = ResolveValidatedRelativeTemplate(binding);
        return binding;
    }

    public static string ResolveValidatedRelativeTemplate(AdminOperationRouteBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding.Definition);
        var relative = ResolveRelativeTemplate(binding);
        var normalized = NormalizeRoutePath(CombinePrefixAndRelative(binding.GroupPrefix, relative));
        if (!string.Equals(normalized, binding.Definition.Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Admin operation '{binding.Definition.OperationId}' route '{normalized}' does not match catalog path '{binding.Definition.Path}'.");
        }

        return relative;
    }

    public static string NormalizeRoutePath(string rawRouteTemplate)
    {
        ArgumentNullException.ThrowIfNull(rawRouteTemplate);
        var trimmed = rawRouteTemplate.Trim();
        if (trimmed.StartsWith(VersionedAdminPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[VersionedAdminPrefix.Length..];
        }
        else if (trimmed.StartsWith(ConcreteAdminPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[ConcreteAdminPrefix.Length..];
        }

        trimmed = RouteConstraintRegex().Replace(trimmed, "{$1}");
        trimmed = NormalizeConnectionParameter(trimmed);
        if (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    public static IReadOnlyList<AdminHttpOperationBindingViolation> Validate(
        IEnumerable<Endpoint> endpoints,
        IEnumerable<IAdminHttpOperationDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(definitions);

        var bound = endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => (Endpoint: endpoint, Definition: endpoint.Metadata.GetMetadata<IAdminHttpOperationDefinition>()))
            .Where(item => item.Definition is not null)
            .GroupBy(item => item.Definition!.OperationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Endpoint).ToArray(), StringComparer.Ordinal);

        var violations = new List<AdminHttpOperationBindingViolation>();
        foreach (var definition in definitions)
        {
            ValidateDefinition(definition, bound, violations);
        }

        return violations;
    }

    private static void ValidateDefinition(
        IAdminHttpOperationDefinition definition,
        Dictionary<string, RouteEndpoint[]> bound,
        List<AdminHttpOperationBindingViolation> violations)
    {
        if (!bound.TryGetValue(definition.OperationId, out var matches) || matches.Length == 0)
        {
            violations.Add(new(definition.OperationId, "MissingEndpoint",
                $"No HTTP endpoint carries operation definition '{definition.OperationId}' ({definition.Method.Method} {definition.Path})."));
            return;
        }

        if (matches.Length > 1)
        {
            violations.Add(new(definition.OperationId, "DuplicateEndpoint",
                $"Expected 1 bound endpoint for '{definition.OperationId}', found {matches.Length}."));
            return;
        }

        var endpoint = matches[0];
        ValidateEndpointContract(definition, endpoint, violations);
    }

    private static void ValidateEndpointContract(
        IAdminHttpOperationDefinition definition,
        RouteEndpoint endpoint,
        List<AdminHttpOperationBindingViolation> violations)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
        if (methods.Count != 1 || !string.Equals(methods[0], definition.Method.Method, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(new(definition.OperationId, "MethodMismatch",
                $"Expected method '{definition.Method.Method}', found '[{string.Join(", ", methods)}]'."));
        }

        var normalizedPath = NormalizeRoutePath(endpoint.RoutePattern.RawText ?? string.Empty);
        if (!string.Equals(normalizedPath, definition.Path, StringComparison.Ordinal))
        {
            violations.Add(new(definition.OperationId, "PathMismatch",
                $"Expected path '{definition.Path}', found '{normalizedPath}'."));
        }

        var endpointName = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (!string.Equals(endpointName, definition.OpenApiOperationId, StringComparison.Ordinal))
        {
            violations.Add(new(definition.OperationId, "EndpointNameMismatch",
                $"Expected endpoint name '{definition.OpenApiOperationId}', found '{endpointName ?? "<null>"}'."));
        }
    }

    private static string ResolveRelativeTemplate(AdminOperationRouteBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.RelativeTemplate))
        {
            return binding.RelativeTemplate;
        }

        var normalizedPrefix = NormalizeRoutePath(binding.GroupPrefix);
        if (string.Equals(binding.Definition.Path, normalizedPrefix, StringComparison.Ordinal))
        {
            return "/";
        }

        if (normalizedPrefix == "/")
        {
            return binding.Definition.Path;
        }

        if (binding.Definition.Path.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal))
        {
            return binding.Definition.Path[normalizedPrefix.Length..];
        }

        throw new InvalidOperationException(
            $"Admin operation '{binding.Definition.OperationId}' path '{binding.Definition.Path}' does not start with group prefix '{normalizedPrefix}'.");
    }

    private static string CombinePrefixAndRelative(string prefix, string relative)
    {
        var cleanPrefix = prefix.TrimEnd('/');
        var cleanRelative = relative.StartsWith('/') ? relative : "/" + relative;
        if (string.IsNullOrEmpty(cleanPrefix))
        {
            return cleanRelative;
        }

        return cleanRelative == "/" ? cleanPrefix : cleanPrefix + cleanRelative;
    }

    private static string NormalizeConnectionParameter(string path)
    {
        const string legacySegment = "/connections/{id}";
        const string canonicalSegment = "/connections/{connectionId}";
        if (path.Equals(legacySegment, StringComparison.Ordinal))
        {
            return canonicalSegment;
        }

        return path.StartsWith(legacySegment + "/", StringComparison.Ordinal)
            ? canonicalSegment + path[legacySegment.Length..]
            : path;
    }
}
