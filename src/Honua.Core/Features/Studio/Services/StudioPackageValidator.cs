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

            if (string.IsNullOrWhiteSpace(envelope.Format))
            {
                diagnostics.Add(Error(
                    "studio.format.required",
                    "/format",
                    $"format must be '{descriptor.Format}'."));
            }
            else if (!string.Equals(envelope.Format, descriptor.Format, StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "studio.format.unsupported",
                    "/format",
                    $"format must be '{descriptor.Format}'."));
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

    /// <inheritdoc />
    public StudioValidationSummary ValidatePublicationIntent(StudioPublicationIntent? intent)
    {
        var diagnostics = new List<StudioValidationDiagnostic>();
        ValidatePublicationIntent(intent, diagnostics, "/publicationIntent");
        return new StudioValidationSummary
        {
            Status = ResolveStatus(diagnostics),
            Diagnostics = diagnostics,
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

    private static void ValidatePublicationIntent(
        StudioPublicationIntent? intent,
        List<StudioValidationDiagnostic> diagnostics,
        string pathPrefix = "/publicationIntent")
    {
        if (intent is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(intent.Route) && intent.Route[0] != '/')
        {
            diagnostics.Add(Error("studio.publication.route.invalid", $"{pathPrefix}/route", "route must start with '/'."));
        }

        if (!string.IsNullOrWhiteSpace(intent.Visibility))
        {
            var visibility = intent.Visibility.Trim();
            if (!StringEqualsAny(visibility, "personal", "team", "organization", "public"))
            {
                diagnostics.Add(Error(
                    "studio.publication.visibility.invalid",
                    $"{pathPrefix}/visibility",
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

        if (StudioCompositionBodyEditor.CompositionEligibleFamilies.Contains(envelope.Family))
        {
            ValidateCompositionInteractionsAndLayout(envelope, diagnostics);
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

    /// <summary>
    /// Gates the standard-owned <c>interactions</c>/<c>layout</c> blocks of a map/app
    /// composition body (geospatial-mcp ADR-0030). Both blocks are OPTIONAL: a body that
    /// declares neither produces no diagnostics. When present they must satisfy the
    /// closed event/verb sets, the component-reference grammar, in-document reference
    /// resolution, id uniqueness, the per-<c>(on.ref, on.event)</c> fan-out cap, and the
    /// layout grid/placement bounds.
    /// </summary>
    /// <remarks>
    /// The rules are enforced HERE as well as at the composition-tool admission gate
    /// (<see cref="StudioCompositionBodyEditor.BindInteraction"/>) because a body can be
    /// authored wholesale through the draft-update surface, which never passes through the
    /// editor. Both call the shared <see cref="StudioInteractionVocabulary"/> checks, so
    /// the two gates cannot drift.
    /// </remarks>
    private static void ValidateCompositionInteractionsAndLayout(
        StudioPackageEnvelope envelope,
        List<StudioValidationDiagnostic> diagnostics)
    {
        var body = envelope.Body!.Value;
        var hasInteractions = body.TryGetProperty("interactions", out var rawInteractions);
        var hasLayout = body.TryGetProperty("layout", out var rawLayout);
        if (!hasInteractions && !hasLayout)
        {
            return;
        }

        if (!ValidateCompositionWireShape(
                hasInteractions,
                rawInteractions,
                hasLayout,
                rawLayout,
                diagnostics))
        {
            return;
        }

        StudioCompositionBody composition;
        try
        {
            composition = StudioCompositionBodyEditor.ReadBody(envelope);
        }
        catch (StudioCompositionBodyException)
        {
            diagnostics.Add(Error(
                "studio.composition.invalid",
                "/body",
                "the composition's interactions/layout blocks do not match the standard shape."));
            return;
        }

        ValidateInteractions(composition, diagnostics);
        ValidateLayout(composition, diagnostics);
    }

    private static bool ValidateCompositionWireShape(
        bool hasInteractions,
        JsonElement rawInteractions,
        bool hasLayout,
        JsonElement rawLayout,
        List<StudioValidationDiagnostic> diagnostics)
    {
        var canDeserialize = true;
        if (hasInteractions)
        {
            if (rawInteractions.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(Error("studio.interactions.array", "/body/interactions", "interactions must be an array."));
                canDeserialize = false;
            }
            else
            {
                var index = 0;
                foreach (var interaction in rawInteractions.EnumerateArray())
                {
                    var path = $"/body/interactions/{index}";
                    if (!ValidateClosedObject(
                            interaction,
                            ["id", "on", "do", "disabled"],
                            path,
                            "studio.interaction.object",
                            "studio.interaction.member.unknown",
                            diagnostics))
                    {
                        canDeserialize = false;
                        index++;
                        continue;
                    }

                    canDeserialize &= ValidateRequiredString(
                        interaction, "id", path, "studio.interaction.id.required", diagnostics);

                    if (!interaction.TryGetProperty("on", out var on)
                        || !ValidateClosedObject(
                            on,
                            ["ref", "event"],
                            $"{path}/on",
                            "studio.interaction.event.object",
                            "studio.interaction.event.member.unknown",
                            diagnostics))
                    {
                        diagnostics.Add(Error(
                            "studio.interaction.event.required",
                            $"{path}/on",
                            "interaction on must be an object."));
                        canDeserialize = false;
                    }
                    else
                    {
                        canDeserialize &= ValidateRequiredString(
                            on, "ref", $"{path}/on", "studio.interaction.event.ref.required", diagnostics);
                        canDeserialize &= ValidateRequiredString(
                            on, "event", $"{path}/on", "studio.interaction.event.name.required", diagnostics);
                    }

                    if (!interaction.TryGetProperty("do", out var action)
                        || !ValidateClosedObject(
                            action,
                            ["ref", "verb", "args"],
                            $"{path}/do",
                            "studio.interaction.action.object",
                            "studio.interaction.action.member.unknown",
                            diagnostics))
                    {
                        diagnostics.Add(Error(
                            "studio.interaction.action.required",
                            $"{path}/do",
                            "interaction do must be an object."));
                        canDeserialize = false;
                    }
                    else
                    {
                        canDeserialize &= ValidateRequiredString(
                            action, "ref", $"{path}/do", "studio.interaction.action.ref.required", diagnostics);
                        canDeserialize &= ValidateRequiredString(
                            action, "verb", $"{path}/do", "studio.interaction.action.verb.required", diagnostics);
                        if (action.TryGetProperty("args", out var args) && args.ValueKind != JsonValueKind.Object)
                        {
                            diagnostics.Add(Error(
                                "studio.interaction.args.object",
                                $"{path}/do/args",
                                "do.args must be a JSON object."));
                        }
                    }

                    if (interaction.TryGetProperty("disabled", out var disabled)
                        && disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        diagnostics.Add(Error(
                            "studio.interaction.disabled.boolean",
                            $"{path}/disabled",
                            "interaction disabled must be a boolean."));
                        canDeserialize = false;
                    }

                    index++;
                }
            }
        }

        if (hasLayout)
        {
            if (!ValidateClosedObject(
                    rawLayout,
                    ["grid", "items"],
                    "/body/layout",
                    "studio.layout.object",
                    "studio.layout.member.unknown",
                    diagnostics))
            {
                canDeserialize = false;
            }
            else
            {
                if (rawLayout.TryGetProperty("grid", out var grid))
                {
                    if (!ValidateClosedObject(
                            grid,
                            ["columns"],
                            "/body/layout/grid",
                            "studio.layout.grid.object",
                            "studio.layout.grid.member.unknown",
                            diagnostics))
                    {
                        canDeserialize = false;
                    }
                    else if (grid.TryGetProperty("columns", out var columns)
                        && (columns.ValueKind != JsonValueKind.Number || !columns.TryGetInt32(out _)))
                    {
                        diagnostics.Add(Error(
                            "studio.layout.grid.columns.integer",
                            "/body/layout/grid/columns",
                            "layout grid columns must be an integer."));
                        canDeserialize = false;
                    }
                }

                if (rawLayout.TryGetProperty("items", out var items))
                {
                    if (items.ValueKind != JsonValueKind.Array)
                    {
                        diagnostics.Add(Error(
                            "studio.layout.items.array",
                            "/body/layout/items",
                            "layout items must be an array."));
                        canDeserialize = false;
                    }
                    else
                    {
                        var index = 0;
                        foreach (var item in items.EnumerateArray())
                        {
                            var path = $"/body/layout/items/{index}";
                            if (!ValidateClosedObject(
                                    item,
                                    ["ref", "x", "y", "w", "h"],
                                    path,
                                    "studio.layout.item.object",
                                    "studio.layout.item.member.unknown",
                                    diagnostics))
                            {
                                canDeserialize = false;
                                index++;
                                continue;
                            }

                            canDeserialize &= ValidateRequiredString(
                                item, "ref", path, "studio.layout.item.ref.required", diagnostics);
                            canDeserialize &= ValidateRequiredIntegerPair(
                                item,
                                "x",
                                "y",
                                path,
                                "studio.layout.item.origin.required",
                                "studio.layout.item.origin.integer",
                                "layout item must declare integer x and y coordinates.",
                                diagnostics);
                            canDeserialize &= ValidateRequiredIntegerPair(
                                item,
                                "w",
                                "h",
                                path,
                                "studio.layout.item.size.required",
                                "studio.layout.item.size.integer",
                                "layout item must declare integer w and h dimensions.",
                                diagnostics);
                            index++;
                        }
                    }
                }
            }
        }

        return canDeserialize;
    }

    private static bool ValidateClosedObject(
        JsonElement value,
        IReadOnlyList<string> allowedMembers,
        string path,
        string objectCode,
        string unknownMemberCode,
        List<StudioValidationDiagnostic> diagnostics)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(Error(objectCode, path, "value must be a JSON object."));
            return false;
        }

        foreach (var member in value.EnumerateObject())
        {
            if (allowedMembers.Contains(member.Name, StringComparer.Ordinal))
            {
                continue;
            }

            diagnostics.Add(Error(
                unknownMemberCode,
                $"{path}/{EscapeJsonPointerSegment(member.Name)}",
                $"member '{member.Name}' is not defined by the ADR-0030 composition schema."));
        }

        return true;
    }

    private static bool ValidateRequiredString(
        JsonElement value,
        string memberName,
        string path,
        string code,
        List<StudioValidationDiagnostic> diagnostics)
    {
        if (value.TryGetProperty(memberName, out var member) && member.ValueKind == JsonValueKind.String)
        {
            return true;
        }

        diagnostics.Add(Error(code, $"{path}/{memberName}", $"{memberName} must be a string."));
        return false;
    }

    private static bool ValidateRequiredIntegerPair(
        JsonElement value,
        string firstName,
        string secondName,
        string path,
        string requiredCode,
        string integerCode,
        string message,
        List<StudioValidationDiagnostic> diagnostics)
    {
        var hasFirst = value.TryGetProperty(firstName, out var first);
        var hasSecond = value.TryGetProperty(secondName, out var second);
        if (!hasFirst || !hasSecond)
        {
            diagnostics.Add(Error(requiredCode, path, message));
        }

        if ((!hasFirst || first.ValueKind == JsonValueKind.Number && first.TryGetInt32(out _))
            && (!hasSecond || second.ValueKind == JsonValueKind.Number && second.TryGetInt32(out _)))
        {
            return true;
        }

        diagnostics.Add(Error(integerCode, path, message));
        return false;
    }

    private static string EscapeJsonPointerSegment(string value)
        => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static void ValidateInteractions(
        StudioCompositionBody composition,
        List<StudioValidationDiagnostic> diagnostics)
    {
        var interactions = composition.Interactions;
        if (interactions is null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fanOut = new Dictionary<(string Ref, string Event), int>();
        for (var i = 0; i < interactions.Count; i++)
        {
            var interaction = interactions[i];
            var path = $"/body/interactions/{i}";
            // The wire model cannot express non-null: an explicit JSON null deserializes
            // into these `required` members regardless of their annotation.
            if (interaction is null || interaction.On is null || interaction.Do is null)
            {
                diagnostics.Add(Error(
                    "studio.interaction.required", path, "interaction must declare 'id', 'on' and 'do'."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(interaction.Id))
            {
                diagnostics.Add(Error("studio.interaction.id.required", $"{path}/id", "interaction id is required."));
            }
            else if (interaction.Id.Length > StudioInteractionVocabulary.MaxInteractionIdLength)
            {
                diagnostics.Add(Error(
                    "studio.interaction.id.too-long",
                    $"{path}/id",
                    $"interaction id must be {StudioInteractionVocabulary.MaxInteractionIdLength} characters or fewer."));
            }
            else if (!ids.Add(interaction.Id))
            {
                diagnostics.Add(Error(
                    "studio.interaction.id.duplicate",
                    $"{path}/id",
                    $"interaction id '{interaction.Id}' must be unique within the interactions block."));
            }

            if (!StudioInteractionVocabulary.IsEventName(interaction.On.Event))
            {
                diagnostics.Add(Error(
                    "studio.interaction.event.unsupported",
                    $"{path}/on/event",
                    $"on.event must be one of: {string.Join(", ", StudioInteractionVocabulary.EventNames)}."));
            }

            if (!StudioInteractionVocabulary.IsActionVerb(interaction.Do.Verb))
            {
                diagnostics.Add(Error(
                    "studio.interaction.verb.unsupported",
                    $"{path}/do/verb",
                    $"do.verb must be one of: {string.Join(", ", StudioInteractionVocabulary.ActionVerbs)}."));
            }

            if (interaction.Do.Args is { ValueKind: not JsonValueKind.Object })
            {
                diagnostics.Add(Error(
                    "studio.interaction.args.object",
                    $"{path}/do/args",
                    "do.args must be a JSON object."));
            }

            ValidateComponentRef(composition, interaction.On.Ref, $"{path}/on/ref", "studio.interaction.ref", diagnostics);
            ValidateComponentRef(composition, interaction.Do.Ref, $"{path}/do/ref", "studio.interaction.ref", diagnostics);

            var key = (interaction.On.Ref, interaction.On.Event);
            fanOut[key] = fanOut.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        foreach (var ((eventRef, eventName), count) in fanOut)
        {
            if (count > StudioInteractionVocabulary.MaxInteractionsPerEventSource)
            {
                diagnostics.Add(Error(
                    "studio.interactions.fan-out",
                    "/body/interactions",
                    $"At most {StudioInteractionVocabulary.MaxInteractionsPerEventSource} interactions may share the "
                    + $"same event source; '{eventRef}'/'{eventName}' has {count}."));
            }
        }
    }

    private static void ValidateLayout(StudioCompositionBody composition, List<StudioValidationDiagnostic> diagnostics)
    {
        var layout = composition.Layout;
        if (layout is null)
        {
            return;
        }

        if (layout.Grid?.Columns is { } columns &&
            (columns < StudioInteractionVocabulary.MinGridColumns || columns > StudioInteractionVocabulary.MaxGridColumns))
        {
            diagnostics.Add(Error(
                "studio.layout.grid.columns.invalid",
                "/body/layout/grid/columns",
                $"layout grid columns must be between {StudioInteractionVocabulary.MinGridColumns} and "
                + $"{StudioInteractionVocabulary.MaxGridColumns}."));
        }

        var items = layout.Items;
        if (items is null)
        {
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var path = $"/body/layout/items/{i}";
            if (item is null)
            {
                diagnostics.Add(Error("studio.layout.item.required", path, "layout item must not be null."));
                continue;
            }

            ValidateComponentRef(composition, item.Ref, $"{path}/ref", "studio.layout.item.ref", diagnostics);

            if (item.X < 0 || item.Y < 0)
            {
                diagnostics.Add(Error(
                    "studio.layout.item.origin.invalid", path, "layout item x/y must be zero or greater."));
            }

            if (item.W < 1 || item.H < 1)
            {
                diagnostics.Add(Error(
                    "studio.layout.item.size.invalid", path, "layout item w/h must be one or greater."));
            }
        }
    }

    private static void ValidateComponentRef(
        StudioCompositionBody composition,
        string? reference,
        string path,
        string codePrefix,
        List<StudioValidationDiagnostic> diagnostics)
    {
        var resolution = StudioInteractionVocabulary.ResolveRef(composition, reference);
        if (resolution == StudioComponentRefResolution.Resolved)
        {
            return;
        }

        var suffix = resolution switch
        {
            StudioComponentRefResolution.Malformed => "invalid",
            StudioComponentRefResolution.ControlsUnsupported => "control-unsupported",
            _ => "unresolved",
        };

        diagnostics.Add(Error(
            $"{codePrefix}.{suffix}",
            path,
            StudioInteractionVocabulary.DescribeResolution(reference, resolution)));
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

    /// <summary>Returns true when the derived content item state enum value is defined by the contract.</summary>
    public static bool IsDefined(StudioContentItemState state)
        => state is StudioContentItemState.Draft
            or StudioContentItemState.Current
            or StudioContentItemState.Published;
}
