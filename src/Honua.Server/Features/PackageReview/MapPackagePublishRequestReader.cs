// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Validation.Contracts;

namespace Honua.PackageReview;

/// <summary>
/// Reads a map package publish request the way the installed clients send it (honua-server#4906):
/// a <c>honua_map_package.v1</c> document whose <c>status</c> and <c>createdAt</c> are optional,
/// with every refusal reported as a field-level error naming the JSON member.
/// </summary>
internal static class MapPackagePublishRequestReader
{
    private const string PackageMember = "package";

    internal sealed record Result
    {
        public PackageReviewEndpoints.MapPackagePublishRequest? Request { get; init; }

        /// <summary>True when <c>package</c> or <c>package.mapPackageId</c> is absent.</summary>
        public bool MissingPackage { get; init; }

        public IReadOnlyList<FieldValidationError> Errors { get; init; } = [];
    }

    public static async Task<Result> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        JsonNode? root;
        try
        {
            root = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return Refused(ex.Path ?? "$", "invalid_json", "The request body is not valid JSON.");
        }

        if (root is not JsonObject body)
        {
            return Refused("$", "invalid_type", "The request body must be a JSON object.");
        }

        if (!body.TryGetPropertyValue(PackageMember, out var packageNode) || packageNode is null)
        {
            return new Result { MissingPackage = true };
        }

        if (packageNode is not JsonObject package)
        {
            return Refused("$.package", "invalid_type", "'package' must be a honua_map_package.v1 object.");
        }

        if (package["mapPackageId"] is not JsonValue mapPackageId
            || !mapPackageId.TryGetValue<string>(out var mapPackageIdText)
            || string.IsNullOrWhiteSpace(mapPackageIdText))
        {
            return new Result { MissingPackage = true };
        }

        // honua_map_package.v1 leaves the lifecycle members to the server: an unsaved package is
        // a draft created now.
        if (package["status"] is null)
        {
            package["status"] = nameof(Honua.Core.Features.Geoprocessing.Domain.PackageStatus.Draft);
        }

        if (package["createdAt"] is null)
        {
            package["createdAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        try
        {
            // The package-review context carries the package enum converters that the combined
            // HTTP resolver does not apply to nested members.
            var parsed = body.Deserialize(PackageReviewJsonContext.Default.MapPackagePublishRequest);
            return parsed is null
                ? Refused("$", "invalid_type", "The request body must be a JSON object.")
                : new Result { Request = parsed };
        }
        catch (JsonException ex)
        {
            return Refused(ex.Path ?? "$.package", "invalid_member", DescribeFailure(ex));
        }
    }

    // System.Text.Json messages name CLR types; only the member names they list reach the client.
    private static string DescribeFailure(JsonException exception)
    {
        const string MissingMarker = "missing required properties including: ";
        var message = exception.Message;
        var missingAt = message.IndexOf(MissingMarker, StringComparison.Ordinal);
        if (missingAt >= 0)
        {
            var members = message[(missingAt + MissingMarker.Length)..].TrimEnd('.', ' ');
            return $"Required members are missing: {members}.";
        }

        return "The value does not match the honua_map_package.v1 schema.";
    }

    private static Result Refused(string path, string code, string message) => new()
    {
        Errors =
        [
            new FieldValidationError
            {
                Code = code,
                Severity = ValidationSeverity.Error,
                Path = path,
                Message = message
            }
        ]
    };
}
