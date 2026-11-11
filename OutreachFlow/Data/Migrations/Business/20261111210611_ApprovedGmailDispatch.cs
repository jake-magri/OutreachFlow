using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutreachFlow.Data.Migrations.Business
{
    /// <inheritdoc />
    public partial class ApprovedGmailDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboundDispatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProspectId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    ResearchSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    PolicySnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovalHash = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RfcMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundDispatches", x => x.Id);
                    table.CheckConstraint("CK_OutboundDispatch_State", "\"State\" IN ('Sending','Sent','Uncertain','Rejected')");
                    table.ForeignKey(
                        name: "FK_OutboundDispatches_Prospects_WorkspaceId_ProspectId",
                        columns: x => new { x.WorkspaceId, x.ProspectId },
                        principalTable: "Prospects",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundDispatches_ProspectId",
                table: "OutboundDispatches",
                column: "ProspectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundDispatches_RfcMessageId",
                table: "OutboundDispatches",
                column: "RfcMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundDispatches_WorkspaceId_ProspectId",
                table: "OutboundDispatches",
                columns: new[] { "WorkspaceId", "ProspectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboundDispatches");
        }
    }
}
