// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

public static class GeoservicesImportTargetPlanner
{
    private const string ServiceIdPrefix = "svc-import-";
    private static readonly SearchValues<char> ServiceIdHexDigits = SearchValues.Create("0123456789abcdef");

    public static GeoservicesImportTarget Plan(MetadataV2Graph graph, GeoservicesImportSelection selection, string? trustedTenant)
    {
        if (selection.Plan is not null) return UsePlan(graph, selection, trustedTenant);
        var tenant = LayerPublicationNamespace.ResolveTenant(selection.Namespace, trustedTenant);
        var existing = selection.ServiceName is null ? [] : graph.Services.Where(service =>
            string.Equals(service.Metadata.Name, selection.ServiceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (existing.Length > 0) return ExistingTarget(graph, existing, selection, tenant);

        var source = GeoservicesSourceService.FromUrl(selection.ServiceUrl);
        if (source is null && !selection.FolderId.IsSpecified)
            throw InvalidMapping("The source service path is unsupported. Select an explicit target folder or root.");
        var folder = SelectFolder(graph, selection.FolderId, source?.FolderPath, tenant);
        var publicName = selection.PublicName ?? selection.ServiceName ?? source?.PublicName;
        if (publicName is null || !MetadataV2ServicePaths.IsPublicName(publicName, folder?.Metadata.Id))
            throw InvalidMapping("The source public name requires an explicit valid target service name or publicName.");

        var serviceName = selection.ServiceName ?? AllocateServiceName(graph, publicName, folder?.Metadata.Name);
        if (!MetadataV2ServicePaths.IsIdentifier(serviceName) || MetadataV2ServicePaths.IsReserved(serviceName))
            throw InvalidMapping("The target logical service name must be an unreserved ASCII identifier of at most 64 characters.");
        var target = new GeoservicesImportTarget
        {
            ServiceName = serviceName,
            ServiceId = $"{ServiceIdPrefix}{Guid.NewGuid():N}",
            PublicName = publicName,
            FolderId = folder?.Metadata.Id,
            FolderName = folder?.Metadata.Name,
            Namespace = selection.Namespace,
            TenantId = tenant
        };
        ValidateTargetPath(graph, target);
        return target;
    }

    private static GeoservicesImportTarget UsePlan(MetadataV2Graph graph, GeoservicesImportSelection selection, string? trustedTenant)
    {
        var plan = selection.Plan!;
        if (!Uri.TryCreate(plan.ServiceUrl, UriKind.Absolute, out var plannedUrl) ||
            !Uri.TryCreate(selection.ServiceUrl, UriKind.Absolute, out var requestedUrl) ||
            plannedUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') != requestedUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') ||
            (selection.ServiceName is not null && selection.ServiceName != plan.ServiceName) ||
            (selection.PublicName is not null && selection.PublicName != plan.PublicName) ||
            (selection.FolderId.IsSpecified && selection.FolderId.Value != plan.FolderId) ||
            (selection.Namespace is not null && selection.Namespace != plan.Namespace) ||
            string.IsNullOrWhiteSpace(plan.ServiceId) || string.IsNullOrWhiteSpace(plan.ServiceName) ||
            string.IsNullOrWhiteSpace(plan.PublicName) || (plan.FolderId is null) != (plan.FolderName is null))
            throw InvalidMapping("The import request does not match its selected plan.");

        var bound = plan.BindTenant(LayerPublicationNamespace.ResolveTenant(plan.Namespace, trustedTenant));
        if ((!plan.ExistingService && !IsAllocatedServiceId(plan.ServiceId) &&
             !graph.Services.Any(service => service.Metadata.Id == plan.ServiceId)) ||
            (plan.FolderId is not null && !graph.Folders.Any(folder => folder.Metadata.Id == plan.FolderId) &&
             plan.FolderId != SourceFolderId(plan.FolderName!, bound.TenantId)))
            throw InvalidMapping("The import plan has an invalid target identity. Request a new plan.");
        bound.ValidateServiceIdentity(graph);
        if (plan.FolderId is not null && !graph.Folders.Any(folder => folder.Metadata.Id == plan.FolderId) &&
            !string.Equals(GeoservicesSourceService.FromUrl(selection.ServiceUrl)?.FolderPath, plan.FolderName, StringComparison.OrdinalIgnoreCase))
            throw InvalidMapping("A new planned folder must preserve the source folder. Select an existing target folder or root to remap.");
        graph = bound.PrepareFolder(graph);

        var target = Plan(graph, selection with
        {
            Plan = null,
            ServiceName = plan.ServiceName,
            PublicName = plan.PublicName,
            FolderId = new(plan.FolderId),
            Namespace = plan.Namespace
        }, trustedTenant);
        if (target.ExistingService && target.ServiceId != plan.ServiceId)
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                "The planned import service facet changed. Select a new target mapping.");
        target = target with { ServiceId = plan.ServiceId, ExistingService = plan.ExistingService };
        ValidateTargetPath(graph, target);
        return target;
    }

    private static GeoservicesImportTarget ExistingTarget(MetadataV2Graph graph,
        MetadataV2Service[] services, GeoservicesImportSelection selection, string? tenant)
    {
        var first = services[0];
        if (services.Any(service => !MetadataV2ServicePaths.SameTenant(service.Metadata.Tenant, tenant) ||
            service.Metadata.Namespace != selection.Namespace))
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict, "The existing service belongs to a different publication scope.");
        if ((selection.FolderId.IsSpecified && selection.FolderId.Value != first.FolderId) ||
            (selection.PublicName is not null && selection.PublicName != first.EffectivePublicName))
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                "Import cannot change an existing service's folder or public name. Use the service move operation.");
        return new GeoservicesImportTarget
        {
            ServiceName = first.Metadata.Name,
            ServiceId = first.Metadata.Id,
            ExistingService = true,
            PublicName = first.EffectivePublicName,
            FolderId = first.FolderId,
            FolderName = MetadataV2ServicePaths.FolderName(graph, first),
            Namespace = selection.Namespace,
            TenantId = tenant
        };
    }

    private static MetadataV2Folder? SelectFolder(MetadataV2Graph graph, MetadataV2FolderSelection selection,
        string? sourceFolder, string? tenant)
    {
        if (selection.IsSpecified)
            return selection.Value is null ? null : graph.Folders.FirstOrDefault(folder => !folder.Deleted &&
                folder.Metadata.Id == selection.Value && MetadataV2ServicePaths.SameTenant(folder.Metadata.Tenant, tenant)) ??
                throw new LayerPublishingException(LayerPublishingErrorKind.NotFound, "The target folder was not found in the publication scope.");
        if (sourceFolder is null) return null;
        if (!MetadataV2ServicePaths.IsFolderName(sourceFolder))
            throw InvalidMapping("The source folder is invalid, reserved or nested. Select an explicit target folder or root.");
        return graph.Folders.FirstOrDefault(folder => !folder.Deleted &&
            string.Equals(folder.Metadata.Name, sourceFolder, StringComparison.OrdinalIgnoreCase) &&
            MetadataV2ServicePaths.SameTenant(folder.Metadata.Tenant, tenant)) ?? new MetadataV2Folder
            {
                Metadata = new() { Id = SourceFolderId(sourceFolder, tenant), Name = sourceFolder, Title = sourceFolder, Tenant = tenant }
            };
    }

    private static string SourceFolderId(string folderName, string? tenant)
    {
        var identity = MetadataV2ServicePaths.NormalizeTenant(tenant) + "\0" + folderName.ToLowerInvariant();
        return "folder-import-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    private static bool IsAllocatedServiceId(string serviceId)
        => serviceId.Length == ServiceIdPrefix.Length + 32 && serviceId.StartsWith(ServiceIdPrefix, StringComparison.Ordinal) &&
           serviceId.AsSpan(ServiceIdPrefix.Length).IndexOfAnyExcept(ServiceIdHexDigits) < 0;

    private static string AllocateServiceName(MetadataV2Graph graph, string publicName, string? folderName)
    {
        var reserved = graph.Services.Select(service => service.Metadata.Name)
            .Concat(graph.Services.Select(service => service.Metadata.Id))
            .Concat(graph.Folders.SelectMany(folder => folder.PreviousNames.Append(folder.Metadata.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (folderName is not null) reserved.Add(folderName);
        if (!reserved.Contains(publicName)) return publicName;
        for (var ordinal = 2; ; ordinal++)
        {
            var suffix = $"-{ordinal.ToString(CultureInfo.InvariantCulture)}";
            var candidate = publicName[..Math.Min(publicName.Length, 64 - suffix.Length)] + suffix;
            if (!reserved.Contains(candidate)) return candidate;
        }
    }

    public static IReadOnlyList<GeoservicesImportTarget> PlanBatch(MetadataV2Graph graph,
        IReadOnlyList<GeoservicesImportSelection> selections, string? trustedTenant)
    {
        var persistedServiceIds = graph.Services.Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var plans = new Dictionary<GeoservicesImportSelection, GeoservicesImportTarget>();
        var targets = new List<GeoservicesImportTarget>(selections.Count);
        foreach (var selection in selections)
        {
            if (!plans.TryGetValue(selection, out var target))
            {
                target = Plan(graph, selection, trustedTenant);
                target = target with { ExistingService = persistedServiceIds.Contains(target.ServiceId!) };
                plans.Add(selection, target);
                graph = AddPlannedTarget(graph, target);
            }
            targets.Add(target);
        }
        return targets;
    }

    private static MetadataV2Graph AddPlannedTarget(MetadataV2Graph graph, GeoservicesImportTarget target)
    {
        var candidate = target.PrepareFolder(graph);
        if (graph.Services.Any(service => service.Metadata.Id == target.ServiceId)) return candidate;
        var service = new MetadataV2Service
        {
            Metadata = new() { Id = target.ServiceId!, Name = target.ServiceName, Tenant = target.TenantId, Namespace = target.Namespace },
            FolderId = target.FolderId,
            PublicName = target.PublicName
        };
        return MetadataV2PathReservations.Reconcile(graph, candidate with { Services = [.. graph.Services, service] });
    }

    private static void ValidateTargetPath(MetadataV2Graph graph, GeoservicesImportTarget target)
    {
        try
        {
            target.ValidateServiceIdentity(graph);
            AddPlannedTarget(graph, target);
        }
        catch (InvalidOperationException)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                "The target folder or public service path is reserved. Select an explicit mapping.");
        }
    }

    private static LayerPublishingException InvalidMapping(string message) => new(LayerPublishingErrorKind.Validation, message);
}
