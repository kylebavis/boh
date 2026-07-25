using Boh.Web.Data.Entities;
using Boh.Web.Tags;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Boh.Web.Data;

public class BohDbContext(DbContextOptions<BohDbContext> options) : DbContext(options)
{
    /// <summary>
    /// SQLite cannot ORDER BY a DateTimeOffset — the provider rejects it outright, which
    /// would break the gallery's "newest first" query. Storing Unix milliseconds gives an
    /// INTEGER column that sorts and indexes natively. No information is lost because every
    /// timestamp we write is UTC.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, long> UtcMilliseconds = new(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PostTag> PostTags => Set<PostTag>();
    public DbSet<TagAlias> TagAliases => Set<TagAlias>();
    public DbSet<TagNamespace> TagNamespaces => Set<TagNamespace>();
    public DbSet<TagImplication> TagImplications => Set<TagImplication>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Post>(e =>
        {
            e.Property(p => p.Sha256).HasMaxLength(64).IsRequired();
            e.HasIndex(p => p.Sha256).IsUnique();

            e.Property(p => p.UploadedAt).HasConversion(UtcMilliseconds);
            e.HasIndex(p => p.UploadedAt).IsDescending();

            e.Property(p => p.FileExtension).HasMaxLength(16).IsRequired();
            e.Property(p => p.MimeType).HasMaxLength(128).IsRequired();
            e.Property(p => p.SourceUrl).HasMaxLength(2048);
            e.Property(p => p.Description).HasMaxLength(8192);

            e.HasOne(p => p.UploadedBy)
                .WithMany()
                .HasForeignKey(p => p.UploadedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Tag>(e =>
        {
            e.Property(t => t.Namespace).HasMaxLength(32).IsRequired();
            e.Property(t => t.Name).HasMaxLength(128).IsRequired();
            e.HasIndex(t => new { t.Namespace, t.Name }).IsUnique();
            e.HasIndex(t => t.PostCount).IsDescending();

            // Computed display form; not a stored column.
            e.Ignore(t => t.Display);
        });

        // Many-to-many with a payload. The skip navigations (Post.Tags / Tag.Posts) exist so
        // search reads naturally; writes always go through PostTag directly so Source is set.
        b.Entity<Post>()
            .HasMany(p => p.Tags)
            .WithMany(t => t.Posts)
            .UsingEntity<PostTag>(
                j => j.HasOne(pt => pt.Tag)
                      .WithMany(t => t.PostTags)
                      .HasForeignKey(pt => pt.TagId)
                      .OnDelete(DeleteBehavior.Cascade),
                j => j.HasOne(pt => pt.Post)
                      .WithMany(p => p.PostTags)
                      .HasForeignKey(pt => pt.PostId)
                      .OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.HasKey(pt => new { pt.PostId, pt.TagId });
                    j.HasIndex(pt => pt.TagId);
                    j.Property(pt => pt.Source).HasConversion<int>();
                });

        b.Entity<TagNamespace>(e =>
        {
            e.HasIndex(n => n.Name).IsUnique();
            e.Property(n => n.Name).HasMaxLength(TagName.MaxNamespaceLength);
            e.Property(n => n.Color).HasMaxLength(16);
        });

        b.Entity<TagAlias>(e =>
        {
            e.HasKey(a => a.AliasTagId);

            e.HasOne(a => a.AliasTag)
                .WithMany()
                .HasForeignKey(a => a.AliasTagId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(a => a.CanonicalTag)
                .WithMany()
                .HasForeignKey(a => a.CanonicalTagId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(a => a.CanonicalTagId);
        });

        b.Entity<TagImplication>(e =>
        {
            e.HasKey(i => new { i.ChildTagId, i.ParentTagId });

            e.HasOne(i => i.ChildTag)
                .WithMany()
                .HasForeignKey(i => i.ChildTagId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(i => i.ParentTag)
                .WithMany()
                .HasForeignKey(i => i.ParentTagId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(i => i.ParentTagId);
        });

        b.Entity<User>(e =>
        {
            e.Property(u => u.Username).HasMaxLength(64).IsRequired();
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(u => u.CreatedAt).HasConversion(UtcMilliseconds);
        });
    }
}
