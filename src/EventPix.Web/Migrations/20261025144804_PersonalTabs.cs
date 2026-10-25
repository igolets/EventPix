using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventPix.Web.Migrations
{
    /// <inheritdoc />
    public partial class PersonalTabs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Collapse same-named author tabs duplicated by the find-or-create race in /uploads: point all
            // media of a member's duplicate tabs at their oldest one, then remove the duplicates.
            migrationBuilder.Sql("""
                UPDATE MediaItems
                SET TabId = K.KeeperId
                FROM (
                    SELECT D.Id AS DupId,
                           (SELECT MIN(K2.Id) FROM Tabs K2
                            WHERE K2.EventId = D.EventId AND K2.Kind = 1 AND K2.OwnerMemberId = D.OwnerMemberId
                              AND K2.Name = D.Name) AS KeeperId
                    FROM Tabs D
                    WHERE D.Kind = 1 AND D.OwnerMemberId IS NOT NULL
                ) AS K
                WHERE MediaItems.TabId = K.DupId AND MediaItems.TabId <> K.KeeperId;

                DELETE FROM Tabs
                WHERE Kind = 1 AND OwnerMemberId IS NOT NULL
                  AND Id <> (SELECT MIN(K2.Id) FROM Tabs K2
                             WHERE K2.EventId = Tabs.EventId AND K2.Kind = 1 AND K2.OwnerMemberId = Tabs.OwnerMemberId
                               AND K2.Name = Tabs.Name);
                """);

            migrationBuilder.DropIndex(
                name: "IX_Tabs_EventId_OwnerMemberId",
                table: "Tabs");

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_EventId_OwnerMemberId_Name",
                table: "Tabs",
                columns: new[] { "EventId", "OwnerMemberId", "Name" },
                unique: true,
                filter: "[Kind] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tabs_EventId_OwnerMemberId_Name",
                table: "Tabs");

            migrationBuilder.CreateIndex(
                name: "IX_Tabs_EventId_OwnerMemberId",
                table: "Tabs",
                columns: new[] { "EventId", "OwnerMemberId" },
                unique: true,
                filter: "[Kind] = 1");
        }
    }
}
