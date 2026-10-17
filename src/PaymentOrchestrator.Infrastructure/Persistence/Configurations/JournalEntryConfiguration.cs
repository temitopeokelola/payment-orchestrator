using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentOrchestrator.Domain.Ledger;

namespace PaymentOrchestrator.Infrastructure.Persistence.Configurations;

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("ledger_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.Currency).HasMaxLength(3).IsFixedLength();
        builder.HasIndex(e => e.PaymentId);

        builder.OwnsMany(e => e.Postings, postings =>
        {
            postings.ToTable("ledger_postings");
            postings.WithOwner().HasForeignKey("EntryId");
            postings.Property<long>("Id").UseIdentityAlwaysColumn();
            postings.HasKey("Id");
            postings.Property(p => p.Account).HasMaxLength(128);
            postings.Property(p => p.AmountMinor).HasColumnName("amount");
            postings.HasIndex(p => p.Account);
        });

        builder.Navigation(e => e.Postings).HasField("_postings").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
