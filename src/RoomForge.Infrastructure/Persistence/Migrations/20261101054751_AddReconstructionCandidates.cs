using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoomForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReconstructionCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReconstructionCandidates",
                columns: table => new
                {
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnotationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconstructionCandidates", x => x.CandidateId);
                    table.ForeignKey(
                        name: "FK_ReconstructionCandidates_AnnotationDocuments_AnnotationId",
                        column: x => x.AnnotationId,
                        principalTable: "AnnotationDocuments",
                        principalColumn: "AnnotationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReconstructionCandidates_Scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "Scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionCandidates_AnnotationId",
                table: "ReconstructionCandidates",
                column: "AnnotationId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionCandidates_ProjectId_SceneId_CreatedAt",
                table: "ReconstructionCandidates",
                columns: new[] { "ProjectId", "SceneId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionCandidates_SceneId",
                table: "ReconstructionCandidates",
                column: "SceneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconstructionCandidates");
        }
    }
}
