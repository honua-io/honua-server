// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OgcFeatures.Models;
using FeatureServerGeoJsonFeature = Honua.Server.Features.FeatureServer.Models.GeoJsonFeature;

namespace Honua.Server.Features.Shared.Examples;

/// <summary>
/// Demonstration of how shared models can reduce duplication across protocol implementations
/// This file shows the "before" and "after" approach for handling common patterns
/// </summary>
internal static class SharedModelUsageExample
{
    /// <summary>
    /// Example: Converting a core feature to different protocol representations
    /// BEFORE: Each protocol had its own conversion logic with duplicated patterns
    /// AFTER: Shared base models enable consistent conversions with protocol-specific serialization
    /// </summary>
    public static class FeatureConversions
    {
        /// <summary>
        /// Convert core Feature to FeatureServer GeoJSON representation
        /// Uses shared GeoJsonFeatureBase as intermediate step for consistency
        /// </summary>
        public static FeatureServerGeoJsonFeature ToFeatureServerGeoJson(Feature coreFeature, GeoJsonGeometry? geometry = null)
        {
            var sharedBase = coreFeature.ToGeoJsonBase();
            return sharedBase.ToGeoJsonFeature(geometry);
        }

        /// <summary>
        /// Convert core Feature to OGC API Features representation
        /// Uses the same shared base but converts to OGC-specific format
        /// </summary>
        public static Honua.Server.Features.OgcFeatures.Models.GeoJsonFeature ToOgcGeoJson(
            Feature coreFeature,
            SimpleGeoJsonGeometry? geometry = null,
            ImmutableArray<Link>? links = null)
        {
            var sharedBase = coreFeature.ToGeoJsonBase();
            return sharedBase.ToOgcGeoJsonFeature(geometry, links);
        }

        /// <summary>
        /// Convert core Feature to OData representation
        /// Uses shared base with OData-specific context
        /// </summary>
        public static ODataFeatureResponse ToODataFeature(
            Feature coreFeature,
            string context,
            int layerId,
            object? geometry = null)
        {
            var sharedBase = coreFeature.ToGeoJsonBase();
            return sharedBase.ToODataFeatureResponse(context, layerId, geometry);
        }
    }

    /// <summary>
    /// Example: Handling spatial reference information consistently
    /// BEFORE: Each protocol had different SpatialReference classes with similar properties
    /// AFTER: Shared SpatialReference struct with protocol-specific conversion extensions
    /// </summary>
    public static class SpatialReferenceConversions
    {
        /// <summary>
        /// Convert from any protocol representation to shared format and then to any other protocol
        /// This eliminates the need for N×N conversion methods between protocols
        /// </summary>
        public static void DemonstrateConversions()
        {
            // Start with a shared spatial reference
            var sharedSpatialRef = SpatialReference.Create(4326, 4979);

            // Convert to protocol-specific formats
            var featureServerInfo = sharedSpatialRef.ToSpatialReferenceInfo();
            var geoServicesSpatialRef = sharedSpatialRef.ToGeoServicesSpatialReference();
            var ogcCrsString = sharedSpatialRef.ToOgcCrs();

            // Convert back to shared format from any protocol
            var backFromFeatureServer = featureServerInfo.ToSpatialReference();
            var backFromGeoServices = geoServicesSpatialRef.ToSpatialReference();
            var backFromOgc = ogcCrsString.ToSpatialReference();

            // All should be equivalent
            System.Diagnostics.Debug.Assert(backFromFeatureServer.Wkid == sharedSpatialRef.Wkid);
            System.Diagnostics.Debug.Assert(backFromGeoServices.Wkid == sharedSpatialRef.Wkid);
            System.Diagnostics.Debug.Assert(backFromOgc.Wkid == sharedSpatialRef.Wkid);
        }
    }

    /// <summary>
    /// Example: Error handling with shared ServiceError structure
    /// BEFORE: Each protocol had different error classes (EditError, ErrorDetail, etc.)
    /// AFTER: Shared ServiceError with protocol-specific conversion extensions
    /// </summary>
    public static class ErrorHandlingConversions
    {
        /// <summary>
        /// Demonstrate consistent error creation and protocol-specific formatting
        /// </summary>
        public static void DemonstrateErrorHandling()
        {
            // Create shared error
            var validationError = ModelConversions.CreateValidationError("Invalid geometry", "geometry");

            // Convert to protocol-specific formats
            var featureServerError = validationError.ToEditError();
            var oDataError = validationError.ToODataError();
            // OGC uses standard HTTP error responses, so no specific conversion needed

            // All maintain the core error information but in protocol-appropriate format
            System.Diagnostics.Debug.Assert(featureServerError.Code == validationError.ToNumericErrorCode());
            System.Diagnostics.Debug.Assert(oDataError.Error.Code == validationError.Code);
        }
    }

    /// <summary>
    /// Example: Extent/Bounding box handling
    /// BEFORE: ExtentInfo (FeatureServer) and SpatialExtent (OGC) had different representations
    /// AFTER: Core FeatureExtent with conversion extensions for different formats
    /// </summary>
    public static class ExtentConversions
    {
        /// <summary>
        /// Demonstrate consistent extent handling across protocols
        /// </summary>
        public static void DemonstrateExtentHandling()
        {
            // Start with core extent
            var coreExtent = FeatureExtent.Create(-180.0, -90.0, 180.0, 90.0, 4326);

            // Convert to protocol-specific formats
            var featureServerExtent = coreExtent.ToExtentInfo();
            var ogcSpatialExtent = coreExtent.ToSpatialExtent();
            var boundingBox = coreExtent.ToBoundingBox();

            // Convert back to verify consistency
            var backFromFeatureServer = featureServerExtent.ToFeatureExtent();
            var backFromOgc = ogcSpatialExtent.ToFeatureExtent();
            var backFromBbox = boundingBox.ToFeatureExtent(4326);

            // All should be equivalent
            System.Diagnostics.Debug.Assert(backFromFeatureServer.MinX == coreExtent.MinX);
            System.Diagnostics.Debug.Assert(backFromOgc.MaxY == coreExtent.MaxY);
            System.Diagnostics.Debug.Assert(backFromBbox.SpatialReference == coreExtent.SpatialReference);
        }
    }

    /// <summary>
    /// Example: Pagination response handling
    /// BEFORE: Each protocol had different count/paging properties
    /// AFTER: Shared PagedResponseBase with protocol-specific property mapping
    /// </summary>
    public static class PaginationConversions
    {
        /// <summary>
        /// Demonstrate consistent pagination across protocols
        /// </summary>
        public static void DemonstratePaginationHandling()
        {
            // Create shared pagination info
            var pagedResponse = PagedResponseBase.Create(10, 100, true);

            // Convert to protocol-specific properties
            var (count, exceededLimit) = pagedResponse.ToQueryResponseProperties(); // FeatureServer
            var (numberMatched, numberReturned) = pagedResponse.ToFeatureCollectionProperties(); // OGC
            var (context, oDataCount, nextLink) = pagedResponse.ToODataResponseProperties("$metadata#Features"); // OData

            // All contain the same core information
            System.Diagnostics.Debug.Assert(count == pagedResponse.TotalCount);
            System.Diagnostics.Debug.Assert(numberMatched == pagedResponse.TotalCount);
            System.Diagnostics.Debug.Assert(oDataCount == pagedResponse.TotalCount);
            System.Diagnostics.Debug.Assert(numberReturned == pagedResponse.ReturnedCount);
        }
    }
}

/// <summary>
/// Benefits achieved by this refactoring:
///
/// 1. REDUCED DUPLICATION:
///    - Eliminated duplicate spatial reference classes (3 → 1 shared)
///    - Eliminated duplicate error classes (3 → 1 shared)
///    - Eliminated duplicate extent classes (2 → 1 shared)
///    - Eliminated duplicate feature base patterns (3 → 1 shared)
///
/// 2. IMPROVED CONSISTENCY:
///    - All protocols now use the same core logic for common operations
///    - Consistent error codes and messages across protocols
///    - Consistent coordinate system handling
///    - Consistent pagination semantics
///
/// 3. MAINTAINABILITY:
///    - Single place to fix bugs in common logic
///    - Single place to add new features to common patterns
///    - Easier to ensure protocol compatibility
///    - Clear separation of shared vs protocol-specific concerns
///
/// 4. TESTABILITY:
///    - Shared logic can be tested once in core tests
///    - Protocol-specific tests focus only on serialization differences
///    - Better test coverage with less duplication
///
/// 5. AOT COMPATIBILITY PRESERVED:
///    - Each protocol maintains its own JSON serialization context
///    - No reflection in shared models (all value types with explicit constructors)
///    - Source generation continues to work as before
///    - Only conversion logic is shared, not serialization format
/// </summary>
internal static class RefactoringBenefits { }
