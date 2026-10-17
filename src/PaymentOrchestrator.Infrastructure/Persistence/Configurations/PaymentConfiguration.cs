using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentOrchestrator.Domain.Payments;

namespace PaymentOrchestrator.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.MerchantId).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.PaymentMethod).HasMaxLength(128).IsRequired();
        builder.Property(p => p.CustomerEmail).HasMaxLength(254);
        builder.Property(p => p.Provider).HasMaxLength(32);
        builder.Property(p => p.ProviderReference).HasMaxLength(128);
        builder.Property(p => p.FailureReason).HasMaxLength(256);

        builder.ComplexProperty(p => p.Amount, m =>
        {
            m.Property(x => x.AmountMinor).HasColumnName("amount");
            m.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
        });
        builder.ComplexProperty(p => p.CapturedAmount, m =>
        {
            m.Property(x => x.AmountMinor).HasColumnName("captured_amount");
            m.Property(x => x.Currency).HasColumnName("captured_currency").HasMaxLength(3).IsFixedLength();
        });
        builder.ComplexProperty(p => p.RefundedAmount, m =>
        {
            m.Property(x => x.AmountMinor).HasColumnName("refunded_amount");
            m.Property(x => x.Currency).HasColumnName("refunded_currency").HasMaxLength(3).IsFixedLength();
        });

        // Postgres xmin system column gives optimistic concurrency without an extra column.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasIndex(p => new { p.MerchantId, p.CreatedAt });
        builder.HasIndex(p => new { p.Provider, p.ProviderReference }).IsUnique()
            .HasFilter("provider_reference IS NOT NULL");

        builder.Ignore(p => p.DomainEvents);
    }
}
