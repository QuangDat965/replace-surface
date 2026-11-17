using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoomForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomTopologies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomTopologies",
                columns: table => new
                {
                    TopologyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnotationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomTopologies", x => x.TopologyId);
                    table.ForeignKey(
                        name: "FK_RoomTopologies_AnnotationDocuments_AnnotationId",
                        column: x => x.AnnotationId,
                        principalTable: "AnnotationDocuments",
                        principalColumn: "AnnotationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoomTopologies_AnnotationId",
                table: "RoomTopologies",
                column: "AnnotationId");

            migrationBuilder.CreateIndex(
                name: "IX_RoomTopologies_ProjectId_AnnotationId_Revision",
                table: "RoomTopologies",
                columns: new[] { "ProjectId", "AnnotationId", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomTopologies");
        }
    }
}
