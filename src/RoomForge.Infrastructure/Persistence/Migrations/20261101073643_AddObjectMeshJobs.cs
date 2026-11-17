using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoomForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectMeshJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObjectMeshJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedSceneRevision = table.Column<int>(type: "integer", nullable: false),
                    CropAssetStorageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CropContentType = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SubmissionState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ProviderJobId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OutputAssetStorageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Progress = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectMeshJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjectMeshJobs_ImageAssets_SourceImageId",
                        column: x => x.SourceImageId,
                        principalTable: "ImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ObjectMeshJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ObjectMeshJobs_ReconstructionJobs_AnalysisJobId",
                        column: x => x.AnalysisJobId,
                        principalTable: "ReconstructionJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ObjectMeshJobs_Scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "Scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMeshJobs_AnalysisJobId",
                table: "ObjectMeshJobs",
                column: "AnalysisJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMeshJobs_ProjectId_IdempotencyKey",
                table: "ObjectMeshJobs",
                columns: new[] { "ProjectId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMeshJobs_SceneId",
                table: "ObjectMeshJobs",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMeshJobs_SourceImageId",
                table: "ObjectMeshJobs",
                column: "SourceImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMeshJobs_Status_CreatedAt",
                table: "ObjectMeshJobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObjectMeshJobs");
        }
    }
}
