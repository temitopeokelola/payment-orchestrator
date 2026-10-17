using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentOrchestrator.Application.Webhooks;

namespace PaymentOrchestrator.Infrastructure.Webhooks;

public sealed class WebhookDelivery
{
    public Guid Id { get; init; }
    public Guid EventId { get; init; }
    public required string EventType { get; init; }
    public required string MerchantId { get; init; }
    public required string Url { get; init; }
    public required string Payload { get; init; }
    public WebhookDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }

    public WebhookDeliveryResponse ToResponse() => new(
        Id, EventId, EventType, Url, Status.ToString().ToLowerInvariant(), Attempts, LastStatusCode, LastError, CreatedAt, NextAttemptAt, DeliveredAt);
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.EventType).HasMaxLength(64);
        builder.Property(d => d.MerchantId).HasMaxLength(64);
        builder.Property(d => d.Url).HasMaxLength(2048);
        builder.Property(d => d.Payload).HasColumnType("jsonb");
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.LastError).HasMaxLength(1024);

        // One delivery per event per endpoint: the consumer can safely process a redelivered message.
        builder.HasIndex(d => new { d.EventId, d.Url }).IsUnique();
        builder.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_webhook_deliveries_due");
        builder.HasIndex(d => new { d.MerchantId, d.CreatedAt });
    }
}
