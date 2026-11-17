using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoomForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderSpendReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderSpendReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderSpendReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderSpendReservations_ReconstructionJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ReconstructionJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderSpendReservations_CreatedAt",
                table: "ProviderSpendReservations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderSpendReservations_JobId",
                table: "ProviderSpendReservations",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderSpendReservations");
        }
    }
}
