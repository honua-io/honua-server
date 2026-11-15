// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private static string RelatedTokenBinding(Proto.QueryRelatedRecordsRequest request, GrpcLayerContext layer,
        ServerCallContext context)
    {
        var canonical = request.Clone();
        canonical.PageToken = string.Empty;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            var bytes = canonical.ToByteArray();
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Write(layer.Snapshot!.Revision);
            writer.Write(layer.Resource.Metadata.Id);
            var http = context.GetHttpContext();
            writer.Write(http.RequestServices.GetService<ITenantContext>()?.TenantId ?? string.Empty);
            var identities = http.User.Identities.OrderBy(identity => identity.AuthenticationType, StringComparer.Ordinal).ToArray();
            writer.Write(identities.Length);
            foreach (var identity in identities)
            {
                WriteRelatedIdentity(writer, identity);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteRelatedIdentity(BinaryWriter writer, ClaimsIdentity identity)
    {
        writer.Write(identity.AuthenticationType ?? string.Empty);
        var claims = identity.Claims.OrderBy(claim => claim.Type, StringComparer.Ordinal)
            .ThenBy(claim => claim.Value, StringComparer.Ordinal).ThenBy(claim => claim.Issuer, StringComparer.Ordinal).ToArray();
        writer.Write(claims.Length);
        foreach (var claim in claims)
        {
            writer.Write(claim.Type);
            writer.Write(claim.Value);
            writer.Write(claim.Issuer);
        }
    }

    private static Proto.QueryRelatedRecordsResponse BuildRelatedResponse(RelatedReadPlan plan,
        RelatedFeaturePage page, GrpcRelatedPageTokens tokens)
    {
        var request = plan.Request;
        var related = plan.Related;
        var query = plan.Query;
        var fields = plan.Fields;
        var selected = fields.Where(field => request.OutFields.Count == 0 || request.OutFields.Contains("*") ||
            request.OutFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var names = selected.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var response = new Proto.QueryRelatedRecordsResponse
        {
            RelatedLayerId = plan.Relationship.RelatedLayerId,
            ObjectIdFieldName = related.ObjectIdFieldName,
            GlobalIdFieldName = query.GlobalIdField ?? string.Empty,
            GeometryType = GrpcConversionHelpers.ToProtoGeometryType(related.GeometryType),
            SpatialReference = GrpcConversionHelpers.ToProtoSpatialReference(query.ResponseSpatialReference)
        };
        response.Fields.AddRange(selected.Select(GrpcConversionHelpers.ToProtoField));
        response.Groups.AddRange(page.Positions.GroupBy(position => position.OriginId)
            .Select(group => BuildRelatedGroup(plan, group, names)));
        if (page.HasMore)
        {
            if (page.Positions.IsDefaultOrEmpty)
            {
                throw new RpcException(new Status(StatusCode.Internal, "The provider returned an invalid related page."));
            }
            var last = page.Positions[^1];
            response.NextPageToken = tokens.Encode(
                new RelatedPageCursor(last.OriginId, last.RelatedFeature?.Id), DateTimeOffset.UtcNow);
        }
        return response;
    }
    private static Proto.RelatedRecordGroup BuildRelatedGroup(RelatedReadPlan plan,
        IGrouping<long, RelatedFeaturePosition> positions, HashSet<string> selectedNames)
    {
        var output = new Proto.RelatedRecordGroup { ObjectId = positions.Key };
        foreach (var position in positions)
        {
            if (position.RelatedFeature is not { } feature)
            {
                continue;
            }
            var projected = feature with
            {
                Attributes = feature.Attributes.Where(pair => selectedNames.Contains(pair.Key)).ToImmutableDictionary()
            };
            var query = plan.Query;
            output.Features.Add(WithGlobalId(GrpcConversionHelpers.ToProtoFeature(projected, query.ReturnGeometry,
                query.GeometryLimits, plan.Related.ObjectIdFieldName, query.AttributeHiddenFields,
                plan.Related.AttributeFields), feature, query.GlobalIdField));
        }
        return output;
    }

}
