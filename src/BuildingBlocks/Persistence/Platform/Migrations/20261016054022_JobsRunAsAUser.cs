using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// Jobs that work for a user. The worker used to run every job as nobody, so row-level security would hide every user-owned row from
/// a handler; a job now carries the user it works for and the worker runs it with that user as the current user. <c>platform.enqueue_job</c>
/// gets a <c>p_user_id</c> parameter (the old nine-argument version is dropped) with one rule that keeps the column from becoming a
/// privilege escalation: when the caller is an API login, the user must be the one making the request, so a compromised API cannot
/// queue work as someone else. The worker's own logins (schedulers fanning work out per user) may name any user. The function's search
/// path now ends with <c>pg_temp</c>, so a temporary object cannot shadow a catalog or platform one inside this security-definer code.
/// </summary>
/// <inheritdoc />
public partial class JobsRunAsAUser : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "user_id",
            schema: "platform",
            table: "job_queue",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql("""
            DROP FUNCTION platform.enqueue_job(uuid, text, text, jsonb, timestamptz, integer, text, text, timestamptz);

            CREATE FUNCTION platform.enqueue_job(
                p_id uuid, p_job_key text, p_type text, p_payload jsonb, p_available_at timestamptz,
                p_max_attempts integer, p_trace_id text, p_correlation_id text, p_created_at timestamptz, p_user_id uuid)
            RETURNS boolean
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, platform, pg_temp
            AS $$
            DECLARE
                v_count integer;
            BEGIN
                IF p_user_id IS NOT NULL
                   AND pg_has_role(session_user, 'api_role', 'member')
                   AND p_user_id IS DISTINCT FROM NULLIF(current_setting('app.current_user_id', true), '')::uuid THEN
                    RAISE EXCEPTION 'a job can only be enqueued for the user making the request'
                        USING ERRCODE = 'insufficient_privilege';
                END IF;

                INSERT INTO platform.job_queue
                    (id, job_key, type, payload, state, attempts, max_attempts, available_at, trace_id, correlation_id, user_id, created_at)
                VALUES
                    (p_id, p_job_key, p_type, p_payload, 'pending', 0, p_max_attempts, p_available_at, p_trace_id, p_correlation_id, p_user_id, p_created_at)
                ON CONFLICT (job_key) DO NOTHING;
                GET DIAGNOSTICS v_count = ROW_COUNT;
                RETURN v_count = 1;
            END
            $$;

            REVOKE ALL ON FUNCTION platform.enqueue_job(uuid, text, text, jsonb, timestamptz, integer, text, text, timestamptz, uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION platform.enqueue_job(uuid, text, text, jsonb, timestamptz, integer, text, text, timestamptz, uuid) TO api_role, worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
