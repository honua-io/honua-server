// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Constants;

/// <summary>
/// Operation identifiers for test trait attributes.
/// Used with <see cref="Attributes.OperationAttribute"/> for test categorization.
/// </summary>
public static class Operations
{
    // Common Operations
    public const string Query = "Query";
    public const string Consume = "Consume";
    public const string GetById = "GetById";
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string BulkCreate = "BulkCreate";
    public const string BulkUpdate = "BulkUpdate";
    public const string BulkDelete = "BulkDelete";
    public const string ApplyEdits = "ApplyEdits";
    public const string QueryRelatedRecords = "QueryRelatedRecords";

    // Spatial Operations
    public const string SpatialQuery = "SpatialQuery";
    public const string BufferQuery = "BufferQuery";
    public const string NearestQuery = "NearestQuery";
    public const string Directions = "Directions";
    public const string OptimizeRoute = "OptimizeRoute";
    public const string ServiceArea = "ServiceArea";
    public const string ClosestFacility = "ClosestFacility";

    // Geometry Service Operations
    public const string Buffer = "Buffer";
    public const string Simplify = "Simplify";
    public const string Project = "Project";
    public const string Intersect = "Intersect";
    public const string Union = "Union";
    public const string Clip = "Clip";
    public const string Difference = "Difference";
    public const string Area = "Area";
    public const string Length = "Length";
    public const string Distance = "Distance";
    public const string Relation = "Relation";
    public const string Densify = "Densify";
    public const string ConvexHull = "ConvexHull";
    public const string Generalize = "Generalize";
    public const string LabelPoints = "LabelPoints";
    public const string Cut = "Cut";
    public const string TrimExtend = "TrimExtend";
    public const string Offset = "Offset";
    public const string AutoComplete = "AutoComplete";
    public const string Reshape = "Reshape";
    public const string FindTransformations = "FindTransformations";

    // Metadata Operations
    public const string GetMetadata = "GetMetadata";
    public const string GetLayerInfo = "GetLayerInfo";
    public const string GetServiceInfo = "GetServiceInfo";
    public const string Metadata = "Metadata";

    // Admin Operations
    public const string TableDiscovery = "TableDiscovery";
    public const string Configuration = "Configuration";
    public const string Validation = "Validation";
    public const string Cache = "Cache";
    public const string Infrastructure = "Infrastructure";
    public const string OperationsProgress = "OperationsProgress";
    public const string LicenseManagement = "LicenseManagement";
    public const string IdentityManagement = "IdentityManagement";
    public const string RoleManagement = "RoleManagement";
    public const string ApiKeyManagement = "ApiKeyManagement";
    public const string RateLimitManagement = "RateLimitManagement";
    public const string License = "License";
    public const string Identity = "Identity";
    public const string Features = "Features";
    public const string GeocodingAdmin = "GeocodingAdmin";

    // Import Operations
    public const string Import = "Import";

    // Health Operations
    public const string HealthCheck = "HealthCheck";
    public const string LivenessCheck = "LivenessCheck";
    public const string ReadinessCheck = "ReadinessCheck";

    // Content Operations
    public const string ContentNegotiation = "ContentNegotiation";

    // Studio Operations
    public const string StudioLifecycle = "StudioLifecycle";

    // Error Handling
    public const string ErrorHandling = "ErrorHandling";

    // Security Operations
    public const string Security = "Security";

    // Pagination Operations
    public const string Pagination = "Pagination";

    // Performance Operations
    public const string Performance = "Performance";

    // Tile Operations
    public const string GetTile = "GetTile";
    public const string GetTileMetadata = "GetTileMetadata";

    // Image / Map Operations
    public const string Export = "Export";
    public const string Print = "Print";
    public const string Identify = "Identify";
    public const string Render = "Render";
    public const string Tile = "Tile";
    public const string Wms = "WMS";
    public const string Wmts = "WMTS";

    // Filter Operations
    public const string WhereClause = "WhereClause";
    public const string CqlFilter = "CqlFilter";
    public const string ODataFilter = "ODataFilter";

    // OData v4 Advanced Operations
    public const string ODataBatch = "ODataBatch";
    public const string ODataApply = "ODataApply";
    public const string ODataSearch = "ODataSearch";
    public const string ODataExpand = "ODataExpand";

    // Attachment Operations
    public const string QueryAttachments = "QueryAttachments";
    public const string AddAttachment = "AddAttachment";
    public const string UpdateAttachment = "UpdateAttachment";
    public const string DeleteAttachments = "DeleteAttachments";
    public const string DownloadAttachment = "DownloadAttachment";

    // Replication Operations
    public const string ListReplicas = "ListReplicas";
    public const string ReplicaInfo = "ReplicaInfo";
    public const string CreateReplica = "CreateReplica";
    public const string ExtractChanges = "ExtractChanges";
    public const string SynchronizeReplica = "SynchronizeReplica";
    public const string UnRegisterReplica = "UnRegisterReplica";

    // Maintenance Operations
    public const string Append = "Append";
    public const string Calculate = "Calculate";
    public const string QueryDomains = "QueryDomains";
    public const string QueryRelationships = "QueryRelationships";
    public const string ValidateSql = "ValidateSql";
    public const string GetEstimates = "GetEstimates";
    public const string QueryTopFeatures = "QueryTopFeatures";
    public const string QueryDateBins = "QueryDateBins";
    public const string QueryBins = "QueryBins";
    public const string QueryH3 = "QueryH3";

    // Spatial Analytics Operations (Pro tier)
    public const string QueryClusters = "QueryClusters";
    public const string SpatialJoin = "SpatialJoin";
    public const string QueryBufferAggregate = "QueryBufferAggregate";
    public const string QueryDensity = "QueryDensity";

    // OGC API Processes Operations
    public const string ProcessDiscovery = "ProcessDiscovery";
    public const string ProcessExecution = "ProcessExecution";
    public const string JobStatus = "JobStatus";
    public const string JobResults = "JobResults";
    public const string JobDismiss = "JobDismiss";

    // Streaming Operations
    public const string Streaming = "Streaming";

    // COG Operations
    public const string CogAdmin = "CogAdmin";

    // Zarr Operations
    public const string ZarrAdmin = "ZarrAdmin";

    // STAC Operations
    public const string StacCatalog = "StacCatalog";
    public const string StacSearch = "StacSearch";

    // Grounding Operations
    public const string GroundingMutate = "GroundingMutate";
    public const string GroundingSummarize = "GroundingSummarize";

    // Test Quality Operations
    public const string TestQuality = "TestQuality";
    public const string FuzzTesting = "FuzzTesting";
    public const string SecurityTesting = "SecurityTesting";
    public const string ChaosTesting = "ChaosTesting";
    public const string ContractTesting = "ContractTesting";
    public const string PerformanceTesting = "PerformanceTesting";
    public const string TestInfrastructure = "TestInfrastructure";
}
