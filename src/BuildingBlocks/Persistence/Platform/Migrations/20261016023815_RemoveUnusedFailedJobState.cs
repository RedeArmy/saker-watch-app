using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// <c>platform.job_queue</c> allowed a state, <c>failed</c>, that nothing writes and nothing reads: a failing job goes back to
/// <c>pending</c> or ends <c>dead</c>, and retention and the queue gauges only look at those and <c>succeeded</c>. A row parked in
/// <c>failed</c> would never be retried, purged or counted, so the constraint now refuses it. The constraint is dropped and added in
/// one transaction on a small table; it fails loudly if a row in that state ever existed.
/// </summary>
/// <inheritdoc />
public partial class RemoveUnusedFailedJobState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_job_queue_state",
            schema: "platform",
            table: "job_queue");

        migrationBuilder.AddCheckConstraint(
            name: "ck_job_queue_state",
            schema: "platform",
            table: "job_queue",
            sql: "state IN ('pending', 'leased', 'succeeded', 'dead')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
