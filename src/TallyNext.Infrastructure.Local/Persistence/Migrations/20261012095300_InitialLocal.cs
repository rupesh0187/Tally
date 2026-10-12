using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TallyNext.Infrastructure.Local.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLocal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditChainHeads",
                columns: table => new
                {
                    ChainKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditChainHeads", x => x.ChainKey);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChainSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<byte>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ChangesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ChangeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PreviousHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ChainKey = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MailingName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddressLine1 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddressLine2 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StateCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    Pincode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Country = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Gstin = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    Pan = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CurrencySymbol = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    FinancialYearStartMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    BooksBeginningDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LockedThroughDate = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRegistration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SeriesCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    ServerUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RegisteredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessfulSyncAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRegistration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutboxSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ServerConflictId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LocalPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ServerPayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ServerVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalUsers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    VerifierSalt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ProtectedVerifier = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Iterations = table.Column<int>(type: "INTEGER", nullable: false),
                    OfflineGraceDays = table.Column<int>(type: "INTEGER", nullable: false),
                    LastOnlineAuthAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OfflineAllowedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalUsers", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxEntries",
                columns: table => new
                {
                    LocalSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<byte>(type: "INTEGER", nullable: false),
                    BaseVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AcceptedVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConflictId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvesConflictId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEntries", x => x.LocalSequence);
                });

            migrationBuilder.CreateTable(
                name: "ProtectedSecrets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProtectedValue = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedSecrets", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cursor = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPulledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AccessRevoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCursors", x => x.CompanyId);
                });

            migrationBuilder.CreateTable(
                name: "AccountGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ParentGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Nature = table.Column<byte>(type: "INTEGER", nullable: false),
                    AffectsGrossProfit = table.Column<bool>(type: "INTEGER", nullable: false),
                    SystemKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountGroups_AccountGroups_ParentGroupId",
                        column: x => x.ParentGroupId,
                        principalTable: "AccountGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountGroups_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<byte>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    UserDisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyMemberships_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VoucherTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BaseType = table.Column<byte>(type: "INTEGER", nullable: false),
                    Abbreviation = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoucherTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoucherTypes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Ledgers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Alias = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpeningBalanceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    OpeningSide = table.Column<byte>(type: "INTEGER", nullable: false),
                    IsBillWise = table.Column<bool>(type: "INTEGER", nullable: false),
                    SystemKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    GstRegistrationType = table.Column<byte>(type: "INTEGER", nullable: false),
                    Gstin = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    StateCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    Pan = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    AddressLine1 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddressLine2 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Pincode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CreditPeriodDays = table.Column<int>(type: "INTEGER", nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ledgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ledgers_AccountGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AccountGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ledgers_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VoucherNumberSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VoucherTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FinancialYearStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SeriesCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    LastNumber = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoucherNumberSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoucherNumberSeries_VoucherTypes_VoucherTypeId",
                        column: x => x.VoucherTypeId,
                        principalTable: "VoucherTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Vouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VoucherTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BaseType = table.Column<byte>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FinancialYearStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    SeriesCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    SequenceNumber = table.Column<long>(type: "INTEGER", nullable: true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Narration = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReferenceDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ReversalOfVoucherId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedOnDeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PostedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vouchers_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vouchers_VoucherTypes_VoucherTypeId",
                        column: x => x.VoucherTypeId,
                        principalTable: "VoucherTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vouchers_Vouchers_ReversalOfVoucherId",
                        column: x => x.ReversalOfVoucherId,
                        principalTable: "Vouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VoucherLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VoucherId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    LedgerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Side = table.Column<byte>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Narration = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    VoucherDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsPosted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoucherLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoucherLines_Ledgers_LedgerId",
                        column: x => x.LedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VoucherLines_Vouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "Vouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountGroups_CompanyId_NormalizedName",
                table: "AccountGroups",
                columns: new[] { "CompanyId", "NormalizedName" },
                filter: "\"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_AccountGroups_CompanyId_ParentGroupId",
                table: "AccountGroups",
                columns: new[] { "CompanyId", "ParentGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountGroups_ParentGroupId",
                table: "AccountGroups",
                column: "ParentGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ChainKey_ChainSequence",
                table: "AuditEntries",
                columns: new[] { "ChainKey", "ChainSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_CompanyId_EntityType_EntityId",
                table: "AuditEntries",
                columns: new[] { "CompanyId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_CompanyId_OccurredAtUtc",
                table: "AuditEntries",
                columns: new[] { "CompanyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMemberships_CompanyId_UserId",
                table: "CompanyMemberships",
                columns: new[] { "CompanyId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMemberships_UserId",
                table: "CompanyMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_CompanyId_GroupId",
                table: "Ledgers",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_CompanyId_NormalizedName",
                table: "Ledgers",
                columns: new[] { "CompanyId", "NormalizedName" },
                filter: "\"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_GroupId",
                table: "Ledgers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalConflicts_Status_CompanyId",
                table: "LocalConflicts",
                columns: new[] { "Status", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalUsers_NormalizedEmail",
                table: "LocalUsers",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEntries_ChangeId",
                table: "OutboxEntries",
                column: "ChangeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEntries_EntityId_Status",
                table: "OutboxEntries",
                columns: new[] { "EntityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEntries_Status_LocalSequence",
                table: "OutboxEntries",
                columns: new[] { "Status", "LocalSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_VoucherLines_CompanyId_LedgerId_VoucherDate",
                table: "VoucherLines",
                columns: new[] { "CompanyId", "LedgerId", "VoucherDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VoucherLines_LedgerId",
                table: "VoucherLines",
                column: "LedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherLines_VoucherId_LineNo",
                table: "VoucherLines",
                columns: new[] { "VoucherId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoucherNumberSeries_CompanyId_VoucherTypeId_FinancialYearStart_SeriesCode",
                table: "VoucherNumberSeries",
                columns: new[] { "CompanyId", "VoucherTypeId", "FinancialYearStart", "SeriesCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoucherNumberSeries_VoucherTypeId",
                table: "VoucherNumberSeries",
                column: "VoucherTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Vouchers_CompanyId_Date_Id",
                table: "Vouchers",
                columns: new[] { "CompanyId", "Date", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Vouchers_CompanyId_IdempotencyKey",
                table: "Vouchers",
                columns: new[] { "CompanyId", "IdempotencyKey" },
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Vouchers_CompanyId_VoucherTypeId_FinancialYearStart_SeriesCode_SequenceNumber",
                table: "Vouchers",
                columns: new[] { "CompanyId", "VoucherTypeId", "FinancialYearStart", "SeriesCode", "SequenceNumber" },
                filter: "\"SequenceNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Vouchers_ReversalOfVoucherId",
                table: "Vouchers",
                column: "ReversalOfVoucherId",
                filter: "\"ReversalOfVoucherId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Vouchers_VoucherTypeId",
                table: "Vouchers",
                column: "VoucherTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherTypes_CompanyId_NormalizedName",
                table: "VoucherTypes",
                columns: new[] { "CompanyId", "NormalizedName" },
                filter: "\"IsDeleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditChainHeads");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "CompanyMemberships");

            migrationBuilder.DropTable(
                name: "DeviceRegistration");

            migrationBuilder.DropTable(
                name: "LocalConflicts");

            migrationBuilder.DropTable(
                name: "LocalUsers");

            migrationBuilder.DropTable(
                name: "OutboxEntries");

            migrationBuilder.DropTable(
                name: "ProtectedSecrets");

            migrationBuilder.DropTable(
                name: "SyncCursors");

            migrationBuilder.DropTable(
                name: "VoucherLines");

            migrationBuilder.DropTable(
                name: "VoucherNumberSeries");

            migrationBuilder.DropTable(
                name: "Ledgers");

            migrationBuilder.DropTable(
                name: "Vouchers");

            migrationBuilder.DropTable(
                name: "AccountGroups");

            migrationBuilder.DropTable(
                name: "VoucherTypes");

            migrationBuilder.DropTable(
                name: "Companies");
        }
    }
}
