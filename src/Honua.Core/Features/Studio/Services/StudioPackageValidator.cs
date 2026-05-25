// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Envelope and baseline family validator for Studio package lifecycle operations.
/// </summary>
public sealed class StudioPackageValidator : IStudioPackageValidator
{
    private readonly IStudioPackageFamilyRegistry _registry;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new Studio package validator.
    /// </summary>
    public StudioPackageValidator(IStudioPackageFamilyRegistry registry, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public StudioValidationSummary Validate(StudioPackageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var diagnostics = new List<StudioValidationDiagnostic>();
        var unsupportedCapabilities = new List<string>();

        if (!StudioPackageEnumHelpers.IsDefined(envelope.Family))
        {
            diagnostics.Add(Error("studio.family.unknown", "/family", "Package family is not supported."));
        }

        var descriptor = _registry.GetDescriptor(envelope.Family);
        if (descriptor is null)
        {
            diagnostics.Add(Error("studio.family.unregistered", "/family", "Package family is not registered."));
        }
        else
        {
            if (descriptor.SupportLevel == StudioPackageSupportLevel.Unsupported)
            {
                unsupportedCapabilities.Add(envelope.Family.ToString());
                diagnostics.Add(Blocker("studio.family.unsupported", "/family", "Package family is not available in this deployment."));
            }

            if (!string.Equals(envelope.SchemaVersion, descriptor.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "studio.schema-version.unsupported",
                    "/schemaVersion",
                    $"Schema version must be '{descriptor.CurrentSchemaVersion}'."));
            }

            var serialized = JsonSerializer.SerializeToUtf8Bytes(envelope, StudioJsonContext.Default.StudioPackageEnvelope);
            if (serialized.Length > descriptor.MaxPackageBytes)
            {
                diagnostics.Add(Blocker(
                    "studio.package.too-large",
                    "/",
                    $"Serialized package exceeds the {descriptor.MaxPackageBytes} byte limit."));
            }
        }

        if (string.IsNullOrWhiteSpace(envelope.SchemaVersion))
        {
            diagnostics.Add(Error("studio.schema-version.required", "/schemaVersion", "schemaVersion is required."));
        }

        if (envelope.Body is null || envelope.Body.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            diagnostics.Add(Error("studio.body.required", "/body", "body is required."));
        }
        else if (envelope.Body.Value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(Error("studio.body.object", "/body", "body must be a JSON object."));
        }

        ValidateBindings(envelope, diagnostics);
        ValidateDependencies(envelope, diagnostics);
        ValidateProvenance(envelope, diagnostics);
        ValidatePublicationIntent(envelope.PublicationIntent, diagnostics);
        ValidateFamilyBody(envelope, diagnostics);

        var status = ResolveStatus(diagnostics);
        return new StudioValidationSummary
        {
            Status = status,
            Diagnostics = diagnostics,
            UnsupportedCapabilities = unsupportedCapabilities,
            GeneratedAt = _timeProvider.GetUtcNow(),
        };
    }

    private static void ValidateBindings(StudioPackageEnvelope envelope, List<StudioValidationDiagnostic> diagnostics)
    {
        if (envelope.Bindings is null)
        {
            diagnostics.Add(Error("studio.bindings.array", "/bindings", "bindings must be an array."));
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < envelope.Bindings.Count; i++)
        {
            var binding = envelope.Bindings[i];
            var path = $"/bindings/{i}";
            if (binding is null)
            {
                diagnostics.Add(Error("studio.binding.required", path, "binding must not be null."));
                continue;
            }

            Required(binding.Key, $"{path}/key", "studio.binding.key.required", "binding key is required.", diagnostics);
            Required(binding.Kind, $"{path}/kind", "studio.binding.kind.required", "binding kind is required.", diagnostics);
            Required(binding.Ref, $"{path}/ref", "studio.binding.ref.required", "binding ref is required.", diagnostics);
            if (!string.IsNullOrWhiteSpace(binding.Key) && !keys.Add(binding.Key))
            {
                diagnostics.Add(Error("studio.binding.key.duplicate", $"{path}/key", "binding key must be unique."));
            }

            if (binding.Srid is <= 0)
            {
                diagnostics.Add(Error("studio.binding.srid.invalid", $"{path}/srid", "srid must be a positive integer."));
            }

            if (!string.IsNullOrWhiteSpace(binding.Crs) && !IsValidCrs(binding.Crs))
            {
                diagnostics.Add(Error("studio.binding.crs.invalid", $"{path}/crs", "crs must be an EPSG identifier or CRS URI."));
            }

            if (binding.RequiredPermissions is null)
            {
                diagnostics.Add(Error("studio.binding.permissions.array", $"{path}/requiredPermissions", "requiredPermissions must be an array."));
            }
        }
    }

    private static void ValidateDependencies(StudioPackageEnvelope envelope, List<StudioValidationDiagnostic> diagnostics)
    {
        if (envelope.Dependencies is null)
        {
            diagnostics.Add(Error("studio.dependencies.array", "/dependencies", "dependencies must be an array."));
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < envelope.Dependencies.Count; i++)
        {
            var dependency = envelope.Dependencies[i];
            var path = $"/dependencies/{i}";
            if (dependency is null)
            {
                diagnostics.Add(Error("studio.dependency.required", path, "dependency must not be null."));
                continue;
            }

            Required(dependency.Kind, $"{path}/kind", "studio.dependency.kind.required", "dependency kind is required.", diagnostics);
            Required(dependency.Ref, $"{path}/ref", "studio.dependency.ref.required", "dependency ref is required.", diagnostics);
            var key = string.Concat(dependency.Kind, "|", dependency.Ref, "|", dependency.VersionId);
            if (!string.IsNullOrWhiteSpace(dependency.Kind) &&
                !string.IsNullOrWhiteSpace(dependency.Ref) &&
                !keys.Add(key))
            {
                diagnostics.Add(Error("studio.dependency.duplicate", path, "dependency kind/ref/version tuple must be unique."));
            }
        }
    }

    private static void ValidateProvenance(StudioPackageEnvelope envelope, List<StudioValidationDiagnostic> diagnostics)
    {
        if (envelope.Provenance is null)
        {
            diagnostics.Add(Error("studio.provenance.array", "/provenance", "provenance must be an array."));
            return;
        }

        for (var i = 0; i < envelope.Provenance.Count; i++)
        {
            var provenance = envelope.Provenance[i];
            var path = $"/provenance/{i}";
            if (provenance is null)
            {
                diagnostics.Add(Error("studio.provenance.required", path, "provenance reference must not be null."));
                continue;
            }

            Required(provenance.Kind, $"{path}/kind", "studio.provenance.kind.required", "provenance kind is required.", diagnostics);
            Required(provenance.Ref, $"{path}/ref", "studio.provenance.ref.required", "provenance ref is required.", diagnostics);
            Required(provenance.Rel, $"{path}/rel", "studio.provenance.rel.required", "provenance rel is required.", diagnostics);
        }
    }

    private static void ValidatePublicationIntent(StudioPublicationIntent? intent, List<StudioValidationDiagnostic> diagnostics)
    {
        if (intent is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(intent.Route) && intent.Route[0] != '/')
        {
            diagnostics.Add(Error("studio.publication.route.invalid", "/publicationIntent/route", "route must start with '/'."));
        }

        if (!string.IsNullOrWhiteSpace(intent.Visibility))
        {
            var visibility = intent.Visibility.Trim();
            if (!StringEqualsAny(visibility, "personal", "team", "organization", "public"))
            {
                diagnostics.Add(Error(
                    "studio.publication.visibility.invalid",
                    "/publicationIntent/visibility",
                    "visibility must be personal, team, organization, or public."));
            }
        }
    }

    private static void ValidateFamilyBody(StudioPackageEnvelope envelope, List<StudioValidationDiagnostic> diagnostics)
    {
        if (envelope.Body is null || envelope.Body.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        try
        {
            if (envelope.Family == StudioPackageFamily.Map)
            {
                var map = envelope.Body.Value.Deserialize(PackagingJsonContext.Default.MapPackage);
                if (map is null)
                {
                    diagnostics.Add(Error("studio.map.body.invalid", "/body", "map body must match honua_map_package.v1."));
                    return;
                }

                if (!string.Equals(map.Format, "honua_map_package.v1", StringComparison.Ordinal))
                {
                    diagnostics.Add(Error("studio.map.format.invalid", "/body/format", "map body format must be honua_map_package.v1."));
                }

                if (map.InitialView is not null)
                {
                    ValidateInitialView(map.InitialView, "/body/initialView", diagnostics);
                }
            }
            else if (envelope.Family == StudioPackageFamily.App)
            {
                var app = envelope.Body.Value.Deserialize(PackagingJsonContext.Default.AppPackage);
                if (app is null)
                {
                    diagnostics.Add(Error("studio.app.body.invalid", "/body", "app body must match honua_app_package.v1."));
                    return;
                }

                if (!string.Equals(app.Format, "honua_app_package.v1", StringComparison.Ordinal))
                {
                    diagnostics.Add(Error("studio.app.format.invalid", "/body/format", "app body format must be honua_app_package.v1."));
                }
            }
        }
        catch (JsonException)
        {
            diagnostics.Add(Error("studio.body.invalid", "/body", "package body does not match the declared family format."));
        }
        catch (InvalidOperationException)
        {
            diagnostics.Add(Error("studio.body.invalid", "/body", "package body does not match the declared family format."));
        }
    }

    private static void ValidateInitialView(MapInitialView initialView, string path, List<StudioValidationDiagnostic> diagnostics)
    {
        if (initialView.Bbox is null || initialView.Bbox.Length != 4)
        {
            diagnostics.Add(Error("studio.map.initial-view.bbox.invalid", $"{path}/bbox", "bbox must contain [minX,minY,maxX,maxY]."));
            return;
        }

        if (initialView.Bbox[0] > initialView.Bbox[2] || initialView.Bbox[1] > initialView.Bbox[3])
        {
            diagnostics.Add(Error("studio.map.initial-view.bbox.order", $"{path}/bbox", "bbox min values must be less than or equal to max values."));
        }

        if (string.IsNullOrWhiteSpace(initialView.Crs) || !IsValidCrs(initialView.Crs))
        {
            diagnostics.Add(Error("studio.map.initial-view.crs.invalid", $"{path}/crs", "initial view CRS must be an EPSG identifier or CRS URI."));
        }
    }

    private static StudioPackageValidationStatus ResolveStatus(List<StudioValidationDiagnostic> diagnostics)
    {
        var hasBlocker = diagnostics.Any(static d => d.Severity == StudioPackageDiagnosticSeverity.Blocker);
        var hasError = diagnostics.Any(static d => d.Severity == StudioPackageDiagnosticSeverity.Error);
        if (hasBlocker || hasError)
        {
            return StudioPackageValidationStatus.Invalid;
        }

        return diagnostics.Any(static d => d.Severity == StudioPackageDiagnosticSeverity.Warning)
            ? StudioPackageValidationStatus.Warning
            : StudioPackageValidationStatus.Valid;
    }

    private static void Required(
        string? value,
        string path,
        string code,
        string message,
        List<StudioValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(Error(code, path, message));
        }
    }

    private static bool IsValidCrs(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var srid = trimmed[5..];
            return srid.Length > 0 && srid.All(static c => c is >= '0' and <= '9');
        }

        return trimmed.StartsWith("http://www.opengis.net/def/crs/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("urn:ogc:def:crs:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StringEqualsAny(string value, string a, string b, string c, string d)
        => string.Equals(value, a, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, b, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, c, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, d, StringComparison.OrdinalIgnoreCase);

    private static StudioValidationDiagnostic Error(string code, string path, string message) => new()
    {
        Code = code,
        Severity = StudioPackageDiagnosticSeverity.Error,
        Path = path,
        Message = message,
    };

    private static StudioValidationDiagnostic Blocker(string code, string path, string message) => new()
    {
        Code = code,
        Severity = StudioPackageDiagnosticSeverity.Blocker,
        Path = path,
        Message = message,
    };
}

/// <summary>
/// Non-reflection enum helpers for Studio package contracts.
/// </summary>
public static class StudioPackageEnumHelpers
{
    /// <summary>Returns true when the family enum value is defined by the contract.</summary>
    public static bool IsDefined(StudioPackageFamily family)
        => family is StudioPackageFamily.Query
            or StudioPackageFamily.Analysis
            or StudioPackageFamily.Map
            or StudioPackageFamily.Dashboard
            or StudioPackageFamily.Report
            or StudioPackageFamily.Form
            or StudioPackageFamily.App
            or StudioPackageFamily.Workflow
            or StudioPackageFamily.Geoprocessing
            or StudioPackageFamily.Etl;

    /// <summary>Returns true when the rollback pointer enum value is defined by the contract.</summary>
    public static bool IsDefined(StudioRollbackPointer target)
        => target is StudioRollbackPointer.Current
            or StudioRollbackPointer.Published
            or StudioRollbackPointer.Both;

    /// <summary>Returns true when the publication request status enum value is defined by the contract.</summary>
    public static bool IsDefined(StudioPublicationRequestStatus status)
        => status is StudioPublicationRequestStatus.Accepted
            or StudioPublicationRequestStatus.Pending
            or StudioPublicationRequestStatus.Rejected;
}
