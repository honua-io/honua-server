// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2EditCapabilities
{
    public const string Create = "Create";

    public const string Update = "Update";

    public const string Delete = "Delete";

    public const string Editing = "Editing";

    private static readonly string[] _queryOnly = ["Query"];

    public static IReadOnlyList<string>? ResolveDeclared(MetadataV2Service service, MetadataV2Publication? publication)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (publication?.Capabilities is { Count: > 0 } publicationCapabilities)
        {
            return publicationCapabilities;
        }

        if (service.Options.TryGetValue("capabilities", out var element) &&
            element.ValueKind == JsonValueKind.Array)
        {
            var declared = element.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToArray();
            if (declared.Length > 0)
            {
                return declared;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> Resolve(MetadataV2Service service, MetadataV2Publication? publication)
        => ResolveDeclared(service, publication) ?? _queryOnly;

    public static bool Supports(MetadataV2Service service, MetadataV2Publication? publication, string operation)
        => Contains(Resolve(service, publication), operation);

    // Silence is not a denial, because a missing capability list would otherwise turn editing off.
    public static bool? SupportsDeclared(MetadataV2Service service, MetadataV2Publication? publication, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var declared = ResolveDeclared(service, publication);
        return declared is null ? null : Contains(declared, operation);
    }

    private static bool Contains(IReadOnlyList<string> capabilities, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        for (var index = 0; index < capabilities.Count; index++)
        {
            var capability = capabilities[index];
            if (capability.Equals(operation, StringComparison.OrdinalIgnoreCase) ||
                capability.Equals(Editing, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
