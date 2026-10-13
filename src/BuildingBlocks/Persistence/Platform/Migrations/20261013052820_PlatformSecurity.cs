using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// Security of the platform schema (ADR-0010, ADR-0017): row-level security on the user-owned table, least-privilege
/// grants per role, and security-definer functions for the few operations that must cross user boundaries
/// (audit append and verification, job enqueue, expired-key purge). The audit log is append-only and hash-chained.
/// </summary>
/// <inheritdoc />
public partial class PlatformSecurity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE platform.idempotency_keys ENABLE ROW LEVEL SECURITY;
            CREATE POLICY idempotency_keys_owner ON platform.idempotency_keys
                FOR ALL TO api_role
                USING (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid)
                WITH CHECK (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid);
            """);

        migrationBuilder.Sql("""
            REVOKE ALL ON ALL TABLES IN SCHEMA platform FROM PUBLIC, api_role, worker_role;
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.idempotency_keys TO api_role;
            GRANT INSERT ON platform.outbox_messages TO api_role;
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.outbox_messages TO worker_role;
            GRANT SELECT, INSERT, DELETE ON platform.inbox_messages TO worker_role;
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.job_queue TO worker_role;
            """);

        // Hash chain: each entry hashes its content together with the previous entry's hash.
        migrationBuilder.Sql("""
            CREATE FUNCTION platform.audit_entry_hash(
                p_prev bytea, p_occurred_at timestamptz, p_event_type text, p_actor_ref text,
                p_subject_ref text, p_details jsonb, p_trace_id text, p_correlation_id text)
            RETURNS bytea
            LANGUAGE sql
            STABLE
            SET search_path = pg_catalog
            AS $$
                SELECT sha256(convert_to(concat_ws('|',
                    coalesce(encode(p_prev, 'hex'), ''),
                    to_char(p_occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'),
                    p_event_type, p_actor_ref, coalesce(p_subject_ref, ''), p_details::text,
                    coalesce(p_trace_id, ''), coalesce(p_correlation_id, '')), 'UTF8'))
            $$;

            CREATE FUNCTION platform.audit_log_reject_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'platform.audit_log is append-only' USING ERRCODE = 'insufficient_privilege';
            END
            $$;

            CREATE TRIGGER audit_log_no_update_delete BEFORE UPDATE OR DELETE ON platform.audit_log
                FOR EACH ROW EXECUTE FUNCTION platform.audit_log_reject_change();
            CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON platform.audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION platform.audit_log_reject_change();
            """);

        migrationBuilder.Sql("""
            CREATE FUNCTION platform.append_audit(
                p_occurred_at timestamptz, p_event_type text, p_actor_ref text, p_subject_ref text,
                p_details jsonb, p_trace_id text, p_correlation_id text)
            RETURNS bigint
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, platform
            AS $$
            DECLARE
                v_prev bytea;
                v_id bigint;
            BEGIN
                -- One writer at a time keeps the chain linear; the lock is released at commit.
                PERFORM pg_advisory_xact_lock(727001);
                SELECT hash INTO v_prev FROM platform.audit_log ORDER BY id DESC LIMIT 1;
                INSERT INTO platform.audit_log
                    (occurred_at, event_type, actor_ref, subject_ref, details, trace_id, correlation_id, prev_hash, hash)
                VALUES
                    (p_occurred_at, p_event_type, p_actor_ref, p_subject_ref, p_details, p_trace_id, p_correlation_id, v_prev,
                     platform.audit_entry_hash(v_prev, p_occurred_at, p_event_type, p_actor_ref, p_subject_ref, p_details, p_trace_id, p_correlation_id))
                RETURNING id INTO v_id;
                RETURN v_id;
            END
            $$;

            -- Returns the id of the first entry whose link or hash does not verify, or NULL when the chain is intact.
            -- Verification starts at the oldest remaining entry so old partitions can be dropped by retention.
            CREATE FUNCTION platform.verify_audit_chain()
            RETURNS bigint
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, platform
            AS $$
            DECLARE
                r record;
                v_prev bytea;
                v_first boolean := true;
            BEGIN
                FOR r IN SELECT * FROM platform.audit_log ORDER BY id LOOP
                    IF v_first THEN
                        v_prev := r.prev_hash;
                        v_first := false;
                    END IF;
                    IF r.prev_hash IS DISTINCT FROM v_prev THEN
                        RETURN r.id;
                    END IF;
                    IF r.hash IS DISTINCT FROM platform.audit_entry_hash(
                        r.prev_hash, r.occurred_at, r.event_type, r.actor_ref, r.subject_ref, r.details, r.trace_id, r.correlation_id) THEN
                        RETURN r.id;
                    END IF;
                    v_prev := r.hash;
                END LOOP;
                RETURN NULL;
            END
            $$;
            """);

        migrationBuilder.Sql("""
            CREATE FUNCTION platform.enqueue_job(
                p_id uuid, p_job_key text, p_type text, p_payload jsonb, p_available_at timestamptz,
                p_max_attempts integer, p_trace_id text, p_correlation_id text, p_created_at timestamptz)
            RETURNS boolean
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, platform
            AS $$
            DECLARE
                v_count integer;
            BEGIN
                INSERT INTO platform.job_queue
                    (id, job_key, type, payload, state, attempts, max_attempts, available_at, trace_id, correlation_id, created_at)
                VALUES
                    (p_id, p_job_key, p_type, p_payload, 'pending', 0, p_max_attempts, p_available_at, p_trace_id, p_correlation_id, p_created_at)
                ON CONFLICT (job_key) DO NOTHING;
                GET DIAGNOSTICS v_count = ROW_COUNT;
                RETURN v_count = 1;
            END
            $$;

            CREATE FUNCTION platform.purge_expired_idempotency_keys(p_before timestamptz, p_batch integer)
            RETURNS integer
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, platform
            AS $$
            DECLARE
                v_count integer;
            BEGIN
                WITH doomed AS (
                    SELECT user_id, method, route, key
                    FROM platform.idempotency_keys
                    WHERE expires_at < p_before
                    ORDER BY expires_at
                    LIMIT p_batch
                    FOR UPDATE SKIP LOCKED)
                DELETE FROM platform.idempotency_keys k
                USING doomed d
                WHERE k.user_id = d.user_id AND k.method = d.method AND k.route = d.route AND k.key = d.key;
                GET DIAGNOSTICS v_count = ROW_COUNT;
                RETURN v_count;
            END
            $$;
            """);

        // Functions are executable by PUBLIC by default; grant them explicitly instead.
        migrationBuilder.Sql("""
            REVOKE ALL ON FUNCTION platform.audit_entry_hash(bytea, timestamptz, text, text, text, jsonb, text, text) FROM PUBLIC;
            REVOKE ALL ON FUNCTION platform.append_audit(timestamptz, text, text, text, jsonb, text, text) FROM PUBLIC;
            REVOKE ALL ON FUNCTION platform.verify_audit_chain() FROM PUBLIC;
            REVOKE ALL ON FUNCTION platform.enqueue_job(uuid, text, text, jsonb, timestamptz, integer, text, text, timestamptz) FROM PUBLIC;
            REVOKE ALL ON FUNCTION platform.purge_expired_idempotency_keys(timestamptz, integer) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION platform.append_audit(timestamptz, text, text, text, jsonb, text, text) TO api_role, worker_role;
            GRANT EXECUTE ON FUNCTION platform.verify_audit_chain() TO worker_role;
            GRANT EXECUTE ON FUNCTION platform.enqueue_job(uuid, text, text, jsonb, timestamptz, integer, text, text, timestamptz) TO api_role, worker_role;
            GRANT EXECUTE ON FUNCTION platform.purge_expired_idempotency_keys(timestamptz, integer) TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
