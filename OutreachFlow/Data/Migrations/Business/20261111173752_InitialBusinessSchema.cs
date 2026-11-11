using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OutreachFlow.Data.Migrations.Business
{
    /// <inheritdoc />
    public partial class InitialBusinessSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProspectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProspectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Classification = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsSimulation = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Prospects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: false),
                    SiteName = table.Column<string>(type: "TEXT", nullable: false),
                    Website = table.Column<string>(type: "TEXT", nullable: false),
                    ContactEmail = table.Column<string>(type: "TEXT", nullable: false),
                    ResearchOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResearchNotes = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessUrl = table.Column<string>(type: "TEXT", nullable: false),
                    DraftSubject = table.Column<string>(type: "TEXT", nullable: false),
                    DraftBody = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedFollowups = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstFollowupDelayDays = table.Column<int>(type: "INTEGER", nullable: false),
                    SecondFollowupDelayDays = table.Column<int>(type: "INTEGER", nullable: false),
                    NoResponseDelayDays = table.Column<int>(type: "INTEGER", nullable: false),
                    SequenceTimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    SequenceStartHour = table.Column<int>(type: "INTEGER", nullable: false),
                    NextStepDueUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AutomationPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerNotes = table.Column<string>(type: "TEXT", nullable: false),
                    NextAction = table.Column<string>(type: "TEXT", nullable: false),
                    NextActionDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prospects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResearchEvidence",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProspectId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceTitle = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ExcerptOrObservation = table.Column<string>(type: "TEXT", nullable: false),
                    IsOfficial = table.Column<bool>(type: "INTEGER", nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchEvidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessName = table.Column<string>(type: "TEXT", nullable: false),
                    SenderName = table.Column<string>(type: "TEXT", nullable: false),
                    SenderEmail = table.Column<string>(type: "TEXT", nullable: false),
                    SenderPhone = table.Column<string>(type: "TEXT", nullable: false),
                    PostalAddress = table.Column<string>(type: "TEXT", nullable: false),
                    ServiceDescription = table.Column<string>(type: "TEXT", nullable: false),
                    ServiceArea = table.Column<string>(type: "TEXT", nullable: false),
                    TargetDescription = table.Column<string>(type: "TEXT", nullable: false),
                    MaxFollowups = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstFollowupBusinessDays = table.Column<int>(type: "INTEGER", nullable: false),
                    SecondFollowupBusinessDays = table.Column<int>(type: "INTEGER", nullable: false),
                    NoResponseAfterBusinessDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DailySendLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    SendingStartHour = table.Column<int>(type: "INTEGER", nullable: false),
                    SendingEndHour = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    InitialSubjectTemplate = table.Column<string>(type: "TEXT", nullable: false),
                    InitialBodyTemplate = table.Column<string>(type: "TEXT", nullable: false),
                    FollowupOneTemplate = table.Column<string>(type: "TEXT", nullable: false),
                    FollowupTwoTemplate = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ProspectId_OccurredUtc",
                table: "Activities",
                columns: new[] { "ProspectId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ProspectId_OccurredUtc",
                table: "Messages",
                columns: new[] { "ProspectId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Prospects_WorkspaceId_CompanyName_SiteName",
                table: "Prospects",
                columns: new[] { "WorkspaceId", "CompanyName", "SiteName" });

            migrationBuilder.CreateIndex(
                name: "IX_Prospects_WorkspaceId_NextStepDueUtc",
                table: "Prospects",
                columns: new[] { "WorkspaceId", "NextStepDueUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvidence_ProspectId",
                table: "ResearchEvidence",
                column: "ProspectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Prospects");

            migrationBuilder.DropTable(
                name: "ResearchEvidence");

            migrationBuilder.DropTable(
                name: "Workspaces");
        }
    }
}
