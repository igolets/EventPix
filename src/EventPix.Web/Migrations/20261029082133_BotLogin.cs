using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventPix.Web.Migrations
{
    /// <inheritdoc />
    public partial class BotLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Members",
                type: "TEXT",
                maxLength: 254,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 254);

            migrationBuilder.AddColumn<int>(
                name: "MessengerChannel",
                table: "Members",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessengerHandle",
                table: "Members",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessengerId",
                table: "Members",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Invites",
                type: "TEXT",
                maxLength: 254,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 254);

            migrationBuilder.AddColumn<int>(
                name: "MessengerChannel",
                table: "Invites",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessengerHandle",
                table: "Invites",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoginRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemberId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoginRequests_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoginRequests_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Members_EventId_MessengerChannel_MessengerId",
                table: "Members",
                columns: new[] { "EventId", "MessengerChannel", "MessengerId" },
                unique: true,
                filter: "[MessengerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_EventId_MessengerChannel_MessengerHandle",
                table: "Invites",
                columns: new[] { "EventId", "MessengerChannel", "MessengerHandle" },
                unique: true,
                filter: "[MessengerHandle] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoginRequests_CodeHash",
                table: "LoginRequests",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginRequests_EventId",
                table: "LoginRequests",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_LoginRequests_ExpiresUtc",
                table: "LoginRequests",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LoginRequests_MemberId",
                table: "LoginRequests",
                column: "MemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoginRequests");

            migrationBuilder.DropIndex(
                name: "IX_Members_EventId_MessengerChannel_MessengerId",
                table: "Members");

            migrationBuilder.DropIndex(
                name: "IX_Invites_EventId_MessengerChannel_MessengerHandle",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "MessengerChannel",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "MessengerHandle",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "MessengerId",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "MessengerChannel",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "MessengerHandle",
                table: "Invites");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Members",
                type: "TEXT",
                maxLength: 254,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 254,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Invites",
                type: "TEXT",
                maxLength: 254,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 254,
                oldNullable: true);
        }
    }
}
