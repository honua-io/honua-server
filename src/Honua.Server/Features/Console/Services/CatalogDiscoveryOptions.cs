// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Console.Services;

/// <summary>Explicit workspace mappings for the live discovery projection.</summary>
public sealed class CatalogDiscoveryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Console:CatalogDiscovery";

    /// <summary>Configured mappings; absent configuration publishes no workspaces.</summary>
    public List<CatalogDiscoveryWorkspaceOptions> Workspaces { get; set; } = [];
}

/// <summary>A workspace's explicit tenant and metadata namespace, never inferred from its identifier.</summary>
public sealed class CatalogDiscoveryWorkspaceOptions
{
    /// <summary>Case-insensitive workspace identifier, matching the existing registry contract.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Exact tenant identifier that must match the resolved request tenant.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Exact metadata namespace required on the service, publication, and resource.</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Optional workspace display name.</summary>
    public string? DisplayName { get; set; }
}

internal sealed class CatalogDiscoveryOptionsValidator : IValidateOptions<CatalogDiscoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogDiscoveryOptions options)
    {
        if (options.Workspaces is null)
        {
            return ValidateOptionsResult.Fail("Catalog discovery workspaces must be a list.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in options.Workspaces)
        {
            if (workspace is null || !IsIdentifier(workspace.Id) || !IsIdentifier(workspace.TenantId) ||
                !IsIdentifier(workspace.Namespace) || !ids.Add(workspace.Id) || workspace.DisplayName?.Length > 200)
            {
                return ValidateOptionsResult.Fail(
                    "Catalog discovery workspaces require unique IDs and explicit tenant/namespace identifiers (1-128 letters, digits, '.', '_' or '-'); display names must not exceed 200 characters.");
            }
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
