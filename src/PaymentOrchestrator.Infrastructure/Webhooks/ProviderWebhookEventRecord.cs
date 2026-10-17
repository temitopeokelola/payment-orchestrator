using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PaymentOrchestrator.Infrastructure.Webhooks;

public sealed class ProviderWebhookEventRecord
{
    public required string Provider { get; init; }
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public Guid? PaymentId { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}

internal sealed class ProviderWebhookEventRecordConfiguration : IEntityTypeConfiguration<ProviderWebhookEventRecord>
{
    public void Configure(EntityTypeBuilder<ProviderWebhookEventRecord> builder)
    {
        builder.ToTable("provider_webhook_events");
        builder.HasKey(e => new { e.Provider, e.EventId });
        builder.Property(e => e.Provider).HasMaxLength(32);
        builder.Property(e => e.EventId).HasMaxLength(128);
        builder.Property(e => e.Type).HasMaxLength(32);
        builder.Property(e => e.Payload).HasColumnType("jsonb");
        builder.HasIndex(e => e.PaymentId);
    }
}
