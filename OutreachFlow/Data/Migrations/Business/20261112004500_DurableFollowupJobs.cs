using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutreachFlow.Data.Migrations.Business
{
    /// <inheritdoc />
    public partial class DurableFollowupJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FollowupJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProspectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Step = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    DueUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<string>(type: "TEXT", nullable: false),
                    ReplyToMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    RfcMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttemptedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GmailMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowupJobs", x => x.Id);
                    table.CheckConstraint("CK_FollowupJob_State",
                        "\"State\" IN ('Queued','Sending','Sent','Held','Uncertain','Cancelled')");
                    table.CheckConstraint("CK_FollowupJob_Kind",
                        "\"Kind\" IN ('Email','ManualHandoff') AND \"Step\" BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_FollowupJobs_Prospects_WorkspaceId_ProspectId",
                        columns: x => new { x.WorkspaceId, x.ProspectId },
                        principalTable: "Prospects",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FollowupJobs_RfcMessageId",
                table: "FollowupJobs", column: "RfcMessageId", unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_FollowupJobs_State_DueUtc",
                table: "FollowupJobs", columns: new[] { "State", "DueUtc" });
            migrationBuilder.CreateIndex(
                name: "IX_FollowupJobs_WorkspaceId_ProspectId_Step",
                table: "FollowupJobs",
                columns: new[] { "WorkspaceId", "ProspectId", "Step" }, unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "FollowupJobs");
        }
    }
}
