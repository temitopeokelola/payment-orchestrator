using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PaymentOrchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDoubleEntryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_postings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    account = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_postings", x => x.id);
                    table.ForeignKey(
                        name: "fk_ledger_postings_ledger_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "ledger_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_payment_id",
                table: "ledger_entries",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_postings_account",
                table: "ledger_postings",
                column: "account");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_postings_entry_id",
                table: "ledger_postings",
                column: "entry_id");

            // The database enforces the ledger rules too, so no code path (or manual SQL) can break them.
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_assert_entry_balanced() RETURNS trigger AS $$
                DECLARE total bigint;
                BEGIN
                    SELECT COALESCE(SUM(amount), 0) INTO total FROM ledger_postings WHERE entry_id = NEW.entry_id;
                    IF total <> 0 THEN
                        RAISE EXCEPTION 'ledger entry % is unbalanced by %', NEW.entry_id, total USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                -- Deferred to commit time, because the postings of one entry are inserted one row at a time.
                CREATE CONSTRAINT TRIGGER ledger_postings_balanced
                    AFTER INSERT ON ledger_postings
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_assert_entry_balanced();

                CREATE FUNCTION ledger_reject_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'ledger is append-only; post a correcting entry instead' USING ERRCODE = '55000';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER ledger_entries_append_only
                    BEFORE UPDATE OR DELETE ON ledger_entries
                    FOR EACH ROW EXECUTE FUNCTION ledger_reject_mutation();

                CREATE TRIGGER ledger_postings_append_only
                    BEFORE UPDATE OR DELETE ON ledger_postings
                    FOR EACH ROW EXECUTE FUNCTION ledger_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS ledger_postings_append_only ON ledger_postings;
                DROP TRIGGER IF EXISTS ledger_entries_append_only ON ledger_entries;
                DROP TRIGGER IF EXISTS ledger_postings_balanced ON ledger_postings;
                DROP FUNCTION IF EXISTS ledger_reject_mutation();
                DROP FUNCTION IF EXISTS ledger_assert_entry_balanced();
                """);

            migrationBuilder.DropTable(
                name: "ledger_postings");

            migrationBuilder.DropTable(
                name: "ledger_entries");
        }
    }
}
