using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundLifecycleWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lifecycle_event_capture_state",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ChangedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lifecycle_event_capture_state", x => x.Id);
                    table.CheckConstraint("CK_lifecycle_event_capture_state_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "lifecycle_events",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SourceTable = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: true),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                    WorkflowVersion = table.Column<int>(type: "integer", nullable: true),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    UserTaskId = table.Column<long>(type: "bigint", nullable: true),
                    Body = table.Column<byte[]>(type: "bytea", nullable: false),
                    RecipientUserIds = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    TargetRevisionIds = table.Column<long[]>(type: "bigint[]", nullable: false),
                    FanoutCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lifecycle_events", x => x.Id);
                    table.CheckConstraint("CK_lifecycle_events_body", "octet_length(\"Body\") BETWEEN 2 AND 16384");
                    table.CheckConstraint("CK_lifecycle_events_schema_version", "\"SchemaVersion\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "webhook_data_protection_keys",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_data_protection_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_management_audit",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_management_audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<long>(type: "bigint", nullable: false),
                    IsTest = table.Column<bool>(type: "boolean", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetryGeneration = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    GenerationStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GenerationAttempts = table.Column<int>(type: "integer", nullable: false),
                    LifetimeAttempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeasedFromState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastHttpStatus = table.Column<int>(type: "integer", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                    table.CheckConstraint("CK_webhook_deliveries_counters", "\"RetryGeneration\" >= 1 AND \"GenerationAttempts\" >= 0 AND \"LifetimeAttempts\" >= \"GenerationAttempts\" AND \"LeaseGeneration\" >= 0 AND \"Revision\" > 0");
                    table.CheckConstraint("CK_webhook_deliveries_failure", "\"State\" <> 'failed' OR \"FailureCode\" IS NOT NULL");
                    table.CheckConstraint("CK_webhook_deliveries_generation", "(\"GenerationStartedAt\" IS NULL) = (\"DeadlineAt\" IS NULL)");
                    table.CheckConstraint("CK_webhook_deliveries_lease", "(\"State\" = 'delivering' AND \"LeaseToken\" IS NOT NULL AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL AND \"LeasedFromState\" IN ('pending', 'retryScheduled')) OR (\"State\" <> 'delivering' AND \"LeaseToken\" IS NULL AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresAt\" IS NULL AND \"LeasedFromState\" IS NULL)");
                    table.CheckConstraint("CK_webhook_deliveries_settlement", "(\"State\" IN ('succeeded', 'discarded')) = (\"SettledAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_webhook_deliveries_state", "\"State\" IN ('pending', 'retryScheduled', 'delivering', 'succeeded', 'failed', 'discarded')");
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_lifecycle_events_EventId",
                        column: x => x.EventId,
                        principalSchema: "flowbit",
                        principalTable: "lifecycle_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_attempts",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    RetryGeneration = table.Column<int>(type: "integer", nullable: false),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    SigningKeyIds = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SendIntentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_delivery_attempts", x => x.Id);
                    table.CheckConstraint("CK_webhook_delivery_attempts_completion", "(\"CompletedAt\" IS NULL) = (\"Outcome\" IS NULL)");
                    table.CheckConstraint("CK_webhook_delivery_attempts_numbers", "\"AttemptNumber\" > 0 AND \"RetryGeneration\" > 0 AND \"LeaseGeneration\" > 0");
                    table.ForeignKey(
                        name: "FK_webhook_delivery_attempts_webhook_deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "flowbit",
                        principalTable: "webhook_deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_signing_keys",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtectedSecret = table.Column<byte[]>(type: "bytea", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OverlapExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemoveSecretNotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SecretRemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_signing_keys", x => x.Id);
                    table.CheckConstraint("CK_webhook_signing_keys_overlap", "\"Status\" <> 'previous' OR \"OverlapExpiresAt\" IS NOT NULL");
                    table.CheckConstraint("CK_webhook_signing_keys_secret", "(\"SecretRemovedAt\" IS NULL) = (\"ProtectedSecret\" IS NOT NULL) AND (\"ProtectedSecret\" IS NOT NULL OR \"Status\" NOT IN ('current', 'previous'))");
                    table.CheckConstraint("CK_webhook_signing_keys_status", "\"Status\" IN ('current', 'previous', 'revoked', 'expired', 'retired')");
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscription_dispatch_state",
                schema: "flowbit",
                columns: table => new
                {
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    BackoffStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BackoffUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BackoffSeconds = table.Column<int>(type: "integer", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastHttpStatus = table.Column<int>(type: "integer", nullable: true),
                    LastOutcomeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscription_dispatch_state", x => x.SubscriptionId);
                    table.CheckConstraint("CK_webhook_subscription_dispatch_state_backoff", "(\"BackoffUntil\" IS NULL) = (\"BackoffStartedAt\" IS NULL)");
                    table.CheckConstraint("CK_webhook_subscription_dispatch_state_counters", "\"ConsecutiveFailures\" >= 0 AND \"BackoffSeconds\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscription_revisions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationRevision = table.Column<int>(type: "integer", nullable: false),
                    EndpointUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    EventTypes = table.Column<List<string>>(type: "text[]", nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscription_revisions", x => x.Id);
                    table.CheckConstraint("CK_webhook_subscription_revisions_configuration_revision", "\"ConfigurationRevision\" > 0");
                    table.CheckConstraint("CK_webhook_subscription_revisions_event_types", "cardinality(\"EventTypes\") > 0");
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CurrentRevisionId = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PauseReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MaxConcurrency = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ArchiveSettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscriptions", x => x.Id);
                    table.CheckConstraint("CK_webhook_subscriptions_archive", "(\"State\" = 'archived' AND \"ArchivedAt\" IS NOT NULL AND \"ArchivedBy\" IS NOT NULL) OR (\"State\" <> 'archived' AND \"ArchivedAt\" IS NULL AND \"ArchivedBy\" IS NULL AND \"ArchiveSettledAt\" IS NULL)");
                    table.CheckConstraint("CK_webhook_subscriptions_max_concurrency", "\"MaxConcurrency\" BETWEEN 1 AND 16");
                    table.CheckConstraint("CK_webhook_subscriptions_pause_reason", "(\"State\" = 'paused' AND \"PauseReason\" IN ('administrator', 'receiverGone', 'receiverUnavailable')) OR (\"State\" <> 'paused' AND \"PauseReason\" IS NULL)");
                    table.CheckConstraint("CK_webhook_subscriptions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_webhook_subscriptions_state", "\"State\" IN ('active', 'paused', 'archived')");
                    table.ForeignKey(
                        name: "FK_webhook_subscriptions_webhook_subscription_revisions_Curren~",
                        column: x => x.CurrentRevisionId,
                        principalSchema: "flowbit",
                        principalTable: "webhook_subscription_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lifecycle_events_InstanceId_OccurredAt",
                schema: "flowbit",
                table: "lifecycle_events",
                columns: new[] { "InstanceId", "OccurredAt" },
                filter: "\"InstanceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lifecycle_events_OccurredAt_Id",
                schema: "flowbit",
                table: "lifecycle_events",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_lifecycle_events_pending_fanout",
                schema: "flowbit",
                table: "lifecycle_events",
                columns: new[] { "RecordedAt", "Id" },
                filter: "\"FanoutCompletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lifecycle_events_SourceTable_SourceId",
                schema: "flowbit",
                table: "lifecycle_events",
                columns: new[] { "SourceTable", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_due",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "SubscriptionId", "DueAt" },
                filter: "\"State\" IN ('pending', 'retryScheduled')");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_EventId_SubscriptionId",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "EventId", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_InstanceId_OccurredAt",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "InstanceId", "OccurredAt" },
                filter: "\"InstanceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_leases",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "SubscriptionId", "LeaseExpiresAt" },
                filter: "\"State\" = 'delivering'");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_OccurredAt_Id",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_outstanding_test",
                schema: "flowbit",
                table: "webhook_deliveries",
                column: "SubscriptionId",
                filter: "\"IsTest\" AND \"State\" IN ('pending', 'retryScheduled', 'delivering')");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_RevisionId_State",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "RevisionId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_settled",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "SettledAt", "Id" },
                filter: "\"State\" IN ('succeeded', 'discarded')");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_SubscriptionId_OccurredAt_Id",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "SubscriptionId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_SubscriptionId_State",
                schema: "flowbit",
                table: "webhook_deliveries",
                columns: new[] { "SubscriptionId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_delivery_attempts_DeliveryId_AttemptNumber",
                schema: "flowbit",
                table: "webhook_delivery_attempts",
                columns: new[] { "DeliveryId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_management_audit_OccurredAt_Id",
                schema: "flowbit",
                table: "webhook_management_audit",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_management_audit_SubscriptionId_Id",
                schema: "flowbit",
                table: "webhook_management_audit",
                columns: new[] { "SubscriptionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_signing_keys_current",
                schema: "flowbit",
                table: "webhook_signing_keys",
                column: "SubscriptionId",
                unique: true,
                filter: "\"Status\" = 'current'");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_signing_keys_pending_removal",
                schema: "flowbit",
                table: "webhook_signing_keys",
                column: "RemoveSecretNotBefore",
                filter: "\"ProtectedSecret\" IS NOT NULL AND \"Status\" NOT IN ('current', 'previous')");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_signing_keys_previous",
                schema: "flowbit",
                table: "webhook_signing_keys",
                column: "SubscriptionId",
                unique: true,
                filter: "\"Status\" = 'previous'");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_signing_keys_SubscriptionId_CreatedAt",
                schema: "flowbit",
                table: "webhook_signing_keys",
                columns: new[] { "SubscriptionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscription_dispatch_state_LastAcquiredAt",
                schema: "flowbit",
                table: "webhook_subscription_dispatch_state",
                column: "LastAcquiredAt");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscription_revisions_EventTypes",
                schema: "flowbit",
                table: "webhook_subscription_revisions",
                column: "EventTypes")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscription_revisions_SubscriptionId_Configuration~",
                schema: "flowbit",
                table: "webhook_subscription_revisions",
                columns: new[] { "SubscriptionId", "ConfigurationRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_active_revision",
                schema: "flowbit",
                table: "webhook_subscriptions",
                column: "CurrentRevisionId",
                unique: true,
                filter: "\"State\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_CreatedAt_Id",
                schema: "flowbit",
                table: "webhook_subscriptions",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_State",
                schema: "flowbit",
                table: "webhook_subscriptions",
                column: "State");

            migrationBuilder.AddForeignKey(
                name: "FK_webhook_deliveries_webhook_subscription_revisions_RevisionId",
                schema: "flowbit",
                table: "webhook_deliveries",
                column: "RevisionId",
                principalSchema: "flowbit",
                principalTable: "webhook_subscription_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_webhook_deliveries_webhook_subscriptions_SubscriptionId",
                schema: "flowbit",
                table: "webhook_deliveries",
                column: "SubscriptionId",
                principalSchema: "flowbit",
                principalTable: "webhook_subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_webhook_signing_keys_webhook_subscriptions_SubscriptionId",
                schema: "flowbit",
                table: "webhook_signing_keys",
                column: "SubscriptionId",
                principalSchema: "flowbit",
                principalTable: "webhook_subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_webhook_subscription_dispatch_state_webhook_subscriptions_S~",
                schema: "flowbit",
                table: "webhook_subscription_dispatch_state",
                column: "SubscriptionId",
                principalSchema: "flowbit",
                principalTable: "webhook_subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_webhook_subscription_revisions_webhook_subscriptions_Subscr~",
                schema: "flowbit",
                table: "webhook_subscription_revisions",
                column: "SubscriptionId",
                principalSchema: "flowbit",
                principalTable: "webhook_subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Capture starts switched off; there is no historical backfill.
            migrationBuilder.Sql("""
                INSERT INTO flowbit.lifecycle_event_capture_state ("Id", "Enabled", "ChangedAt", "ChangedBy")
                VALUES (1, FALSE, now(), NULL)
                ON CONFLICT ("Id") DO NOTHING;

                INSERT INTO flowbit.retention_policies
                    ("Category", "RetentionDays", "Revision", "UpdatedAt", "UpdatedBy", "IsInitialized")
                VALUES
                    ('lifecycleEvents', NULL, 1, now(), NULL, TRUE),
                    ('webhookDeliveries', NULL, 1, now(), NULL, TRUE)
                ON CONFLICT ("Category") DO NOTHING;
                """);
            migrationBuilder.Sql(LifecycleEventFunctionsSql);
            migrationBuilder.Sql(CaptureFunctionsSql);
            // CREATE TRIGGER briefly blocks writes to the four source tables.
            migrationBuilder.Sql(CaptureTriggersSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_workflow_instances_lifecycle_insert" ON flowbit.workflow_instances;
                DROP TRIGGER IF EXISTS "TR_workflow_instances_lifecycle_status" ON flowbit.workflow_instances;
                DROP TRIGGER IF EXISTS "TR_user_tasks_lifecycle_insert" ON flowbit.user_tasks;
                DROP TRIGGER IF EXISTS "TR_user_tasks_lifecycle_update" ON flowbit.user_tasks;
                DROP TRIGGER IF EXISTS "TR_workflow_incidents_lifecycle_insert" ON flowbit.workflow_incidents;
                DROP TRIGGER IF EXISTS "TR_user_delegations_lifecycle_insert" ON flowbit.user_delegations;
                DROP FUNCTION IF EXISTS flowbit.capture_workflow_instance_lifecycle();
                DROP FUNCTION IF EXISTS flowbit.capture_user_task_lifecycle();
                DROP FUNCTION IF EXISTS flowbit.capture_workflow_incident_lifecycle();
                DROP FUNCTION IF EXISTS flowbit.capture_user_delegation_lifecycle();
                DROP FUNCTION IF EXISTS flowbit.record_webhook_test_event(uuid);
                DROP FUNCTION IF EXISTS flowbit.set_lifecycle_event_capture(boolean, text);
                DROP FUNCTION IF EXISTS flowbit.record_lifecycle_event(text, timestamptz, text, bigint, text, bigint, bigint, bigint, text, text[], bigint[], timestamptz);
                DROP FUNCTION IF EXISTS flowbit.lifecycle_event_targets(text, text);
                DROP FUNCTION IF EXISTS flowbit.lifecycle_capture_wanted(text[]);
                DROP FUNCTION IF EXISTS flowbit.lifecycle_json_text(text);
                DROP FUNCTION IF EXISTS flowbit.lifecycle_event_timestamp(timestamptz);
                DROP FUNCTION IF EXISTS flowbit.uuid_v7();
                DELETE FROM flowbit.retention_policies WHERE "Category" IN ('lifecycleEvents', 'webhookDeliveries');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_webhook_subscriptions_webhook_subscription_revisions_Curren~",
                schema: "flowbit",
                table: "webhook_subscriptions");

            migrationBuilder.DropTable(
                name: "lifecycle_event_capture_state",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_data_protection_keys",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_delivery_attempts",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_management_audit",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_signing_keys",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_subscription_dispatch_state",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "lifecycle_events",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_subscription_revisions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions",
                schema: "flowbit");
        }

        // Shared helpers. Event bodies are built as compact JSON text in catalog field order,
        // so the stored UTF-8 bytes are canonical; jsonb would reorder keys.
        private const string LifecycleEventFunctionsSql = """
            CREATE OR REPLACE FUNCTION flowbit.uuid_v7()
            RETURNS uuid
            LANGUAGE sql
            VOLATILE
            PARALLEL SAFE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
                -- A random version 4 UUID whose first 48 bits become the Unix time in
                -- milliseconds and whose version nibble becomes 7. PostgreSQL 18 adds uuidv7().
                SELECT encode(
                    set_bit(
                        set_bit(
                            overlay(uuid_send(gen_random_uuid())
                                PLACING substring(int8send(floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3)
                                FROM 1 FOR 6),
                            52, 1),
                        53, 1),
                    'hex')::uuid;
            $function$;

            CREATE OR REPLACE FUNCTION flowbit.lifecycle_event_timestamp(value timestamptz)
            RETURNS text
            LANGUAGE sql
            STABLE
            PARALLEL SAFE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
                SELECT to_char(value AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"');
            $function$;

            CREATE OR REPLACE FUNCTION flowbit.lifecycle_json_text(value text)
            RETURNS text
            LANGUAGE sql
            IMMUTABLE
            PARALLEL SAFE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
                SELECT COALESCE(to_json(value)::text, 'null');
            $function$;

            -- The capture triggers' gate: the database switch, the transaction-local repair
            -- setting, and whether any target could match one of the candidate types. A
            -- deployment without subscriptions stops here and writes nothing. UI-13 extends
            -- this function and lifecycle_event_targets with its internal target.
            CREATE OR REPLACE FUNCTION flowbit.lifecycle_capture_wanted(event_types text[])
            RETURNS boolean
            LANGUAGE plpgsql
            STABLE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            BEGIN
                IF lower(COALESCE(current_setting('flowbit.lifecycle_events', true), '')) = 'off' THEN
                    RETURN false;
                END IF;
                RETURN EXISTS (
                        SELECT 1
                        FROM flowbit.lifecycle_event_capture_state capture
                        WHERE capture."Id" = 1 AND capture."Enabled")
                    AND EXISTS (
                        SELECT 1
                        FROM flowbit.webhook_subscriptions subscription
                        JOIN flowbit.webhook_subscription_revisions revision
                          ON revision."Id" = subscription."CurrentRevisionId"
                        WHERE subscription."State" = 'active'
                          AND revision."EventTypes" && event_types);
            END;
            $function$;

            -- Current revisions of active subscriptions matching the type and exact family;
            -- a revision without a workflow key matches every family. Read without locks.
            CREATE OR REPLACE FUNCTION flowbit.lifecycle_event_targets(event_type text, workflow_key text)
            RETURNS bigint[]
            LANGUAGE sql
            STABLE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
                SELECT COALESCE(array_agg(revision."Id" ORDER BY revision."Id"), '{}'::bigint[])
                FROM flowbit.webhook_subscriptions subscription
                JOIN flowbit.webhook_subscription_revisions revision
                  ON revision."Id" = subscription."CurrentRevisionId"
                WHERE subscription."State" = 'active'
                  AND event_type = ANY (revision."EventTypes")
                  AND (revision."WorkflowKey" IS NULL OR revision."WorkflowKey" = workflow_key);
            $function$;

            CREATE OR REPLACE FUNCTION flowbit.record_lifecycle_event(
                event_type text,
                occurred_at timestamptz,
                source_table text,
                source_id bigint,
                workflow_key text,
                workflow_definition_id bigint,
                instance_id bigint,
                user_task_id bigint,
                data_json text,
                recipient_user_ids text[],
                target_revision_ids bigint[],
                fanout_completed_at timestamptz DEFAULT NULL)
            RETURNS uuid
            LANGUAGE plpgsql
            VOLATILE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                event_id uuid := flowbit.uuid_v7();
                workflow_version integer;
                body text;
            BEGIN
                IF workflow_definition_id IS NOT NULL THEN
                    SELECT definition."Version"
                      INTO workflow_version
                      FROM flowbit.workflow_definitions definition
                     WHERE definition."Id" = workflow_definition_id;
                END IF;
                body := '{"schemaVersion":1'
                    || ',"eventId":' || to_json(event_id::text)::text
                    || ',"type":' || to_json(event_type)::text
                    || ',"timestamp":' || to_json(flowbit.lifecycle_event_timestamp(occurred_at))::text
                    || ',"workflowKey":' || flowbit.lifecycle_json_text(workflow_key)
                    || ',"workflowDefinitionId":' || COALESCE(workflow_definition_id::text, 'null')
                    || ',"workflowVersion":' || COALESCE(workflow_version::text, 'null')
                    || ',"instanceId":' || COALESCE(instance_id::text, 'null')
                    || ',"data":' || data_json
                    || '}';
                INSERT INTO flowbit.lifecycle_events
                    ("Id", "Type", "SchemaVersion", "OccurredAt", "RecordedAt", "SourceTable", "SourceId",
                     "WorkflowKey", "WorkflowDefinitionId", "WorkflowVersion", "InstanceId", "UserTaskId",
                     "Body", "RecipientUserIds", "TargetRevisionIds", "FanoutCompletedAt")
                VALUES
                    (event_id, event_type, 1, occurred_at, clock_timestamp(), source_table, source_id,
                     workflow_key, workflow_definition_id, workflow_version, instance_id, user_task_id,
                     convert_to(body, 'UTF8'), COALESCE(recipient_user_ids, '{}'::text[]),
                     target_revision_ids, fanout_completed_at);
                -- Only a wakeup hint; polling stays authoritative. Only transactions that
                -- stored an event notify, because notifying commits are serialized.
                PERFORM pg_notify('flowbit_lifecycle_events', '');
                RETURN event_id;
            END;
            $function$;

            -- The documented rollout and emergency-stop switch. Capture applies to
            -- transitions committed after this function's own transaction commits.
            CREATE OR REPLACE FUNCTION flowbit.set_lifecycle_event_capture(capture_enabled boolean, operator_name text)
            RETURNS void
            LANGUAGE plpgsql
            VOLATILE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                changed integer;
            BEGIN
                IF capture_enabled IS NULL THEN
                    RAISE EXCEPTION 'capture_enabled must be true or false.';
                END IF;
                IF operator_name IS NULL OR btrim(operator_name) = '' OR char_length(btrim(operator_name)) > 300 THEN
                    RAISE EXCEPTION 'operator_name must be a nonblank name of at most 300 characters.';
                END IF;
                UPDATE flowbit.lifecycle_event_capture_state
                   SET "Enabled" = capture_enabled,
                       "ChangedAt" = now(),
                       "ChangedBy" = btrim(operator_name)
                 WHERE "Id" = 1
                   AND "Enabled" IS DISTINCT FROM capture_enabled;
                GET DIAGNOSTICS changed = ROW_COUNT;
                IF changed > 0 THEN
                    INSERT INTO flowbit.webhook_management_audit ("Operation", "Actor", "OccurredAt", "ChangesJson")
                    VALUES (
                        CASE WHEN capture_enabled THEN 'captureEnabled' ELSE 'captureDisabled' END,
                        btrim(operator_name),
                        now(),
                        jsonb_build_object('enabled', capture_enabled));
                END IF;
            END;
            $function$;

            -- Queues one signed webhook.test event for one subscription only. The caller holds
            -- the subscription row lock and has checked its state and outstanding tests.
            CREATE OR REPLACE FUNCTION flowbit.record_webhook_test_event(subscription_id uuid)
            RETURNS uuid
            LANGUAGE plpgsql
            VOLATILE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                revision_id bigint;
                subscription_state text;
                event_id uuid;
                delivery_id uuid := flowbit.uuid_v7();
                occurred timestamptz := clock_timestamp();
            BEGIN
                SELECT subscription."CurrentRevisionId", subscription."State"
                  INTO revision_id, subscription_state
                  FROM flowbit.webhook_subscriptions subscription
                 WHERE subscription."Id" = subscription_id;
                IF revision_id IS NULL THEN
                    RAISE EXCEPTION 'Webhook subscription % does not exist.', subscription_id
                        USING ERRCODE = 'no_data_found';
                END IF;
                IF subscription_state = 'archived' THEN
                    RAISE EXCEPTION 'Webhook subscription % is archived.', subscription_id
                        USING ERRCODE = 'object_not_in_prerequisite_state';
                END IF;
                event_id := flowbit.record_lifecycle_event(
                    'webhook.test', occurred, 'webhook_subscriptions', NULL, NULL, NULL, NULL, NULL,
                    '{"subscriptionId":' || to_json(subscription_id::text)::text || '}',
                    '{}'::text[], ARRAY[revision_id], occurred);
                INSERT INTO flowbit.webhook_deliveries
                    ("Id", "EventId", "SubscriptionId", "RevisionId", "IsTest", "EventType", "WorkflowKey",
                     "InstanceId", "OccurredAt", "State", "DueAt", "RetryGeneration", "GenerationAttempts",
                     "LifetimeAttempts", "LeaseGeneration", "Revision", "CreatedAt", "UpdatedAt")
                VALUES
                    (delivery_id, event_id, subscription_id, revision_id, TRUE, 'webhook.test', NULL,
                     NULL, occurred, 'pending', occurred, 1, 0, 0, 0, 1, occurred, occurred);
                RETURN delivery_id;
            END;
            $function$;
            """;

        // Deferred calls run at commit, in the order their row changes happened, after rows
        // written later in the transaction (such as node executions) exist. NEW and OLD are
        // the exact row versions of each change. A failure here fails the source commit.
        // Every string copied into a body is bounded, never truncated: workflow keys and task
        // external ids by their 300-character columns, an incident's external id by the check in
        // capture_workflow_incident_lifecycle(). At six escaped bytes per character at most, a
        // body stays under 5 KiB, far below CK_lifecycle_events_body.
        private const string CaptureFunctionsSql = """
            CREATE OR REPLACE FUNCTION flowbit.capture_workflow_instance_lifecycle()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                candidate_types text[] := '{}'::text[];
                started boolean := false;
                terminal_type text;
                reactivated boolean := false;
                definition_id bigint;
                targets bigint[];
            BEGIN
                terminal_type := CASE NEW."Status"
                    WHEN 'completed' THEN 'instance.completed'
                    WHEN 'faulted' THEN 'instance.faulted'
                    WHEN 'cancelled' THEN 'instance.cancelled'
                END;
                IF TG_OP = 'INSERT' THEN
                    started := true;
                ELSE
                    IF OLD."Status" IS NOT DISTINCT FROM NEW."Status" THEN
                        terminal_type := NULL;
                    END IF;
                    reactivated := NEW."Status" = 'running' AND OLD."Status" IN ('completed', 'cancelled');
                END IF;
                IF started THEN
                    candidate_types := candidate_types || 'instance.started'::text;
                END IF;
                IF terminal_type IS NOT NULL THEN
                    candidate_types := candidate_types || terminal_type;
                END IF;
                IF reactivated THEN
                    candidate_types := candidate_types || 'instance.reactivated'::text;
                END IF;
                IF cardinality(candidate_types) = 0
                   OR NOT flowbit.lifecycle_capture_wanted(candidate_types) THEN
                    RETURN NULL;
                END IF;

                -- The definition applying when the transition commits.
                SELECT instance."WorkflowDefinitionId"
                  INTO definition_id
                  FROM flowbit.workflow_instances instance
                 WHERE instance."Id" = NEW."Id";
                definition_id := COALESCE(definition_id, NEW."WorkflowDefinitionId");

                IF started THEN
                    targets := flowbit.lifecycle_event_targets('instance.started', NEW."WorkflowKey");
                    IF cardinality(targets) > 0 THEN
                        PERFORM flowbit.record_lifecycle_event(
                            'instance.started', NEW."CreatedAt", 'workflow_instances', NEW."Id",
                            NEW."WorkflowKey", definition_id, NEW."Id", NULL,
                            '{"status":"running"}', '{}'::text[], targets);
                    END IF;
                END IF;
                IF terminal_type IS NOT NULL THEN
                    targets := flowbit.lifecycle_event_targets(terminal_type, NEW."WorkflowKey");
                    IF cardinality(targets) > 0 THEN
                        PERFORM flowbit.record_lifecycle_event(
                            terminal_type, COALESCE(NEW."FinishedAt", NEW."UpdatedAt"), 'workflow_instances', NEW."Id",
                            NEW."WorkflowKey", definition_id, NEW."Id", NULL,
                            '{"status":' || to_json(NEW."Status"::text)::text || '}', '{}'::text[], targets);
                    END IF;
                END IF;
                IF reactivated THEN
                    targets := flowbit.lifecycle_event_targets('instance.reactivated', NEW."WorkflowKey");
                    IF cardinality(targets) > 0 THEN
                        PERFORM flowbit.record_lifecycle_event(
                            'instance.reactivated', NEW."UpdatedAt", 'workflow_instances', NEW."Id",
                            NEW."WorkflowKey", definition_id, NEW."Id", NULL,
                            '{"status":"running","previousStatus":' || to_json(OLD."Status"::text)::text || '}',
                            '{}'::text[], targets);
                    END IF;
                END IF;
                RETURN NULL;
            END;
            $function$;

            CREATE OR REPLACE FUNCTION flowbit.capture_user_task_lifecycle()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                old_status text;
                old_assignee text;
                inserted boolean := TG_OP = 'INSERT';
                emit_created boolean;
                emit_assigned boolean;
                emit_completed boolean;
                emit_cancelled boolean;
                assigned_status text;
                candidate_types text[] := '{}'::text[];
                workflow_key text;
                definition_id bigint;
                node_execution_id bigint;
                common_data text;
                change_time timestamptz;
                settle_time timestamptz;
                targets bigint[];
            BEGIN
                IF NOT inserted THEN
                    old_status := OLD."Status";
                    old_assignee := OLD."Assignee";
                END IF;
                -- Inserted active, activated from pending, or completed directly from pending.
                emit_created := NEW."Status" IN ('active', 'completed')
                    AND (old_status IS NULL OR old_status = 'pending');
                assigned_status := CASE WHEN emit_created THEN 'active' ELSE COALESCE(old_status, NEW."Status") END;
                emit_assigned := COALESCE(
                    NEW."Assignee" IS NOT NULL
                        AND (old_assignee IS NULL OR lower(old_assignee) <> lower(NEW."Assignee"))
                        AND assigned_status IN ('pending', 'active'),
                    false);
                emit_completed := NEW."Status" = 'completed' AND old_status IS DISTINCT FROM 'completed';
                -- A pending item without an assignee was never announced.
                emit_cancelled := COALESCE(
                    NEW."Status" = 'cancelled'
                        AND (old_status = 'active'
                             OR (old_status = 'pending' AND (old_assignee IS NOT NULL OR NEW."Assignee" IS NOT NULL))),
                    false);
                IF emit_created THEN candidate_types := candidate_types || 'task.created'::text; END IF;
                IF emit_assigned THEN candidate_types := candidate_types || 'task.assigned'::text; END IF;
                IF emit_completed THEN candidate_types := candidate_types || 'task.completed'::text; END IF;
                IF emit_cancelled THEN candidate_types := candidate_types || 'task.cancelled'::text; END IF;
                IF cardinality(candidate_types) = 0
                   OR NOT flowbit.lifecycle_capture_wanted(candidate_types) THEN
                    RETURN NULL;
                END IF;

                SELECT instance."WorkflowKey", instance."WorkflowDefinitionId"
                  INTO workflow_key, definition_id
                  FROM flowbit.workflow_instances instance
                 WHERE instance."Id" = NEW."InstanceId";
                SELECT execution."Id"
                  INTO node_execution_id
                  FROM flowbit.node_executions execution
                 WHERE execution."UserTaskId" = NEW."Id";
                common_data := '{"userTaskId":' || NEW."Id"::text
                    || ',"executionTokenId":' || NEW."TokenId"::text
                    || ',"nodeId":' || NEW."NodeId"::text
                    || ',"nodeExternalId":' || flowbit.lifecycle_json_text(NEW."NodeExternalId")
                    || ',"nodeExecutionId":' || COALESCE(node_execution_id::text, 'null')
                    || ',"multiInstanceExecutionId":' || COALESCE(NEW."MultiInstanceExecutionId"::text, 'null')
                    || ',"itemIndex":' || COALESCE(NEW."ItemIndex"::text, 'null')
                    || ',"taskStatus":';
                change_time := CASE WHEN inserted THEN NEW."CreatedAt" ELSE NEW."UpdatedAt" END;
                settle_time := COALESCE(NEW."CompletedAt", NEW."UpdatedAt");

                -- One change qualifying for several events emits them in this order.
                IF emit_created THEN
                    targets := flowbit.lifecycle_event_targets('task.created', workflow_key);
                    IF cardinality(targets) > 0 THEN
                        PERFORM flowbit.record_lifecycle_event(
                            'task.created', change_time, 'user_tasks', NEW."Id", workflow_key, definition_id,
                            NEW."InstanceId", NEW."Id", common_data || '"active"}',
                            CASE WHEN NEW."Assignee" IS NULL THEN '{}'::text[] ELSE ARRAY[NEW."Assignee"::text] END,
                            targets);
                    END IF;
                END IF;
                IF emit_assigned THEN
                    targets := flowbit.lifecycle_event_targets('task.assigned', workflow_key);
                    IF cardinality(targets) > 0 THEN
                        PERFORM flowbit.record_lifecycle_event(
                            'task.assigned', change_time, 'user_tasks', NEW."Id", workflow_key, definition_id,
                            NEW."InstanceId", NEW."Id",
                            common_data || to_json(assigned_status)::text
                                || ',"assignmentOperation":'
                                || CASE WHEN old_assignee IS NULL THEN '"assigned"' ELSE '"reassigned"' END
                                || '}',
                            ARRAY[NEW."Assignee"::text], targets);
                    END IF;
                END IF;
                IF emit_completed THEN
                    targets := flowbit.lifecycle_event_targets('task.completed', workflow_key);
                    IF cardinality(targets) > 0 THEN
                        PERFORM flowbit.record_lifecycle_event(
                            'task.completed', settle_time, 'user_tasks', NEW."Id", workflow_key, definition_id,
                            NEW."InstanceId", NEW."Id", common_data || '"completed"}', '{}'::text[], targets);
                    END IF;
                END IF;
                IF emit_cancelled THEN
                    targets := flowbit.lifecycle_event_targets('task.cancelled', workflow_key);
                    IF cardinality(targets) > 0 THEN
                        PERFORM flowbit.record_lifecycle_event(
                            'task.cancelled', settle_time, 'user_tasks', NEW."Id", workflow_key, definition_id,
                            NEW."InstanceId", NEW."Id",
                            common_data || '"cancelled","previousTaskStatus":' || to_json(old_status)::text || '}',
                            '{}'::text[], targets);
                    END IF;
                END IF;
                RETURN NULL;
            END;
            $function$;

            CREATE OR REPLACE FUNCTION flowbit.capture_workflow_incident_lifecycle()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                targets bigint[];
                token_id bigint;
                job_kind text;
                node_id integer;
                node_external_id text;
            BEGIN
                IF NEW."Status" <> 'open'
                   OR NOT flowbit.lifecycle_capture_wanted(ARRAY['incident.opened']) THEN
                    RETURN NULL;
                END IF;
                targets := flowbit.lifecycle_event_targets('incident.opened', NEW."WorkflowKey");
                IF cardinality(targets) = 0 THEN
                    RETURN NULL;
                END IF;
                SELECT job."TokenId", job."Kind"
                  INTO token_id, job_kind
                  FROM flowbit.workflow_jobs job
                 WHERE job."Id" = NEW."OriginalJobId";
                -- Version-change and variable-update batch jobs name the initial start event only to
                -- anchor their row, so no authored node applies to them. LifecycleEventCaptureTests
                -- classifies every job kind, so a new kind cannot miss this list unnoticed.
                IF NEW."NodeId" > 0
                   AND COALESCE(job_kind, '') NOT IN (
                       'instanceVersionChangeBatchPrepare', 'instanceVersionChangeBatchExecute',
                       'instanceVariableUpdateBatchPrepare', 'instanceVariableUpdateBatchExecute') THEN
                    node_id := NEW."NodeId";
                    -- Definitions do not limit external ids, but tokens and tasks store at most 300
                    -- characters, so a longer one never belongs to a node that runs. It is reported as
                    -- null rather than truncated, which keeps every body far below its 16 KiB bound.
                    SELECT node ->> 'externalId'
                      INTO node_external_id
                      FROM flowbit.workflow_definitions definition
                     CROSS JOIN LATERAL jsonb_array_elements(
                         CASE WHEN jsonb_typeof(definition."Definition" -> 'flowNodes') = 'array'
                              THEN definition."Definition" -> 'flowNodes'
                              ELSE '[]'::jsonb END) node
                     WHERE definition."Id" = NEW."WorkflowDefinitionId"
                       AND node ->> 'id' = NEW."NodeId"::text
                       AND jsonb_typeof(node -> 'externalId') = 'string'
                       AND char_length(node ->> 'externalId') <= 300
                     LIMIT 1;
                END IF;
                PERFORM flowbit.record_lifecycle_event(
                    'incident.opened', NEW."CreatedAt", 'workflow_incidents', NEW."Id", NEW."WorkflowKey",
                    NEW."WorkflowDefinitionId", NEW."InstanceId", NULL,
                    '{"incidentId":' || NEW."Id"::text
                        || ',"jobId":' || NEW."OriginalJobId"::text
                        || ',"executionTokenId":' || COALESCE(token_id::text, 'null')
                        || ',"nodeId":' || COALESCE(node_id::text, 'null')
                        || ',"nodeExternalId":' || flowbit.lifecycle_json_text(node_external_id)
                        || ',"status":"open"}',
                    '{}'::text[], targets);
                RETURN NULL;
            END;
            $function$;

            CREATE OR REPLACE FUNCTION flowbit.capture_user_delegation_lifecycle()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                targets bigint[];
            BEGIN
                IF NEW."AcceptanceState" NOT IN ('pending', 'notRequired')
                   OR NOT flowbit.lifecycle_capture_wanted(ARRAY['delegation.granted']) THEN
                    RETURN NULL;
                END IF;
                targets := flowbit.lifecycle_event_targets('delegation.granted', NEW."WorkflowKey");
                IF cardinality(targets) = 0 THEN
                    RETURN NULL;
                END IF;
                PERFORM flowbit.record_lifecycle_event(
                    'delegation.granted', NEW."CreatedAt", 'user_delegations', NEW."Id", NEW."WorkflowKey",
                    NULL, NULL, NULL,
                    '{"delegationId":' || NEW."Id"::text
                        || ',"acceptanceState":' || to_json(NEW."AcceptanceState"::text)::text || '}',
                    ARRAY[NEW."Delegate"::text], targets);
                RETURN NULL;
            END;
            $function$;
            """;

        // Column lists and WHEN clauses are evaluated when the row changes, so unrelated
        // updates never queue a call.
        private const string CaptureTriggersSql = """
            CREATE CONSTRAINT TRIGGER "TR_workflow_instances_lifecycle_insert"
            AFTER INSERT ON flowbit.workflow_instances
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION flowbit.capture_workflow_instance_lifecycle();

            CREATE CONSTRAINT TRIGGER "TR_workflow_instances_lifecycle_status"
            AFTER UPDATE OF "Status" ON flowbit.workflow_instances
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            WHEN (OLD."Status" IS DISTINCT FROM NEW."Status")
            EXECUTE FUNCTION flowbit.capture_workflow_instance_lifecycle();

            CREATE CONSTRAINT TRIGGER "TR_user_tasks_lifecycle_insert"
            AFTER INSERT ON flowbit.user_tasks
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            WHEN (NEW."Status" <> 'pending' OR NEW."Assignee" IS NOT NULL)
            EXECUTE FUNCTION flowbit.capture_user_task_lifecycle();

            CREATE CONSTRAINT TRIGGER "TR_user_tasks_lifecycle_update"
            AFTER UPDATE OF "Status", "Assignee" ON flowbit.user_tasks
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            WHEN (OLD."Status" IS DISTINCT FROM NEW."Status"
                  OR (NEW."Assignee" IS NOT NULL AND lower(OLD."Assignee") IS DISTINCT FROM lower(NEW."Assignee")))
            EXECUTE FUNCTION flowbit.capture_user_task_lifecycle();

            CREATE CONSTRAINT TRIGGER "TR_workflow_incidents_lifecycle_insert"
            AFTER INSERT ON flowbit.workflow_incidents
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            WHEN (NEW."Status" = 'open')
            EXECUTE FUNCTION flowbit.capture_workflow_incident_lifecycle();

            CREATE CONSTRAINT TRIGGER "TR_user_delegations_lifecycle_insert"
            AFTER INSERT ON flowbit.user_delegations
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            WHEN (NEW."AcceptanceState" IN ('pending', 'notRequired'))
            EXECUTE FUNCTION flowbit.capture_user_delegation_lifecycle();
            """;
    }
}
