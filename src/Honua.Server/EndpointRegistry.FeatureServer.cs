// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> FeatureServerEndpoints =>
    [
        new("GET", "/rest/services/{serviceId}/FeatureServer"),
        new("POST", "/rest/services/{serviceId}/FeatureServer"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/query"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/query"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/query"),
        // Esri clients POST large layerDefs/layers arrays that exceed URL limits, so the
        // service-level query operation accepts both GET and POST (#1825).
        new("POST", "/rest/services/{serviceId}/FeatureServer/query"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/applyEdits"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/createReplica"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/extractChanges"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/synchronizeReplica"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/unRegisterReplica"),

        // GeoServices VersionManagementServer — Esri-style branch versioning (#1272, ADR-0051).
        new("GET", "/rest/services/{serviceId}/VersionManagementServer"),
        new("GET", "/rest/services/{serviceId}/VersionManagementServer/versions"),
        new("GET", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/create"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/delete"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/alter"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startReading"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/stopReading"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startEditing"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/stopEditing"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile"),
        new("GET", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/inspectConflicts"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/resolveConflicts"),
        new("POST", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/post"),
        new("GET", "/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/jobs/{jobId}"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/append"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/append"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/calculate"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/calculate"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/queryDomains"),
        // queryDomains also accepts POST so clients can submit large layers arrays (#1825).
        new("POST", "/rest/services/{serviceId}/FeatureServer/queryDomains"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/relationships"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/validateSQL"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/validateSQL"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/getEstimates"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/getEstimates"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryBins"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryBins"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryH3"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryH3"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity"),

        // Data enrichment API (#374): catalog of registered enrichment datasets
        // plus a spatial-join enrichment facade over the shared analytics pipeline.
        new("GET", "/api/enrich/catalog"),
        new("POST", "/api/enrich"),

        // Spec-shaped "not implemented" FeatureServer operations (#1402).
        new("GET", "/rest/services/{serviceId}/FeatureServer/queryContingentValues"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/sharedTemplates"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/sharedTemplates/query"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/sharedTemplates/add"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/sharedTemplates/update"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/sharedTemplates/delete"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/htmlPopup"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/image"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/hasAssets"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryAssets"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/cleanupAssets"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/uploadAssets"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/convert3D"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/query3D"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/metadata/update"),

    ];

    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> FeatureServerAttachmentEndpoints =>
    [
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/replicas"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/replicas/{replicaId}"),

    ];
}
