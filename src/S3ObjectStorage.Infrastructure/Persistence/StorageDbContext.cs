using Microsoft.EntityFrameworkCore;
using S3ObjectStorage.Domain.Entities;

namespace S3ObjectStorage.Infrastructure.Persistence;

public sealed class StorageDbContext : DbContext
{
    public StorageDbContext(DbContextOptions<StorageDbContext> options) : base(options)
    {
    }

    public DbSet<BucketEntity> Buckets => Set<BucketEntity>();

    public DbSet<ObjectEntity> Objects => Set<ObjectEntity>();

    public DbSet<MultipartUploadEntity> MultipartUploads => Set<MultipartUploadEntity>();

    public DbSet<UploadedPartEntity> UploadedParts => Set<UploadedPartEntity>();

    public DbSet<AccessKeyEntity> AccessKeys => Set<AccessKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BucketEntity>(b =>
        {
            b.ToTable("Buckets");
            b.HasKey(x => x.Name);
            b.Property(x => x.Name).HasMaxLength(255);
            b.Property(x => x.OwnerId).HasMaxLength(128);
            b.Property(x => x.OwnerDisplayName).HasMaxLength(255);
            b.Property(x => x.Region).HasMaxLength(64);
            b.HasIndex(x => x.OwnerId);
        });

        modelBuilder.Entity<ObjectEntity>(o =>
        {
            o.ToTable("Objects");
            o.HasKey(x => x.Id);
            o.Property(x => x.BucketName).HasMaxLength(255).IsRequired();
            o.Property(x => x.Key).HasMaxLength(1024).IsRequired();
            o.Property(x => x.ETag).HasMaxLength(128).IsRequired();
            o.Property(x => x.ContentType).HasMaxLength(255);
            o.Property(x => x.BlobPath).HasMaxLength(1024).IsRequired();
            o.Property(x => x.StorageClass).HasMaxLength(32);
            // (BucketName, Key) 必须唯一，这是对象存储的天然主键。
            o.HasIndex(x => new { x.BucketName, x.Key }).IsUnique();
            o.HasIndex(x => new { x.BucketName, x.Size });
        });

        modelBuilder.Entity<MultipartUploadEntity>(m =>
        {
            m.ToTable("MultipartUploads");
            m.HasKey(x => x.UploadId);
            m.Property(x => x.UploadId).HasMaxLength(128);
            m.Property(x => x.BucketName).HasMaxLength(255).IsRequired();
            m.Property(x => x.Key).HasMaxLength(1024).IsRequired();
            m.HasIndex(x => new { x.BucketName, x.Key });
            m.HasMany(x => x.Parts)
                .WithOne(p => p.Upload)
                .HasForeignKey(p => p.UploadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UploadedPartEntity>(p =>
        {
            p.ToTable("UploadedParts");
            p.HasKey(x => x.Id);
            p.Property(x => x.Id).ValueGeneratedOnAdd();
            p.Property(x => x.UploadId).HasMaxLength(128).IsRequired();
            p.Property(x => x.ETag).HasMaxLength(64).IsRequired();
            p.Property(x => x.BlobPath).HasMaxLength(1024).IsRequired();
            p.HasIndex(x => new { x.UploadId, x.PartNumber }).IsUnique();
        });

        modelBuilder.Entity<AccessKeyEntity>(k =>
        {
            k.ToTable("AccessKeys");
            k.HasKey(x => x.AccessKeyId);
            k.Property(x => x.AccessKeyId).HasMaxLength(128);
            k.Property(x => x.SecretAccessKey).HasMaxLength(256);
            k.Property(x => x.DisplayName).HasMaxLength(255);
        });
    }
}
