// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Console.Services;

public sealed class CatalogDiscoveryOptions
{
    public const string SectionName = "Console:CatalogDiscovery";

    public List<CatalogDiscoveryWorkspaceOptions> Workspaces { get; set; } = [];
}

// Tenant and metadata namespace are explicit and never inferred from the workspace identifier.
public sealed class CatalogDiscoveryWorkspaceOptions
{
    public string Id { get; set; } = string.Empty;

    // The tenant identifier must match the resolved request tenant.
    public string TenantId { get; set; } = string.Empty;

    // The metadata namespace is required on the service, publication, and resource.
    public string Namespace { get; set; } = string.Empty;

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
