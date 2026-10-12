using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TallyNext.Infrastructure.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryAndGst : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PartyLedgerId",
                table: "Vouchers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfSupplyStateCode",
                table: "Vouchers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "SupplyNature",
                table: "Vouchers",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GstRatesJson",
                table: "Ledgers",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<byte>(
                name: "GstTaxType",
                table: "Ledgers",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte>(
                name: "GstRegistration",
                table: "Companies",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "Godowns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Godowns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Godowns_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Units",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NormalizedSymbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DecimalPlaces = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Units", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Units_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PartNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValuationMethod = table.Column<byte>(type: "smallint", nullable: false),
                    GstRatesJson = table.Column<string>(type: "text", nullable: false),
                    OpeningQuantityMilli = table.Column<long>(type: "bigint", nullable: false),
                    OpeningValueMinor = table.Column<long>(type: "bigint", nullable: false),
                    OpeningGodownId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockItems_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockItems_Godowns_OpeningGodownId",
                        column: x => x.OpeningGodownId,
                        principalTable: "Godowns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockItems_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VoucherItemLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VoucherId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    StockItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    GodownId = table.Column<Guid>(type: "uuid", nullable: true),
                    LedgerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    QuantityMilli = table.Column<long>(type: "bigint", nullable: false),
                    RateScaled = table.Column<long>(type: "bigint", nullable: false),
                    DiscountMilliPercent = table.Column<int>(type: "integer", nullable: false),
                    TaxableMinor = table.Column<long>(type: "bigint", nullable: false),
                    Direction = table.Column<byte>(type: "smallint", nullable: false),
                    Taxability = table.Column<byte>(type: "smallint", nullable: false),
                    IgstMilliPercent = table.Column<int>(type: "integer", nullable: false),
                    CessMilliPercent = table.Column<int>(type: "integer", nullable: false),
                    HsnSac = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    CgstMinor = table.Column<long>(type: "bigint", nullable: false),
                    StateTaxMinor = table.Column<long>(type: "bigint", nullable: false),
                    IgstMinor = table.Column<long>(type: "bigint", nullable: false),
                    CessMinor = table.Column<long>(type: "bigint", nullable: false),
                    VoucherDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsPosted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoucherItemLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoucherItemLines_Godowns_GodownId",
                        column: x => x.GodownId,
                        principalTable: "Godowns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VoucherItemLines_Ledgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VoucherItemLines_StockItems_StockItemId",
                        column: x => x.StockItemId,
                        principalTable: "StockItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VoucherItemLines_Vouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "Vouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vouchers_PartyLedgerId",
                table: "Vouchers",
                column: "PartyLedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_Godowns_CompanyId_NormalizedName",
                table: "Godowns",
                columns: new[] { "CompanyId", "NormalizedName" },
                unique: true,
                filter: "\"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_CompanyId_NormalizedName",
                table: "StockItems",
                columns: new[] { "CompanyId", "NormalizedName" },
                unique: true,
                filter: "\"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_OpeningGodownId",
                table: "StockItems",
                column: "OpeningGodownId");

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_UnitId",
                table: "StockItems",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Units_CompanyId_NormalizedSymbol",
                table: "Units",
                columns: new[] { "CompanyId", "NormalizedSymbol" },
                unique: true,
                filter: "\"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherItemLines_CompanyId_StockItemId_VoucherDate",
                table: "VoucherItemLines",
                columns: new[] { "CompanyId", "StockItemId", "VoucherDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VoucherItemLines_CompanyId_VoucherDate",
                table: "VoucherItemLines",
                columns: new[] { "CompanyId", "VoucherDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VoucherItemLines_GodownId",
                table: "VoucherItemLines",
                column: "GodownId");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherItemLines_LedgerId",
                table: "VoucherItemLines",
                column: "LedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherItemLines_StockItemId",
                table: "VoucherItemLines",
                column: "StockItemId");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherItemLines_VoucherId_LineNo",
                table: "VoucherItemLines",
                columns: new[] { "VoucherId", "LineNo" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Vouchers_Ledgers_PartyLedgerId",
                table: "Vouchers",
                column: "PartyLedgerId",
                principalTable: "Ledgers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Vouchers_Ledgers_PartyLedgerId",
                table: "Vouchers");

            migrationBuilder.DropTable(
                name: "VoucherItemLines");

            migrationBuilder.DropTable(
                name: "StockItems");

            migrationBuilder.DropTable(
                name: "Godowns");

            migrationBuilder.DropTable(
                name: "Units");

            migrationBuilder.DropIndex(
                name: "IX_Vouchers_PartyLedgerId",
                table: "Vouchers");

            migrationBuilder.DropColumn(
                name: "PartyLedgerId",
                table: "Vouchers");

            migrationBuilder.DropColumn(
                name: "PlaceOfSupplyStateCode",
                table: "Vouchers");

            migrationBuilder.DropColumn(
                name: "SupplyNature",
                table: "Vouchers");

            migrationBuilder.DropColumn(
                name: "GstRatesJson",
                table: "Ledgers");

            migrationBuilder.DropColumn(
                name: "GstTaxType",
                table: "Ledgers");

            migrationBuilder.DropColumn(
                name: "GstRegistration",
                table: "Companies");
        }
    }
}
