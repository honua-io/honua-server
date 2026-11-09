// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Catalog;

public sealed class CatalogMembershipPolicy(MetadataV2CatalogConfiguration configuration)
{
    public static bool AllowsAudience(MetadataV2CatalogAudience audience, ClaimsPrincipal principal)
        => audience.Kind switch
        {
            "public" => true,
            "signed-in" => principal.Identity?.IsAuthenticated == true,
            "roles" => principal.Identity?.IsAuthenticated == true && audience.RoleIds.Where(role => !string.IsNullOrWhiteSpace(role)).Any(principal.IsInRole),
            _ => false
        };

    private readonly string _configurationRevision = ConfigurationRevision(configuration);

    public IReadOnlyList<CatalogItem> Apply(IReadOnlyList<CatalogItem> items)
    {
        if (configuration.Memberships.Any(membership => membership?.Item is null)) return [];
        var memberships = configuration.Memberships.ToLookup(membership => (membership.Item.Kind, membership.Item.Id));
        var listed = new List<CatalogItem>();
        foreach (var item in items.DistinctBy(item => (item.Kind, item.Id)))
        {
            var entries = memberships[(item.Kind, item.Id)].ToArray();
            if (entries.Length > 1 || !item.CanAppearInCatalogs ||
                !configuration.Scope.Kinds.Contains(item.Kind, StringComparer.Ordinal)) continue;
            var membership = entries.SingleOrDefault();
            if (membership?.HiddenHere == true || membership?.Inclusion == "exclude" ||
                !AllowsMembership(item, membership)) continue;
            listed.Add(ApplyAppearance(item, membership));
        }
        return listed;
    }

    public static string ConfigurationRevision(MetadataV2CatalogConfiguration configuration)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration with { Memberships = [] },
            MetadataV2JsonContext.Default.MetadataV2CatalogConfiguration);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private bool AllowsMembership(CatalogItem item, MetadataV2CatalogMembership? membership)
    {
        if (membership is not null && membership.Inclusion is not ("scope" or "include")) return false;
        if (membership?.Inclusion != "include" && !MatchesScope(configuration.Scope, item)) return false;
        return configuration.DefaultListing switch
        {
            "automatic" => true,
            "manual" => membership?.Inclusion == "include",
            "after-review" => membership?.ApprovedItemRevision == item.Revision &&
                membership.ApprovedConfigurationRevision == _configurationRevision,
            _ => false
        };
    }

    private static bool MatchesScope(MetadataV2CatalogScope scope, CatalogItem item)
        => scope.FolderIds.Count == 0 && scope.Tags.Count == 0 ||
           item.Folder is not null && scope.FolderIds.Contains(item.Folder.Id, StringComparer.Ordinal) ||
           item.Tags.Intersect(scope.Tags, StringComparer.OrdinalIgnoreCase).Any();

    private static CatalogItem ApplyAppearance(CatalogItem item, MetadataV2CatalogMembership? membership)
        => item with
        {
            Title = Override(membership?.Appearance?.Title, item.Title) ?? item.Title,
            Summary = Override(membership?.Appearance?.Summary, item.Summary),
            Categories = membership?.CategoryIds.Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.Ordinal).ToArray() ?? []
        };

    private static string? Override(MetadataV2CatalogTextOverride? appearance, string? inherited)
        => appearance?.Mode switch { "set" => appearance.Value, "clear" => null, _ => inherited };
}
