// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Protocol-shape validator for served Esri I3S 1.7 SceneServer resources
/// (#1813). Asserts the structural invariants a conformant SceneLayer client
/// relies on — descriptor field presence, store/node-page wiring, node-page
/// obb/lod/child/mesh shapes, and statistics summary shape — without vendoring
/// the CC BY-ND Esri i3s-spec. The validator returns a list of violation
/// messages; an empty list means the resource is structurally conformant. This
/// is the test oracle for the node-page / attribute hard lane.
/// </summary>
internal static class I3sProtocolShapeValidator
{
    /// <summary>Validates a served SceneServer service descriptor.</summary>
    public static IReadOnlyList<string> ValidateService(JsonElement service)
    {
        var violations = new List<string>();

        RequireString(service, "serviceName", violations);
        RequireString(service, "serviceVersion", violations);
        RequireArray(service, "supportedBindings", violations);

        if (!service.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            violations.Add("service.layers must be a non-empty array.");
            return violations;
        }

        if (layers.GetArrayLength() == 0)
        {
            violations.Add("service.layers must carry at least one layer.");
            return violations;
        }

        foreach (var layer in layers.EnumerateArray())
        {
            violations.AddRange(ValidateLayer(layer));
        }

        return violations;
    }

    /// <summary>Validates a served <c>3dSceneLayer</c> descriptor.</summary>
    public static IReadOnlyList<string> ValidateLayer(JsonElement layer)
    {
        var violations = new List<string>();

        RequireInt(layer, "id", violations);
        RequireString(layer, "version", violations);

        var layerType = GetString(layer, "layerType");
        if (layerType is null)
        {
            violations.Add("layer.layerType is required.");
        }
        else if (layerType is not ("3DObject" or "Building" or "PointCloud" or "IntegratedMesh"))
        {
            violations.Add($"layer.layerType '{layerType}' is not a recognized I3S layer type.");
        }

        if (!layer.TryGetProperty("spatialReference", out var sr) || sr.ValueKind != JsonValueKind.Object)
        {
            violations.Add("layer.spatialReference must be an object.");
        }
        else
        {
            RequireInt(sr, "wkid", violations);
        }

        if (!layer.TryGetProperty("store", out var store) || store.ValueKind != JsonValueKind.Object)
        {
            violations.Add("layer.store must be an object.");
            return violations;
        }

        RequireString(store, "profile", violations);

        // If the store advertises nodePages, the pagination descriptor must be
        // complete so a client can page the node tree.
        if (store.TryGetProperty("nodePages", out var nodePages))
        {
            if (nodePages.ValueKind != JsonValueKind.Object)
            {
                violations.Add("store.nodePages must be an object when present.");
            }
            else
            {
                if (!nodePages.TryGetProperty("nodesPerPage", out var nodesPerPage)
                    || nodesPerPage.ValueKind != JsonValueKind.Number
                    || nodesPerPage.GetInt32() <= 0)
                {
                    violations.Add("store.nodePages.nodesPerPage must be a positive integer.");
                }

                RequireString(nodePages, "lodSelectionMetricType", violations);

                if (!store.TryGetProperty("defaultGeometrySchema", out var schema)
                    || schema.ValueKind != JsonValueKind.Object)
                {
                    violations.Add("store.defaultGeometrySchema must accompany store.nodePages.");
                }
            }
        }

        // attributeStorageInfo, when present, must declare a key + name per field
        // (drives identify and the statistics resource address).
        if (layer.TryGetProperty("attributeStorageInfo", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var attr in attrs.EnumerateArray())
            {
                RequireString(attr, "key", violations);
                RequireString(attr, "name", violations);
            }
        }

        return violations;
    }

    /// <summary>Validates a served node page.</summary>
    public static IReadOnlyList<string> ValidateNodePage(JsonElement page)
    {
        var violations = new List<string>();

        if (!page.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            violations.Add("nodePage.nodes must be an array.");
            return violations;
        }

        var index = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("lodThreshold", out var lod) || lod.ValueKind != JsonValueKind.Number)
            {
                violations.Add($"node[{index}].lodThreshold must be a number.");
            }
            else if (lod.GetDouble() < 0)
            {
                violations.Add($"node[{index}].lodThreshold must be non-negative.");
            }

            if (node.TryGetProperty("obb", out var obb))
            {
                ValidateObb(obb, index, violations);
            }

            if (node.TryGetProperty("children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array)
                {
                    violations.Add($"node[{index}].children must be an array when present.");
                }
                else
                {
                    foreach (var child in children.EnumerateArray()
                        .Where(c => c.ValueKind != JsonValueKind.Number || c.GetInt32() < 0))
                    {
                        violations.Add($"node[{index}].children must be non-negative integer indices.");
                    }
                }
            }

            if (node.TryGetProperty("mesh", out var mesh))
            {
                ValidateMesh(mesh, index, violations);
            }

            index++;
        }

        return violations;
    }

    /// <summary>Validates a served per-field statistics document.</summary>
    public static IReadOnlyList<string> ValidateStatistics(JsonElement document)
    {
        var violations = new List<string>();

        if (!document.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
        {
            violations.Add("statistics.stats must be an object.");
            return violations;
        }

        if (!stats.TryGetProperty("totalValuesCount", out var total)
            || total.ValueKind != JsonValueKind.Number
            || total.GetInt64() < 0)
        {
            violations.Add("statistics.stats.totalValuesCount must be a non-negative number.");
        }

        return violations;
    }

    private static void ValidateObb(JsonElement obb, int index, List<string> violations)
    {
        if (obb.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"node[{index}].obb must be an object.");
            return;
        }

        RequireVector(obb, "center", 3, index, violations);
        RequireVector(obb, "halfSize", 3, index, violations);
        RequireVector(obb, "quaternion", 4, index, violations);
    }

    private static void ValidateMesh(JsonElement mesh, int index, List<string> violations)
    {
        if (mesh.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"node[{index}].mesh must be an object.");
            return;
        }

        if (!mesh.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"node[{index}].mesh.geometry must be an object.");
            return;
        }

        if (!geometry.TryGetProperty("resource", out var resource)
            || resource.ValueKind != JsonValueKind.Number
            || resource.GetInt32() < 0)
        {
            violations.Add($"node[{index}].mesh.geometry.resource must be a non-negative integer.");
        }
    }

    private static void RequireVector(JsonElement parent, string name, int length, int index, List<string> violations)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != length)
        {
            violations.Add($"node[{index}].obb.{name} must be an array of {length} numbers.");
        }
    }

    private static void RequireString(JsonElement parent, string name, List<string> violations)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString()))
        {
            violations.Add($"'{name}' must be a non-empty string.");
        }
    }

    private static void RequireArray(JsonElement parent, string name, List<string> violations)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            violations.Add($"'{name}' must be an array.");
        }
    }

    private static void RequireInt(JsonElement parent, string name, List<string> violations)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            violations.Add($"'{name}' must be a number.");
        }
    }

    private static string? GetString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
