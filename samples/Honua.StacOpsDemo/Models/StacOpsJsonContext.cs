using System.Text.Json.Serialization;

namespace Honua.StacOpsDemo.Models;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StacCatalogDocument))]
[JsonSerializable(typeof(StacCollectionsResponseDocument))]
[JsonSerializable(typeof(StacCollectionDocument))]
[JsonSerializable(typeof(StacExtentDocument))]
[JsonSerializable(typeof(StacTemporalExtentDocument))]
[JsonSerializable(typeof(StacItemCollectionDocument))]
[JsonSerializable(typeof(StacItemDocument))]
[JsonSerializable(typeof(StacLinkDocument))]
internal sealed partial class StacOpsJsonContext : JsonSerializerContext
{
}
