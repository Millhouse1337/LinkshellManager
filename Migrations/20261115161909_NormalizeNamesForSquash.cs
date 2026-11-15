using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Squash prep (Round 4 deploy 4, plan D10, migration 2). The squash (D11) replaces the whole
    // migration chain with one Baseline made from the model. Before it, production, a fresh database
    // built from the chain, and a fresh database built from the model must have the same catalog
    // (Snapshots/SchemaCatalog.txt). This makes them agree. It changes no row and no column.
    //
    // 1. VALIDATE the seven CHECK constraints the chain added NOT VALID (AddOneParentCheckConstraints,
    //    AddClosedListCheckConstraints). A table built from the model has them fully checked.
    //    Production already had the two OneParent checks validated by hand, and 0 rows break any of
    //    the seven (read-only counts, 2026-11-15). NOT VALID checks still refuse every new insert and
    //    update, so that count cannot have grown. VALIDATE only reads the rows; it does not block
    //    reads or writes while it runs.
    //    A check that is already validated is skipped. If an old row did break one, that check is
    //    left NOT VALID with a Postgres WARNING instead of failing the app's start. The WARNING does
    //    not reach the normal startup log (Npgsql logs notices only at Debug), so the owner checks
    //    pg_constraint for "NOT convalidated" after the deploy (docs/round4-drop-manifest.md), fixes
    //    the row and runs ValidateChecksSql again by hand (safe to run twice).
    //
    // 2. Rename the names PostgreSQL kept from before a table or column rename, to the names a table
    //    built from the model gets. Postgres 18 names each NOT NULL constraint "<table>_<column>_not_null"
    //    and each identity sequence "<table>_<column>_seq" when the column is made, and never renames
    //    them later. So SkyPopItems (now ChartPopItems), SkyPopItemCredits (now ChartPopItemCredits),
    //    ChartPopItems.God (now Boss), the credits' SkyPopItemId (now ChartPopItemId) and
    //    Linkshells.EnableRevenue (now EnableTreasury) left 13 constraint and 2 sequence names behind.
    //    Each rename runs only if the old name is there and the new one is not, so it is safe to run
    //    twice. Nothing in the app or the tools names them (EF finds an identity sequence through its
    //    column, not by name).
    //
    // Down renames them back. It does NOT un-validate the checks: Postgres can only make a validated
    // check NOT VALID again by dropping and re-adding it, and a validated check is the same rule,
    // just also proven for the old rows. Every build before this one works the same either way.
    public partial class NormalizeNamesForSquash : Migration
    {
        public const string MigrationId = "20261115161909_NormalizeNamesForSquash";

        // The seven checks the chain adds NOT VALID, as (table, constraint).
        public static readonly (string Table, string Name)[] Checks =
        {
            ("AuctionItems", "CK_AuctionItems_OneParent"),
            ("EventAttendanceWindows", "CK_EventAttendanceWindows_OneParent"),
            ("AttendanceSnapshots", "CK_AttendanceSnapshots_SlotKind"),
            ("AttendanceSnapshots", "CK_AttendanceSnapshots_SnapshotStatus"),
            ("Linkshells", "CK_Linkshells_HnmAttendanceMode"),
            ("WindowEvents", "CK_WindowEvents_EntryType"),
            ("WindowEvents", "CK_WindowEvents_Status"),
        };

        // (table, old name, new name) for the NOT NULL constraints.
        public static readonly (string Table, string OldName, string NewName)[] ConstraintRenames =
        {
            ("ChartPopItemCredits", "SkyPopItemCredits_CharacterName_not_null", "ChartPopItemCredits_CharacterName_not_null"),
            ("ChartPopItemCredits", "SkyPopItemCredits_CreditedAt_not_null", "ChartPopItemCredits_CreditedAt_not_null"),
            ("ChartPopItemCredits", "SkyPopItemCredits_Id_not_null", "ChartPopItemCredits_Id_not_null"),
            ("ChartPopItemCredits", "SkyPopItemCredits_SkyPopItemId_not_null", "ChartPopItemCredits_ChartPopItemId_not_null"),
            ("ChartPopItems", "SkyPopItems_CreatedAt_not_null", "ChartPopItems_CreatedAt_not_null"),
            ("ChartPopItems", "SkyPopItems_God_not_null", "ChartPopItems_Boss_not_null"),
            ("ChartPopItems", "SkyPopItems_Id_not_null", "ChartPopItems_Id_not_null"),
            ("ChartPopItems", "SkyPopItems_ItemName_not_null", "ChartPopItems_ItemName_not_null"),
            ("ChartPopItems", "SkyPopItems_LinkshellId_not_null", "ChartPopItems_LinkshellId_not_null"),
            ("ChartPopItems", "SkyPopItems_Quantity_not_null", "ChartPopItems_Quantity_not_null"),
            ("ChartPopItems", "SkyPopItems_SortOrder_not_null", "ChartPopItems_SortOrder_not_null"),
            ("ChartPopItems", "SkyPopItems_UpdatedAt_not_null", "ChartPopItems_UpdatedAt_not_null"),
            ("Linkshells", "Linkshells_EnableRevenue_not_null", "Linkshells_EnableTreasury_not_null"),
        };

        // (old name, new name) for the identity sequences.
        public static readonly (string OldName, string NewName)[] SequenceRenames =
        {
            ("SkyPopItemCredits_Id_seq", "ChartPopItemCredits_Id_seq"),
            ("SkyPopItems_Id_seq", "ChartPopItems_Id_seq"),
        };

        // Validates each of the seven checks that is still NOT VALID. One that an old row breaks is
        // left as it is, with a WARNING naming it. Safe to run twice, and by hand.
        public static readonly string ValidateChecksSql = $$"""
            DO $$
            DECLARE
                c record;
            BEGIN
                FOR c IN
                    SELECT k.conrelid::regclass AS tbl, k.conname
                    FROM pg_constraint k
                    WHERE k.connamespace = 'public'::regnamespace AND k.contype = 'c' AND NOT k.convalidated
                      AND k.conname IN ({{string.Join(", ", System.Array.ConvertAll(Checks, check => $"'{check.Name}'"))}})
                    ORDER BY k.conname
                LOOP
                    BEGIN
                        EXECUTE format('ALTER TABLE %s VALIDATE CONSTRAINT %I', c.tbl, c.conname);
                        RAISE NOTICE 'NormalizeNamesForSquash: validated %.', c.conname;
                    EXCEPTION WHEN check_violation THEN
                        RAISE WARNING 'NormalizeNamesForSquash: % is still NOT VALID because an old row breaks it. Fix the row, then validate it by hand.', c.conname;
                    END;
                END LOOP;
            END $$;
            """;

        public static readonly string RenameSql = BuildRenameSql(forward: true);

        public static readonly string RenameBackSql = BuildRenameSql(forward: false);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ValidateChecksSql);
            migrationBuilder.Sql(RenameSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The checks stay validated (see the note at the top).
            migrationBuilder.Sql(RenameBackSql);
        }

        // One guarded rename per name: only when the name to rename is there and the target is not.
        private static string BuildRenameSql(bool forward)
        {
            var sql = new System.Text.StringBuilder("DO $$\nBEGIN\n");
            foreach (var (table, oldName, newName) in ConstraintRenames)
            {
                var (from, to) = forward ? (oldName, newName) : (newName, oldName);
                sql.Append($"""
                        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '"{table}"'::regclass AND conname = '{from}')
                           AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '"{table}"'::regclass AND conname = '{to}') THEN
                            ALTER TABLE "{table}" RENAME CONSTRAINT "{from}" TO "{to}";
                        END IF;

                    """);
            }
            foreach (var (oldName, newName) in SequenceRenames)
            {
                var (from, to) = forward ? (oldName, newName) : (newName, oldName);
                sql.Append($"""
                        IF EXISTS (SELECT 1 FROM pg_class WHERE relkind = 'S' AND relnamespace = 'public'::regnamespace AND relname = '{from}')
                           AND NOT EXISTS (SELECT 1 FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relname = '{to}') THEN
                            ALTER SEQUENCE "{from}" RENAME TO "{to}";
                        END IF;

                    """);
            }
            return sql.Append("END $$;\n").ToString();
        }
    }
}
