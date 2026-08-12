using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Migrations
{
    /// <inheritdoc />
    public partial class AddScopesAndQuoteOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Quotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_CreatedByUserId",
                table: "Quotes",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Quotes_Users_CreatedByUserId",
                table: "Quotes",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Quotes_Users_CreatedByUserId",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_CreatedByUserId",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Quotes");
        }
    }
}
