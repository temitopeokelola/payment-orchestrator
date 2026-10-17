using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentOrchestrator.Application.Outbox;
using PaymentOrchestrator.Infrastructure.Persistence;

namespace PaymentOrchestrator.Infrastructure.Outbox;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string MerchantId { get; init; }
    public required string Data { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public IntegrationEvent ToIntegrationEvent() => new(Id, Type, MerchantId, OccurredAt, Data);
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(64);
        builder.Property(m => m.MerchantId).HasMaxLength(64);
        builder.Property(m => m.Data).HasColumnType("jsonb");
        builder.Property(m => m.LastError).HasMaxLength(1024);

        // Partial index: the processor only ever scans unpublished messages.
        builder.HasIndex(m => m.OccurredAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_messages_unpublished");
    }
}

internal sealed class EfOutbox(PaymentsDbContext db) : IOutbox
{
    public void Enqueue(IntegrationEvent integrationEvent) => db.Set<OutboxMessage>().Add(new OutboxMessage
    {
        Id = integrationEvent.Id,
        Type = integrationEvent.Type,
        MerchantId = integrationEvent.MerchantId,
        Data = integrationEvent.Data,
        OccurredAt = integrationEvent.OccurredAt,
    });
}
