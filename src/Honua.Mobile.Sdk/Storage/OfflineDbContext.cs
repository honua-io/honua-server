// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Converters;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Storage;

/// <summary>
/// Entity Framework database context for offline mobile storage.
/// </summary>
public class OfflineDbContext : DbContext
{
    private readonly HonuaMobileClientOptions _options;

    /// <summary>
    /// Initializes a new instance of the OfflineDbContext.
    /// </summary>
    /// <param name="options">Database options</param>
    /// <param name="clientOptions">Mobile client options</param>
    public OfflineDbContext(
        DbContextOptions<OfflineDbContext> options,
        IOptions<HonuaMobileClientOptions> clientOptions) : base(options)
    {
        _options = clientOptions?.Value ?? throw new ArgumentNullException(nameof(clientOptions));
    }

    /// <summary>
    /// Cached features for offline use.
    /// </summary>
    public DbSet<CachedFeatureEntity> CachedFeatures { get; set; } = null!;

    /// <summary>
    /// Pending edit operations waiting for sync.
    /// </summary>
    public DbSet<PendingEditEntity> PendingEdits { get; set; } = null!;

    /// <summary>
    /// Configures the database model.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure CachedFeatureEntity
        modelBuilder.Entity<CachedFeatureEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ServiceId, e.LayerId, e.ObjectId }).IsUnique();
            entity.HasIndex(e => e.CachedAt);
            entity.Property(e => e.ServiceId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AttributesJson).HasColumnType("TEXT");
            entity.Property(e => e.GeometryWkt).HasColumnType("TEXT");
        });

        // Configure PendingEditEntity
        modelBuilder.Entity<PendingEditEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ServiceId, e.LayerId, e.IsSynced });
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.ServiceId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.OperationType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FeatureData).HasColumnType("TEXT");
        });
    }

    /// <summary>
    /// Configures the database connection.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var databasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                _options.OfflineDatabase);

            optionsBuilder.UseSqlite($"Data Source={databasePath}");
        }
    }
}

/// <summary>
/// Entity representing a cached feature for offline use.
/// </summary>
[Table("CachedFeatures")]
public class CachedFeatureEntity
{
    /// <summary>
    /// Primary key.
    /// </summary>
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Service identifier.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Layer identifier.
    /// </summary>
    public int LayerId { get; set; }

    /// <summary>
    /// Feature object ID.
    /// </summary>
    public long ObjectId { get; set; }

    /// <summary>
    /// Feature attributes as JSON.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// Feature geometry as Well-Known Text.
    /// </summary>
    public string? GeometryWkt { get; set; }

    /// <summary>
    /// When the feature was cached.
    /// </summary>
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last time the feature was accessed.
    /// </summary>
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Creates a cached feature entity from a domain feature.
    /// </summary>
    /// <param name="feature">Domain feature</param>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <returns>Cached feature entity</returns>
    public static CachedFeatureEntity FromDomainFeature(DomainFeature feature, string serviceId, int layerId)
    {
        return new CachedFeatureEntity
        {
            ServiceId = serviceId,
            LayerId = layerId,
            ObjectId = feature.Id,
            AttributesJson = System.Text.Json.JsonSerializer.Serialize(feature.Attributes),
            GeometryWkt = feature.Geometry is { Length: > 0 }
                ? GeometryConverter.FromWkb(feature.Geometry).AsText()
                : null,
            CachedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Converts the cached entity back to a domain feature.
    /// </summary>
    /// <returns>Domain feature</returns>
    public DomainFeature ToDomainFeature()
    {
        var attributes = !string.IsNullOrEmpty(AttributesJson)
            ? (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(AttributesJson)
                ?? new Dictionary<string, object?>())
                .ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value)
            : ImmutableDictionary<string, object?>.Empty;

        Geometry? geometry = null;
        if (!string.IsNullOrEmpty(GeometryWkt))
        {
            try
            {
                var geometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
                var wktReader = new NetTopologySuite.IO.WKTReader(geometryFactory);
                geometry = wktReader.Read(GeometryWkt);
            }
            catch
            {
                // Ignore geometry parsing errors for now
            }
        }

        var geometryWkb = geometry is not null ? GeometryConverter.ToWkb(geometry) : null;

        return new DomainFeature
        {
            Id = ObjectId,
            Attributes = attributes,
            Geometry = geometryWkb
        };
    }

    /// <summary>
    /// Updates this entity from another cached feature.
    /// </summary>
    /// <param name="other">Other cached feature</param>
    public void UpdateFromDomainFeature(CachedFeatureEntity other)
    {
        AttributesJson = other.AttributesJson;
        GeometryWkt = other.GeometryWkt;
        LastAccessedAt = DateTime.UtcNow;
        // Keep original CachedAt timestamp
    }
}

/// <summary>
/// Entity representing a pending edit operation.
/// </summary>
[Table("PendingEdits")]
public class PendingEditEntity
{
    /// <summary>
    /// Primary key.
    /// </summary>
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// Service identifier.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Layer identifier.
    /// </summary>
    public int LayerId { get; set; }

    /// <summary>
    /// Type of operation (Add, Update, Delete).
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string OperationType { get; set; } = string.Empty;

    /// <summary>
    /// Original object ID for updates and deletes.
    /// </summary>
    public long? OriginalObjectId { get; set; }

    /// <summary>
    /// Serialized feature data.
    /// </summary>
    public string? FeatureData { get; set; }

    /// <summary>
    /// When the edit was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the edit has been synced to the server.
    /// </summary>
    public bool IsSynced { get; set; } = false;

    /// <summary>
    /// When the edit was synced (if applicable).
    /// </summary>
    public DateTime? SyncedAt { get; set; }

    /// <summary>
    /// Number of sync attempts.
    /// </summary>
    public int SyncAttempts { get; set; } = 0;

    /// <summary>
    /// Last sync error message.
    /// </summary>
    public string? LastSyncError { get; set; }
}
