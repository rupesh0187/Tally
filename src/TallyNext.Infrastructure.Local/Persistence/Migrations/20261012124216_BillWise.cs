using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TallyNext.Infrastructure.Local.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillWise : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpeningBillsJson",
                table: "Ledgers",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "VoucherBillAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VoucherId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    AllocationNo = table.Column<int>(type: "INTEGER", nullable: false),
                    LedgerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Side = table.Column<byte>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    BillName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    NormalizedBillName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    VoucherDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsPosted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoucherBillAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoucherBillAllocations_Ledgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VoucherBillAllocations_Vouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "Vouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoucherBillAllocations_CompanyId_LedgerId_NormalizedBillName",
                table: "VoucherBillAllocations",
                columns: new[] { "CompanyId", "LedgerId", "NormalizedBillName" });

            migrationBuilder.CreateIndex(
                name: "IX_VoucherBillAllocations_LedgerId",
                table: "VoucherBillAllocations",
                column: "LedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherBillAllocations_VoucherId_LineNo_AllocationNo",
                table: "VoucherBillAllocations",
                columns: new[] { "VoucherId", "LineNo", "AllocationNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoucherBillAllocations");

            migrationBuilder.DropColumn(
                name: "OpeningBillsJson",
                table: "Ledgers");
        }
    }
}
