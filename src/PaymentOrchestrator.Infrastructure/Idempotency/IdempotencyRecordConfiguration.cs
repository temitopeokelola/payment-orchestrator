using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PaymentOrchestrator.Infrastructure.Idempotency;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(r => new { r.MerchantId, r.Key });
        builder.Property(r => r.MerchantId).HasMaxLength(64);
        builder.Property(r => r.Key).HasMaxLength(255);
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsFixedLength();
        builder.Property(r => r.ResponseBody).HasColumnType("jsonb");
        builder.HasIndex(r => r.CreatedAt);
    }
}
