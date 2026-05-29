// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Stable compatibility codes emitted by the OGC API Features migration inventory scanner.
/// </summary>
public static class OgcApiFeaturesImportCompatibilityCodes
{
    /// <summary>OGC API Features collection can feed the automated feature import path.</summary>
    public const string CollectionSource = "OGC_API_FEATURES_COLLECTION_SOURCE";

    /// <summary>OGC API Features collection is discoverable but needs operator review before import.</summary>
    public const string ManualReview = "OGC_API_FEATURES_MANUAL_REVIEW";

    /// <summary>OGC API Features collection does not advertise an items endpoint.</summary>
    public const string MissingItemsEndpoint = "OGC_API_FEATURES_ITEMS_ENDPOINT_MISSING";

    /// <summary>OGC API Features collection items are not advertised with a JSON representation.</summary>
    public const string NonJsonItemsEncoding = "OGC_API_FEATURES_NON_JSON_ITEMS_ENCODING";

    /// <summary>OGC API Features conformance class describes source-side transactions that are not migrated.</summary>
    public const string TransactionsManualReview = "OGC_API_FEATURES_TRANSACTIONS_MANUAL_REVIEW";

    /// <summary>OGC API Features source advertises a vendor or non-standard extension requiring review.</summary>
    public const string VendorExtensionManualReview = "OGC_API_FEATURES_VENDOR_EXTENSION_MANUAL_REVIEW";
}

