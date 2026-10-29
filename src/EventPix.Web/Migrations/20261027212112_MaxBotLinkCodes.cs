using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventPix.Web.Migrations
{
    /// <inheritdoc />
    public partial class MaxBotLinkCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing codes were Telegram ones; outstanding 15-minute links stay valid.
            migrationBuilder.RenameTable(
                name: "TelegramLinkCodes",
                newName: "BotLinkCodes");

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "BotLinkCodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);   // NotifyChannel.Telegram

            migrationBuilder.DropIndex(
                name: "IX_TelegramLinkCodes_CodeHash",
                table: "BotLinkCodes");

            migrationBuilder.DropIndex(
                name: "IX_TelegramLinkCodes_EventId",
                table: "BotLinkCodes");

            migrationBuilder.DropIndex(
                name: "IX_TelegramLinkCodes_ExpiresUtc",
                table: "BotLinkCodes");

            migrationBuilder.DropIndex(
                name: "IX_TelegramLinkCodes_MemberId",
                table: "BotLinkCodes");

            migrationBuilder.CreateIndex(
                name: "IX_BotLinkCodes_CodeHash",
                table: "BotLinkCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotLinkCodes_EventId",
                table: "BotLinkCodes",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_BotLinkCodes_ExpiresUtc",
                table: "BotLinkCodes",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BotLinkCodes_MemberId",
                table: "BotLinkCodes",
                column: "MemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotLinkCodes");

            migrationBuilder.CreateTable(
                name: "TelegramLinkCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MemberId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    TabId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramLinkCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramLinkCodes_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TelegramLinkCodes_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_CodeHash",
                table: "TelegramLinkCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_EventId",
                table: "TelegramLinkCodes",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_ExpiresUtc",
                table: "TelegramLinkCodes",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_MemberId",
                table: "TelegramLinkCodes",
                column: "MemberId");
        }
    }
}
