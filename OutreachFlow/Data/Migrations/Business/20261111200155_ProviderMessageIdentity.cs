using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutreachFlow.Data.Migrations.Business
{
    /// <inheritdoc />
    public partial class ProviderMessageIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Prospects_WorkspaceId_Id",
                table: "Prospects",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Messages_ProspectId_Id",
                table: "Messages",
                columns: new[] { "ProspectId", "Id" });

            migrationBuilder.CreateTable(
                name: "ProviderMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProspectId = table.Column<int>(type: "INTEGER", nullable: false),
                    OutreachMessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    MailboxEmail = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderMessages", x => x.Id);
                    table.CheckConstraint("CK_ProviderMessages_Identity", "\"Provider\" = 'gmail' AND length(trim(\"MailboxEmail\")) > 0 AND length(trim(\"MessageId\")) > 0 AND length(trim(\"ThreadId\")) > 0");
                    table.ForeignKey(
                        name: "FK_ProviderMessages_Messages_ProspectId_OutreachMessageId",
                        columns: x => new { x.ProspectId, x.OutreachMessageId },
                        principalTable: "Messages",
                        principalColumns: new[] { "ProspectId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderMessages_Prospects_WorkspaceId_ProspectId",
                        columns: x => new { x.WorkspaceId, x.ProspectId },
                        principalTable: "Prospects",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderMessages_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderMessages_OutreachMessageId",
                table: "ProviderMessages",
                column: "OutreachMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderMessages_ProspectId_OutreachMessageId",
                table: "ProviderMessages",
                columns: new[] { "ProspectId", "OutreachMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderMessages_WorkspaceId_ProspectId",
                table: "ProviderMessages",
                columns: new[] { "WorkspaceId", "ProspectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderMessages_WorkspaceId_Provider_MailboxEmail_MessageId",
                table: "ProviderMessages",
                columns: new[] { "WorkspaceId", "Provider", "MailboxEmail", "MessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderMessages");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Prospects_WorkspaceId_Id",
                table: "Prospects");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Messages_ProspectId_Id",
                table: "Messages");
        }
    }
}
