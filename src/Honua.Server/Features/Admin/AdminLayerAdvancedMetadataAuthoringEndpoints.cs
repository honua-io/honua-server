// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints that author the remaining typed sub-records of a layer's canonical
/// metadata that were previously stored &amp; served but had no admin write path:
/// Esri-style subtypes (<see cref="MetadataV2Subtypes"/>), attribute rules
/// (<see cref="MetadataV2AttributeRule"/>), 3D extrusion + symbology
/// (<see cref="MetadataV2ExtrusionInfo"/> / <see cref="Symbology3D"/>), publication
/// presentation overrides (<see cref="MetadataV2Publication"/> title / aliases /
/// capabilities / formats / primary), and resource lifecycle status
/// (<see cref="MetadataV2Status"/>). All write directly into the Metadata v2 graph,
/// mirroring <see cref="AdminLayerMetadataAuthoringEndpoints"/>.
/// </summary>
internal static class AdminLayerAdvancedMetadataAuthoringEndpoints
{
    private const int MetadataMutationMaxAttempts = 5;

    public static void MapAdminLayerAdvancedMetadataAuthoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var layers = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Authoring")
            .RequireAdminAuthorization();

        _ = layers.MapGet("/{layerId:int}/subtypes", HandleGetSubtypes).WithName("GetAdminLayerSubtypes");
        _ = layers.MapPut("/{layerId:int}/subtypes", HandleSetSubtypes).WithName("SetAdminLayerSubtypes");

        _ = layers.MapGet("/{layerId:int}/attribute-rules", HandleGetAttributeRules).WithName("GetAdminLayerAttributeRules");
        _ = layers.MapPut("/{layerId:int}/attribute-rules", HandleSetAttributeRules).WithName("SetAdminLayerAttributeRules");

        _ = layers.MapGet("/{layerId:int}/extrusion", HandleGetExtrusion).WithName("GetAdminLayerExtrusion");
        _ = layers.MapPut("/{layerId:int}/extrusion", HandleSetExtrusion).WithName("SetAdminLayerExtrusion");

        _ = layers.MapGet("/{layerId:int}/status", HandleGetStatus).WithName("GetAdminLayerStatus");
        _ = layers.MapPut("/{layerId:int}/status", HandleSetStatus).WithName("SetAdminLayerStatus");

        var publications = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/publications")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Authoring")
            .RequireAdminAuthorization();

        _ = publications.MapGet("/{publicationId}/overrides", HandleGetPublicationOverrides).WithName("GetAdminPublicationOverrides");
        _ = publications.MapPut("/{publicationId}/overrides", HandleSetPublicationOverrides).WithName("SetAdminPublicationOverrides");
    }

    // ---- 1. Subtypes (MetadataV2Subtypes) ---------------------------------------------------------------

    private static async Task<IResult> HandleGetSubtypes(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<LayerSubtypesResponse>.CreateSuccess(BuildSubtypesResponse(layerId, resource.Subtypes)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerSubtypesResponse);
    }

    private static async Task<IResult> HandleSetSubtypes(
        int layerId, LayerSubtypesUpdateRequest request, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        MetadataV2Subtypes? newSubtypes;
        if (request.Clear)
        {
            newSubtypes = null;
        }
        else
        {
            var (built, error) = BuildSubtypes(layerId, resource, request);
            if (error != null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, error);
            }

            newSubtypes = built;
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with { Subtypes = newSubtypes }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerSubtypesResponse>.CreateSuccess(BuildSubtypesResponse(layerId, refreshed.Subtypes)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerSubtypesResponse);
    }

    private static (MetadataV2Subtypes? Subtypes, string? Error) BuildSubtypes(
        int layerId, MetadataV2Resource resource, LayerSubtypesUpdateRequest request)
    {
        // A non-clearing update without a subtype field is ambiguous: the model keys the
        // whole set on the subtype field, so require it.
        var existing = resource.Subtypes;
        var subtypeField = request.SubtypeField ?? existing?.SubtypeField;
        if (string.IsNullOrWhiteSpace(subtypeField))
        {
            return (null, "subtypeField is required (send clear=true to remove the subtype set).");
        }

        var fields = resource.SchemaFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!fields.Contains(subtypeField))
        {
            return (null, $"Subtype field '{subtypeField}' does not exist on layer {layerId}.");
        }

        var sourceSubtypes = request.Subtypes
            ?? existing?.Subtypes.Select(ToSubtypePayload).ToArray()
            ?? Array.Empty<SubtypePayload>();

        var subtypes = new List<MetadataV2Subtype>(sourceSubtypes.Count);
        foreach (var payload in sourceSubtypes)
        {
            if (string.IsNullOrWhiteSpace(payload.Name))
            {
                return (null, "Each subtype requires a non-empty name.");
            }

            IReadOnlyDictionary<string, MetadataV2SubtypeFieldOverride> overrides;
            if (payload.FieldOverrides is { Count: > 0 } po)
            {
                var map = new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase);
                foreach (var (fieldName, ov) in po)
                {
                    if (!fields.Contains(fieldName))
                    {
                        return (null, $"Subtype field override references unknown field '{fieldName}' on layer {layerId}.");
                    }

                    map[fieldName] = new MetadataV2SubtypeFieldOverride
                    {
                        DefaultValue = ov.DefaultValue,
                        Domain = ov.Domain?.Deserialize(MetadataV2JsonContext.Default.MetadataV2FieldDomain),
                    };
                }

                overrides = map;
            }
            else
            {
                overrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase);
            }

            subtypes.Add(new MetadataV2Subtype
            {
                Code = payload.Code,
                Name = payload.Name,
                FieldOverrides = overrides,
            });
        }

        return (new MetadataV2Subtypes
        {
            SubtypeField = subtypeField,
            DefaultSubtypeCode = request.DefaultSubtypeCode ?? existing?.DefaultSubtypeCode,
            Subtypes = subtypes,
        }, null);
    }

    private static LayerSubtypesResponse BuildSubtypesResponse(int layerId, MetadataV2Subtypes? subtypes)
    {
        return new LayerSubtypesResponse
        {
            LayerId = layerId,
            SubtypeField = subtypes?.SubtypeField,
            DefaultSubtypeCode = subtypes?.DefaultSubtypeCode,
            Subtypes = subtypes is null
                ? Array.Empty<SubtypePayload>()
                : subtypes.Subtypes.Select(ToSubtypePayload).ToArray(),
        };
    }

    private static SubtypePayload ToSubtypePayload(MetadataV2Subtype subtype) => new()
    {
        Code = subtype.Code,
        Name = subtype.Name,
        FieldOverrides = subtype.FieldOverrides.Count == 0
            ? null
            : subtype.FieldOverrides.ToDictionary(
                kv => kv.Key,
                kv => new SubtypeFieldOverridePayload
                {
                    DefaultValue = kv.Value.DefaultValue,
                    Domain = kv.Value.Domain is null
                        ? null
                        : System.Text.Json.JsonSerializer.SerializeToElement(
                            kv.Value.Domain, MetadataV2JsonContext.Default.MetadataV2FieldDomain),
                },
                StringComparer.OrdinalIgnoreCase),
    };

    // ---- 2. Attribute rules (MetadataV2AttributeRule[]) -------------------------------------------------

    private static async Task<IResult> HandleGetAttributeRules(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<LayerAttributeRulesResponse>.CreateSuccess(BuildAttributeRulesResponse(layerId, resource.AttributeRules)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerAttributeRulesResponse);
    }

    private static async Task<IResult> HandleSetAttributeRules(
        int layerId, LayerAttributeRulesUpdateRequest request, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        var (rules, error) = BuildAttributeRules(layerId, resource, request);
        if (error != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, error);
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with { AttributeRules = rules! }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerAttributeRulesResponse>.CreateSuccess(BuildAttributeRulesResponse(layerId, refreshed.AttributeRules)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerAttributeRulesResponse);
    }

    private static (IReadOnlyList<MetadataV2AttributeRule>? Rules, string? Error) BuildAttributeRules(
        int layerId, MetadataV2Resource resource, LayerAttributeRulesUpdateRequest request)
    {
        var fields = resource.SchemaFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<MetadataV2AttributeRule>(request.Rules.Count);
        foreach (var payload in request.Rules)
        {
            if (string.IsNullOrWhiteSpace(payload.Name))
            {
                return (null, "Each attribute rule requires a non-empty name.");
            }

            if (!names.Add(payload.Name))
            {
                return (null, $"Duplicate attribute rule name '{payload.Name}'.");
            }

            if (!TryParseEnum<MetadataV2AttributeRuleType>(payload.Type, out var ruleType))
            {
                return (null, $"Unsupported attribute rule type '{payload.Type}'. Supported: calculation, constraint, validation.");
            }

            // The target field is only meaningful for calculation rules; validate it there.
            if (ruleType == MetadataV2AttributeRuleType.Calculation
                && !string.IsNullOrWhiteSpace(payload.FieldName)
                && !fields.Contains(payload.FieldName))
            {
                return (null, $"Attribute rule '{payload.Name}' target field '{payload.FieldName}' does not exist on layer {layerId}.");
            }

            rules.Add(new MetadataV2AttributeRule
            {
                Name = payload.Name,
                Type = ruleType,
                FieldName = string.IsNullOrWhiteSpace(payload.FieldName) ? null : payload.FieldName,
                ScriptExpression = payload.ScriptExpression,
                TriggeringEvents = payload.TriggeringEvents ?? Array.Empty<string>(),
                ErrorMessage = payload.ErrorMessage,
                IsEnabled = payload.IsEnabled,
            });
        }

        return (rules, null);
    }

    private static LayerAttributeRulesResponse BuildAttributeRulesResponse(
        int layerId, IReadOnlyList<MetadataV2AttributeRule> rules)
    {
        return new LayerAttributeRulesResponse
        {
            LayerId = layerId,
            Rules = rules.Select(r => new AttributeRulePayload
            {
                Name = r.Name,
                Type = EnumToWire(r.Type),
                FieldName = r.FieldName,
                ScriptExpression = r.ScriptExpression,
                TriggeringEvents = r.TriggeringEvents,
                ErrorMessage = r.ErrorMessage,
                IsEnabled = r.IsEnabled,
            }).ToArray(),
        };
    }

    // ---- 3. 3D extrusion & symbology (MetadataV2ExtrusionInfo + Symbology3D) -----------------------------

    private static async Task<IResult> HandleGetExtrusion(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<LayerExtrusionResponse>.CreateSuccess(BuildExtrusionResponse(layerId, resource.Extrusion, resource.Symbology3D)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerExtrusionResponse);
    }

    private static async Task<IResult> HandleSetExtrusion(
        int layerId, LayerExtrusionUpdateRequest request, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        var error = ValidateExtrusion(layerId, resource, request);
        if (error != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, error);
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with
            {
                Extrusion = request.ClearExtrusion
                    ? null
                    : (request.Extrusion is null ? res.Extrusion : ToExtrusion(request.Extrusion)),
                Symbology3D = request.ClearSymbology3D
                    ? null
                    : (request.Symbology3D is null ? res.Symbology3D : ToSymbology3D(request.Symbology3D)),
            }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerExtrusionResponse>.CreateSuccess(BuildExtrusionResponse(layerId, refreshed.Extrusion, refreshed.Symbology3D)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerExtrusionResponse);
    }

    private static string? ValidateExtrusion(int layerId, MetadataV2Resource resource, LayerExtrusionUpdateRequest request)
    {
        var fields = resource.SchemaFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!request.ClearExtrusion && request.Extrusion is { } ext)
        {
            if (string.IsNullOrWhiteSpace(ext.HeightField) || !fields.Contains(ext.HeightField))
            {
                return $"Extrusion height field '{ext.HeightField}' does not exist on layer {layerId}.";
            }

            if (!string.IsNullOrWhiteSpace(ext.BaseHeightField) && !fields.Contains(ext.BaseHeightField))
            {
                return $"Extrusion base height field '{ext.BaseHeightField}' does not exist on layer {layerId}.";
            }

            if (!string.IsNullOrWhiteSpace(ext.Unit) && !MetadataV2VerticalUnits.TryNormalize(ext.Unit, out _))
            {
                return $"Unsupported extrusion unit '{ext.Unit}'. Supported: meters, feet, usSurveyFeet.";
            }

            if (ext.DefaultHeight is < 0)
            {
                return "Extrusion defaultHeight must be greater than or equal to zero.";
            }
        }

        if (!request.ClearSymbology3D && request.Symbology3D is { } sym)
        {
            foreach (var rule in sym.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Attribute) || !fields.Contains(rule.Attribute))
                {
                    return $"3D symbology rule references unknown attribute '{rule.Attribute}' on layer {layerId}.";
                }

                if (!TryParseEnum<Symbology3DComparison>(rule.Comparison, out _))
                {
                    return $"Unsupported 3D symbology comparison '{rule.Comparison}'.";
                }
            }
        }

        return null;
    }

    private static MetadataV2ExtrusionInfo ToExtrusion(ExtrusionInfoPayload payload) => new()
    {
        HeightField = payload.HeightField,
        BaseHeightField = payload.BaseHeightField,
        Unit = payload.Unit,
        DefaultHeight = payload.DefaultHeight,
        MaterialHint = payload.MaterialHint,
    };

    private static Symbology3D ToSymbology3D(Symbology3DPayload payload) => new()
    {
        DefaultColor = ToColor(payload.DefaultColor),
        DefaultOpacity = payload.DefaultOpacity,
        Rules = payload.Rules.Select(r => new Symbology3DRule
        {
            Attribute = r.Attribute,
            Comparison = ParseEnumOrDefault(r.Comparison, Symbology3DComparison.Equals),
            Value = r.Value,
            Color = ToColor(r.Color),
            Opacity = r.Opacity,
            Visible = r.Visible,
        }).ToArray(),
    };

    private static Symbology3DColor? ToColor(Symbology3DColorPayload? payload) =>
        payload is null ? null : new Symbology3DColor(payload.Red, payload.Green, payload.Blue);

    private static LayerExtrusionResponse BuildExtrusionResponse(
        int layerId, MetadataV2ExtrusionInfo? extrusion, Symbology3D? symbology)
    {
        return new LayerExtrusionResponse
        {
            LayerId = layerId,
            Extrusion = extrusion is null ? null : new ExtrusionInfoPayload
            {
                HeightField = extrusion.HeightField,
                BaseHeightField = extrusion.BaseHeightField,
                Unit = extrusion.Unit,
                DefaultHeight = extrusion.DefaultHeight,
                MaterialHint = extrusion.MaterialHint,
            },
            Symbology3D = symbology is null ? null : new Symbology3DPayload
            {
                DefaultColor = ToColorPayload(symbology.DefaultColor),
                DefaultOpacity = symbology.DefaultOpacity,
                Rules = symbology.Rules.Select(r => new Symbology3DRulePayload
                {
                    Attribute = r.Attribute,
                    Comparison = ComparisonToWire(r.Comparison),
                    Value = r.Value,
                    Color = ToColorPayload(r.Color),
                    Opacity = r.Opacity,
                    Visible = r.Visible,
                }).ToArray(),
            },
        };
    }

    private static Symbology3DColorPayload? ToColorPayload(Symbology3DColor? color) =>
        color is null ? null : new Symbology3DColorPayload
        {
            Red = color.Value.Red,
            Green = color.Value.Green,
            Blue = color.Value.Blue,
        };

    // ---- 4. Publication overrides (MetadataV2Publication) -----------------------------------------------

    private static async Task<IResult> HandleGetPublicationOverrides(
        string publicationId, HttpContext context,
        [FromServices] IMetadataV2GraphStore graphStore,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publication = snapshot.Graph.Publications
            .FirstOrDefault(p => string.Equals(p.Metadata.Id, publicationId, StringComparison.Ordinal));
        if (publication is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Publication '{publicationId}' not found.");
        }

        return Results.Json(
            ApiResponse<PublicationOverridesResponse>.CreateSuccess(BuildPublicationResponse(publication)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponsePublicationOverridesResponse);
    }

    private static async Task<IResult> HandleSetPublicationOverrides(
        string publicationId, PublicationOverridesUpdateRequest request, HttpContext context,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        MetadataV2Publication? mutatedPublication = null;
        var found = await MutatePublicationAsync(
            graphStore, publicationId,
            pub =>
            {
                var updated = ApplyPublicationOverrides(pub, request);
                mutatedPublication = updated;
                return updated;
            }, cancellationToken).ConfigureAwait(false);

        if (!found || mutatedPublication is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status404NotFound, $"Publication '{publicationId}' not found.");
        }

        // Invalidate any layer index this publication routes through so served catalog
        // metadata reflects the new overrides.
        if (mutatedPublication.LayerIndex is { } layerIndex)
        {
            await cacheInvalidator.InvalidateServiceCatalogAsync(
                mutatedPublication.ServiceId, [layerIndex], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await cacheInvalidator.InvalidateServiceCatalogAsync(
                mutatedPublication.ServiceId, null, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var refreshed = snapshot.Graph.Publications
            .FirstOrDefault(p => string.Equals(p.Metadata.Id, publicationId, StringComparison.Ordinal))
            ?? mutatedPublication;
        return Results.Json(
            ApiResponse<PublicationOverridesResponse>.CreateSuccess(BuildPublicationResponse(refreshed)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponsePublicationOverridesResponse);
    }

    private static MetadataV2Publication ApplyPublicationOverrides(
        MetadataV2Publication pub, PublicationOverridesUpdateRequest request)
    {
        return pub with
        {
            TitleOverride = request.TitleOverride is null
                ? pub.TitleOverride
                : (string.IsNullOrWhiteSpace(request.TitleOverride) ? null : request.TitleOverride),
            FieldAliases = request.FieldAliases is null
                ? pub.FieldAliases
                : new Dictionary<string, string>(request.FieldAliases, StringComparer.Ordinal),
            Capabilities = request.Capabilities ?? pub.Capabilities,
            SupportedFormats = request.SupportedFormats ?? pub.SupportedFormats,
            IsPrimary = request.IsPrimary ?? pub.IsPrimary,
        };
    }

    private static PublicationOverridesResponse BuildPublicationResponse(MetadataV2Publication publication)
    {
        return new PublicationOverridesResponse
        {
            PublicationId = publication.Metadata.Id,
            ResourceId = publication.ResourceId,
            ServiceId = publication.ServiceId,
            TitleOverride = publication.TitleOverride,
            FieldAliases = publication.FieldAliases,
            Capabilities = publication.Capabilities,
            SupportedFormats = publication.SupportedFormats,
            IsPrimary = publication.IsPrimary,
        };
    }

    // ---- 5. Lifecycle status (MetadataV2Status) ---------------------------------------------------------

    private static async Task<IResult> HandleGetStatus(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<LayerStatusResponse>.CreateSuccess(BuildStatusResponse(layerId, resource.Status)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerStatusResponse);
    }

    private static async Task<IResult> HandleSetStatus(
        int layerId, LayerStatusUpdateRequest request, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        if (request.Lifecycle is null && request.State is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status400BadRequest, "At least one of lifecycle or state must be supplied.");
        }

        MetadataV2LifecycleStatus? lifecycle = null;
        if (request.Lifecycle is not null)
        {
            if (!TryParseEnum<MetadataV2LifecycleStatus>(request.Lifecycle, out var parsed))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    context, StatusCodes.Status400BadRequest,
                    $"Unsupported lifecycle '{request.Lifecycle}'. Supported: draft, active, deprecated, retired, archived.");
            }

            lifecycle = parsed;
        }

        MetadataV2OperationalState? state = null;
        if (request.State is not null)
        {
            if (!TryParseEnum<MetadataV2OperationalState>(request.State, out var parsed))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    context, StatusCodes.Status400BadRequest,
                    $"Unsupported state '{request.State}'. Supported: unknown, ready, pending, degraded, failed.");
            }

            state = parsed;
        }

        // No metadata-resource lifecycle transition service exists (lifecycle services in
        // the codebase are for Forms/Studio/Geoprocessing packages), so set the requested
        // state directly. The Draft->Published(=Active) progression the console drives is a
        // free transition; any future transition policy can be layered in here.
        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with
            {
                Status = res.Status with
                {
                    Lifecycle = lifecycle ?? res.Status.Lifecycle,
                    State = state ?? res.Status.State,
                    ObservedAt = DateTimeOffset.UtcNow,
                },
            }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerStatusResponse>.CreateSuccess(BuildStatusResponse(layerId, refreshed.Status)),
            LayerAdvancedMetadataAuthoringJsonContext.Default.ApiResponseLayerStatusResponse);
    }

    private static LayerStatusResponse BuildStatusResponse(int layerId, MetadataV2Status status)
    {
        return new LayerStatusResponse
        {
            LayerId = layerId,
            Lifecycle = EnumToWire(status.Lifecycle),
            State = EnumToWire(status.State),
            ObservedAt = status.ObservedAt,
        };
    }

    // ---- shared helpers ---------------------------------------------------------------------------------

    private static async Task<(MetadataV2Resource? Resource, IResult? Problem)> ValidateLayerAsync(
        int layerId, HttpContext context, IResourceValidator resourceValidator, CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerV2Async(layerId, cancellationToken).ConfigureAwait(false);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            return (null, ProblemDetailsHelpers.CreateAdminProblem(
                context, statusCode, layerResult.ErrorMessage ?? $"Layer {layerId} not found."));
        }

        return (layerResult.Resource, null);
    }

    private static async Task<MetadataV2Resource> GetRefreshedResourceAsync(
        IMetadataV2GraphStore graphStore, MetadataV2Resource fallback, CancellationToken cancellationToken)
    {
        var updated = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return updated.Graph.Resources.FirstOrDefault(r => r.Metadata.Id == fallback.Metadata.Id) ?? fallback;
    }

    /// <summary>
    /// Read-modify-write the resource(s) a layer publishes through, with an optimistic-concurrency retry
    /// on Metadata v2 etag mismatch. Mirrors <see cref="AdminLayerMetadataAuthoringEndpoints"/>.
    /// </summary>
    private static async Task MutateResourceForLayerAsync(
        IMetadataV2GraphStore graphStore, int layerId,
        Func<MetadataV2Resource, MetadataV2Resource> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var targetResourceIds = snapshot.Graph.Publications
                .Where(p => p.Identifier.IsNumeric && p.LayerIndex == layerId)
                .Select(p => p.ResourceId)
                .ToHashSet(StringComparer.Ordinal);
            if (targetResourceIds.Count == 0)
            {
                return;
            }

            var resources = snapshot.Graph.Resources.ToArray();
            var mutated = false;
            for (var i = 0; i < resources.Length; i++)
            {
                if (targetResourceIds.Contains(resources[i].Metadata.Id))
                {
                    resources[i] = mutate(resources[i]);
                    mutated = true;
                }
            }

            if (!mutated)
            {
                return;
            }

            var updated = snapshot.Graph with
            {
                Resources = resources,
                Revision = snapshot.Graph.Revision + 1,
            };

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply.
            }
        }
    }

    /// <summary>
    /// Read-modify-write a single publication node (addressed by its
    /// <see cref="MetadataV2ObjectMetadata.Id"/>), with optimistic-concurrency retry.
    /// Returns false when the publication does not exist.
    /// </summary>
    private static async Task<bool> MutatePublicationAsync(
        IMetadataV2GraphStore graphStore, string publicationId,
        Func<MetadataV2Publication, MetadataV2Publication> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publications = snapshot.Graph.Publications.ToArray();
            var index = Array.FindIndex(
                publications, p => string.Equals(p.Metadata.Id, publicationId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            publications[index] = mutate(publications[index]);

            var updated = snapshot.Graph with
            {
                Publications = publications,
                Revision = snapshot.Graph.Revision + 1,
            };

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply.
            }
        }
    }

    private static bool IsEtagMismatch(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.Contains("etag mismatch", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out result);

    private static TEnum ParseEnumOrDefault<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string EnumToWire<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString().ToLowerInvariant();

    /// <summary>
    /// camelCase wire form for a 3D symbology comparison so the response round-trips
    /// the same token the request accepts (e.g. <c>greaterThanOrEqual</c>). A plain
    /// <c>ToLowerInvariant()</c> would flatten the multi-word operators.
    /// </summary>
    private static string ComparisonToWire(Symbology3DComparison comparison) => comparison switch
    {
        Symbology3DComparison.Equals => "equals",
        Symbology3DComparison.NotEquals => "notEquals",
        Symbology3DComparison.GreaterThan => "greaterThan",
        Symbology3DComparison.GreaterThanOrEqual => "greaterThanOrEqual",
        Symbology3DComparison.LessThan => "lessThan",
        Symbology3DComparison.LessThanOrEqual => "lessThanOrEqual",
        _ => comparison.ToString().ToLowerInvariant(),
    };
}
