using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoomForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionReliabilityAndAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastReconciledAt",
                table: "ReconstructionJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "ReconstructionJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderId",
                table: "ReconstructionJobs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ProviderIdempotencySupported",
                table: "ReconstructionJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProviderJobId",
                table: "ReconstructionJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReconciliationAttempts",
                table: "ReconstructionJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SubmissionState",
                table: "ReconstructionJobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "not_attempted");

            migrationBuilder.CreateTable(
                name: "AnnotationDocuments",
                columns: table => new
                {
                    AnnotationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnotationDocuments", x => x.AnnotationId);
                    table.ForeignKey(
                        name: "FK_AnnotationDocuments_ImageAssets_SourceImageId",
                        column: x => x.SourceImageId,
                        principalTable: "ImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnnotationDocuments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionJobs_Status_NextAttemptAt",
                table: "ReconstructionJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AnnotationDocuments_ProjectId_SourceImageId",
                table: "AnnotationDocuments",
                columns: new[] { "ProjectId", "SourceImageId" });

            migrationBuilder.CreateIndex(
                name: "IX_AnnotationDocuments_SourceImageId",
                table: "AnnotationDocuments",
                column: "SourceImageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnnotationDocuments");

            migrationBuilder.DropIndex(
                name: "IX_ReconstructionJobs_Status_NextAttemptAt",
                table: "ReconstructionJobs");

            migrationBuilder.DropColumn(
                name: "LastReconciledAt",
                table: "ReconstructionJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "ReconstructionJobs");

            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "ReconstructionJobs");

            migrationBuilder.DropColumn(
                name: "ProviderIdempotencySupported",
                table: "ReconstructionJobs");

            migrationBuilder.DropColumn(
                name: "ProviderJobId",
                table: "ReconstructionJobs");

            migrationBuilder.DropColumn(
                name: "ReconciliationAttempts",
                table: "ReconstructionJobs");

            migrationBuilder.DropColumn(
                name: "SubmissionState",
                table: "ReconstructionJobs");
        }
    }
}
