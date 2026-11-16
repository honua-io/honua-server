// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

public sealed class AppPackageDraftFactory
{
    public const string AppPackageFormat = "honua_app_package.v1";

    public const string IdentifierPrefix = "app";

    public const string DefaultTargetSdk = "honua-sdk-js";

    private readonly IDraftIdentifierGenerator _identifiers;
    private readonly TimeProvider _timeProvider;

    public AppPackageDraftFactory(IDraftIdentifierGenerator identifiers, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _identifiers = identifiers;
        _timeProvider = timeProvider;
    }

    public AppPackageDraftResult CreateDraft(AppPackageDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<PackageDraftFinding>();
        var warnings = new List<PackageDraftFinding>();

        var templateId = NormalizeOptional(request.TemplateId, "templateId", "/templateId", errors);
        var mapPackageId = NormalizeOptional(request.MapPackageId, "mapPackageId", "/mapPackageId", errors);
        var boundArtifacts = BuildBoundArtifacts(request.BoundArtifactIds, errors);
        var runtimeConfig = ValidateRuntimeConfig(request.RuntimeConfig, errors);

        var targetSdk = string.IsNullOrWhiteSpace(request.TargetSdk)
            ? DefaultTargetSdk
            : request.TargetSdk.Trim();

        if (mapPackageId is null)
        {
            // Retained knowledge §2: an unresolved binding is deferred to publish,
            // not blocking — an app draft with no map yet is a legitimate draft.
            warnings.Add(Finding(
                "bindingNotResolved",
                "/mapPackageId",
                "No map package is bound to this application draft; bind one before publish."));
        }

        if (errors.Count > 0)
        {
            return new AppPackageDraftResult { Errors = errors, Warnings = warnings };
        }

        var package = new AppPackage
        {
            AppPackageId = _identifiers.NewIdentifier(IdentifierPrefix),
            TargetSdk = targetSdk,

            // Server-owned discriminators (retained knowledge §5): never caller supplied.
            Format = AppPackageFormat,
            Status = PackageStatus.Draft,

            TemplateId = templateId,
            MapPackageId = mapPackageId,
            BoundArtifacts = boundArtifacts,
            RuntimeConfig = runtimeConfig,

            // Retained knowledge §5: sharing is closed by default and this entry
            // point offers no way to widen it.
            SharePolicy = AppSharePolicy.Closed,

            CreatedAt = _timeProvider.GetUtcNow()
        };

        return new AppPackageDraftResult { Package = package, Warnings = warnings };
    }

    private static List<string> BuildBoundArtifacts(
        IReadOnlyList<string> artifactIds,
        List<PackageDraftFinding> errors)
    {
        if (artifactIds.Count == 0)
        {
            return [];
        }

        var bound = new List<string>(artifactIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < artifactIds.Count; index++)
        {
            var path = $"/boundArtifactIds/{index}";
            var raw = artifactIds[index];
            if (string.IsNullOrWhiteSpace(raw))
            {
                errors.Add(Finding("boundArtifactIdInvalid", path, "A bound artifact identifier must be non-empty."));
                continue;
            }

            var artifactId = raw.Trim();

            if (!seen.Add(artifactId))
            {
                errors.Add(Finding(
                    "boundArtifactIdDuplicate",
                    path,
                    $"Artifact '{artifactId}' is bound more than once."));
                continue;
            }

            bound.Add(artifactId);
        }

        return bound;
    }

    private static JsonElement? ValidateRuntimeConfig(JsonElement? runtimeConfig, List<PackageDraftFinding> errors)
    {
        if (runtimeConfig is not { } config || config.ValueKind == JsonValueKind.Null || config.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (config.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Finding(
                "runtimeConfigInvalid",
                "/runtimeConfig",
                "runtimeConfig must be a JSON object of configuration values."));
            return null;
        }

        return config;
    }

    private static string? NormalizeOptional(
        string? value,
        string name,
        string path,
        List<PackageDraftFinding> errors)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != 0)
        {
            return trimmed;
        }

        errors.Add(Finding(name + "Invalid", path, $"{name} was supplied but is blank; omit it instead."));
        return null;
    }

    private static PackageDraftFinding Finding(string code, string path, string message) =>
        new() { Code = code, Path = path, Message = message };
}
