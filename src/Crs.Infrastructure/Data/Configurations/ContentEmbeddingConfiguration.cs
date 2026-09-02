using Crs.Core.Entities;
using Crs.Infrastructure.Configuration;
using Crs.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crs.Infrastructure.Data.Configurations;

public class ContentEmbeddingConfiguration : IEntityTypeConfiguration<ContentEmbedding>
{
    public void Configure(EntityTypeBuilder<ContentEmbedding> builder)
    {
        builder.ToTable("ContentEmbeddings");

        builder.HasKey(e => e.ContentId);

        builder.Property(e => e.Embedding)
            .HasColumnType($"vector({EmbeddingSettings.DefaultDimensions})")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .IsRequired();

        builder.HasOne<Content>()
            .WithMany()
            .HasForeignKey(e => e.ContentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
