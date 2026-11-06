// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public static class OgcApiFeaturesImportCompatibilityCodes
{
    public const string CollectionSource = "OGC_API_FEATURES_COLLECTION_SOURCE";

    public const string ManualReview = "OGC_API_FEATURES_MANUAL_REVIEW";

    public const string MissingItemsEndpoint = "OGC_API_FEATURES_ITEMS_ENDPOINT_MISSING";

    public const string NonJsonItemsEncoding = "OGC_API_FEATURES_NON_JSON_ITEMS_ENCODING";

    public const string TransactionsManualReview = "OGC_API_FEATURES_TRANSACTIONS_MANUAL_REVIEW";

    public const string VendorExtensionManualReview = "OGC_API_FEATURES_VENDOR_EXTENSION_MANUAL_REVIEW";
}

