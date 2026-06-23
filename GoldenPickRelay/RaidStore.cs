using Microsoft.Data.Sqlite;

namespace GoldenPickRelay;

// SQLite store: raid counters, awarded crates/picks, password redemptions, kill counts.
// one-writer lock model — raid-end traffic is sparse enough that contention doesn't matter.
// path from GOLDENPAN_DB_PATH env, defaults to a local file (ephemeral — set the env to a
// mounted Fly volume for production). see README for the volume setup.
public sealed class RaidStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public RaidStore(string path, ILogger logger)
    {
        // shared cache + journal_mode=WAL gives reasonable concurrent read perf even with our
        // single-writer lock. cache=shared keeps a single connection across the app lifetime.
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        using (var pragma = _db.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        InitSchema();
        logger.LogInformation("[raid-store] sqlite ready at {path}", path);
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS profile_raids (
                profile_id     TEXT PRIMARY KEY,
                nickname       TEXT,
                survived_count INTEGER NOT NULL DEFAULT 0,
                last_updated   INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS awarded_crates (
                crate_id    TEXT PRIMARY KEY,
                profile_id  TEXT NOT NULL,
                nickname    TEXT,
                awarded_at  INTEGER NOT NULL,
                signature   TEXT NOT NULL,
                pick_number INTEGER  -- the global auto-incremented pick # this crate yields
            );
            CREATE INDEX IF NOT EXISTS idx_awarded_profile ON awarded_crates(profile_id);
            -- migration for DBs created before pick_number existed: see ALTER TABLE below

            -- direct admin-granted picks (NOT crate-derived). authored via the admin pick
            -- editor with all metadata set explicitly per pick. nullable fields all permitted —
            -- admin can leave them blank and the client renders the default look for that field.
            CREATE TABLE IF NOT EXISTS awarded_picks (
                pick_id            TEXT PRIMARY KEY,
                owner_nickname     TEXT NOT NULL,
                awarded_at         INTEGER NOT NULL,
                signature          TEXT NOT NULL,
                sheen_color_hex    TEXT,
                custom_name        TEXT,
                custom_description TEXT,
                pick_number        INTEGER,
                password_hash      TEXT,
                kill_count         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_awarded_pick_owner ON awarded_picks(owner_nickname);
        ";
        cmd.ExecuteNonQuery();

        // best-effort ALTER for already-deployed DBs created before these columns existed.
        // SQLite has no "ADD COLUMN IF NOT EXISTS" — catch swallows the duplicate-column
        // error on subsequent runs. each ALTER in its own try so one failing doesn't skip
        // the next.
        try { using var a = _db.CreateCommand(); a.CommandText = "ALTER TABLE awarded_crates ADD COLUMN pick_number      INTEGER;"; a.ExecuteNonQuery(); } catch { }
        try { using var a = _db.CreateCommand(); a.CommandText = "ALTER TABLE awarded_picks  ADD COLUMN kill_count       INTEGER NOT NULL DEFAULT 0;"; a.ExecuteNonQuery(); } catch { }
        // profile_id is the stable identity. NULL on legacy rows (filled in on next
        // interaction). kill-check and redemption use this when present; fall back to
        // nickname comparison only when NULL. once filled, renames update owner_nickname
        // automatically on every kill submission so display stays fresh.
        try { using var a = _db.CreateCommand(); a.CommandText = "ALTER TABLE awarded_picks  ADD COLUMN owner_profile_id TEXT;"; a.ExecuteNonQuery(); } catch { }
    }

    // bump the survived-raid counter for this profile and return the new total. nickname is
    // upserted so the relay can rehydrate display names even if the profile only ever raids.
    public int IncrementSurvived(string profileId, string nickname)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO profile_raids (profile_id, nickname, survived_count, last_updated)
                VALUES ($pid, $nick, 1, $now)
                ON CONFLICT(profile_id) DO UPDATE SET
                    nickname       = excluded.nickname,
                    survived_count = survived_count + 1,
                    last_updated   = excluded.last_updated
                RETURNING survived_count;
            ";
            cmd.Parameters.AddWithValue("$pid", profileId);
            cmd.Parameters.AddWithValue("$nick", nickname);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var newCount = Convert.ToInt32(cmd.ExecuteScalar());
            tx.Commit();
            return newCount;
        }
    }

    // record an awarded crate so we can verify it later if asked (and so the audit log of
    // who's gotten what is permanent). called AFTER the roll wins.
    //
    // pickNumber is the global auto-incremented "Pick #N" the crate will yield on unpack —
    // computed by the caller via NextCratePickNumber so the same atomic transaction can
    // both pick the number AND insert the row (no race between two concurrent awards).
    public void RecordAward(string crateId, string profileId, string nickname, long awardedAt, string signature, int pickNumber)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO awarded_crates (crate_id, profile_id, nickname, awarded_at, signature, pick_number)
                VALUES ($cid, $pid, $nick, $at, $sig, $num);
            ";
            cmd.Parameters.AddWithValue("$cid", crateId);
            cmd.Parameters.AddWithValue("$pid", profileId);
            cmd.Parameters.AddWithValue("$nick", nickname);
            cmd.Parameters.AddWithValue("$at", awardedAt);
            cmd.Parameters.AddWithValue("$sig", signature);
            cmd.Parameters.AddWithValue("$num", pickNumber);
            cmd.ExecuteNonQuery();
        }
    }

    // global auto-increment for crate-derived picks: returns one more than the current MAX
    // pick_number across all awarded crates. holds the lock so concurrent awards can't grab
    // the same number. starts at 1.
    public int NextCratePickNumber()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(pick_number), 0) + 1 FROM awarded_crates;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // admin-grant insert. owner_profile_id may be NULL at this stage — the admin only
    // entered a nickname; the actual profileId gets filled in by SPT-side after the pick
    // is delivered to the player's mailbox (server knows its own sessionId).
    public void RecordPickAward(
        string pickId, string? ownerProfileId, string ownerNickname, long awardedAt, string signature,
        string? sheenColorHex, string? customName, string? customDescription,
        int? pickNumber, string? passwordHash)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO awarded_picks
                    (pick_id, owner_profile_id, owner_nickname, awarded_at, signature,
                     sheen_color_hex, custom_name, custom_description, pick_number, password_hash)
                VALUES ($id, $profId, $owner, $at, $sig, $color, $name, $desc, $num, $pw);
            ";
            cmd.Parameters.AddWithValue("$id", pickId);
            cmd.Parameters.AddWithValue("$profId", (object?)ownerProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owner", ownerNickname);
            cmd.Parameters.AddWithValue("$at", awardedAt);
            cmd.Parameters.AddWithValue("$sig", signature);
            cmd.Parameters.AddWithValue("$color", (object?)sheenColorHex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name",  (object?)customName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$desc",  (object?)customDescription ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$num",   (object?)pickNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pw",    (object?)passwordHash ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // fills/refreshes the identity columns on an existing pick. SPT-side calls this after
    // mailing an admin-granted pick (so owner_profile_id finally has a value) AND any time
    // we observe a fresh nickname for an existing profileId (rename propagation). silently
    // skips if the pick doesn't exist (could happen if SPT-side mail race beats grant write).
    public void UpdateOwnerProfile(string pickId, string profileId, string nickname)
    {
        if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(profileId)) return;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                UPDATE awarded_picks
                   SET owner_profile_id = $profId,
                       owner_nickname   = $nick
                 WHERE pick_id = $id;
            ";
            cmd.Parameters.AddWithValue("$profId", profileId);
            cmd.Parameters.AddWithValue("$nick", nickname);
            cmd.Parameters.AddWithValue("$id", pickId);
            cmd.ExecuteNonQuery();
        }
    }

    // idempotent variant used to backfill crate-derived picks into the leaderboard. unlike
    // RecordPickAward (which assumes a fresh admin grant + uses INSERT), this one uses
    // INSERT OR IGNORE so re-running the SPT-side startup backfill is a no-op for picks
    // already in the table. custom_name/desc/color/password are always NULL because
    // crate-derived picks have no authored cosmetics. returns true if a row was actually
    // inserted (i.e. the pick wasn't already registered).
    public bool RegisterCrateDerivedPickIfAbsent(
        string pickId, string? ownerProfileId, string ownerNickname, long awardedAt, string signature, int? pickNumber)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO awarded_picks
                    (pick_id, owner_profile_id, owner_nickname, awarded_at, signature,
                     sheen_color_hex, custom_name, custom_description, pick_number, password_hash)
                VALUES ($id, $profId, $owner, $at, $sig, NULL, NULL, NULL, $num, NULL);
            ";
            cmd.Parameters.AddWithValue("$id", pickId);
            cmd.Parameters.AddWithValue("$profId", (object?)ownerProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owner", ownerNickname);
            cmd.Parameters.AddWithValue("$at", awardedAt);
            cmd.Parameters.AddWithValue("$sig", signature);
            cmd.Parameters.AddWithValue("$num", (object?)pickNumber ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public sealed record PickRow(
        string PickId, string? OwnerProfileId, string OwnerNickname, long AwardedAt, string Signature,
        string? SheenColorHex, string? CustomName, string? CustomDescription,
        int? PickNumber, string? PasswordHash);

    // password-based pick lookup for redemption. returns the full row so /pick/redeem can
    // mint a fresh pick with the SAME metadata under a new id + new owner nickname.
    public PickRow? FindPickByPasswordHash(string passwordHash)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT pick_id, owner_profile_id, owner_nickname, awarded_at, signature,
                       sheen_color_hex, custom_name, custom_description, pick_number, password_hash
                FROM awarded_picks
                WHERE password_hash = $hash
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("$hash", passwordHash);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new PickRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2),
                r.GetInt64(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : (int?)r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetString(9));
        }
    }

    // admin update: change ONLY the cosmetic metadata fields on a pick identified by its
    // password. pick_id, owner_nickname, awarded_at, signature stay the same — the player's
    // existing in-stash pick keeps working, just with new color/name/desc/number. returns
    // the affected pick_id (so the broadcast can target the right id) or null on no match.
    public string? UpdatePickMetadataByPassword(
        string passwordHash, string? sheenColorHex, string? customName, string? customDescription, int? pickNumber)
    {
        lock (_lock)
        {
            string? pickId;
            using (var sel = _db.CreateCommand())
            {
                sel.CommandText = "SELECT pick_id FROM awarded_picks WHERE password_hash = $hash LIMIT 1;";
                sel.Parameters.AddWithValue("$hash", passwordHash);
                pickId = sel.ExecuteScalar() as string;
            }
            if (string.IsNullOrEmpty(pickId)) return null;

            using (var upd = _db.CreateCommand())
            {
                upd.CommandText = @"
                    UPDATE awarded_picks
                       SET sheen_color_hex    = $sheen,
                           custom_name        = $name,
                           custom_description = $desc,
                           pick_number        = $num
                     WHERE password_hash = $hash;
                ";
                upd.Parameters.AddWithValue("$sheen", (object?)sheenColorHex     ?? DBNull.Value);
                upd.Parameters.AddWithValue("$name",  (object?)customName        ?? DBNull.Value);
                upd.Parameters.AddWithValue("$desc",  (object?)customDescription ?? DBNull.Value);
                upd.Parameters.AddWithValue("$num",   (object?)pickNumber        ?? DBNull.Value);
                upd.Parameters.AddWithValue("$hash",  passwordHash);
                upd.ExecuteNonQuery();
            }
            return pickId;
        }
    }

    // overwrite-style redemption update: bump pick_id, owner_profile_id, owner_nickname,
    // awarded_at, signature for the row identified by password_hash. all other fields
    // (sheen color, name, description, number) carry over from the original authoring.
    // ONE active pick per password — old pick id is now orphaned, redemption history not
    // preserved. profileId is taken from the redeeming player's session, NOT from any
    // value they pass — that's what makes redemption survive profile rename or reset
    // (different profileId of same human → still claimable via password).
    public void UpdatePickAfterRedemption(
        string passwordHash, string newPickId, string newOwnerProfileId, string newOwnerNickname, long newAwardedAt, string newSignature)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                UPDATE awarded_picks
                   SET pick_id          = $newId,
                       owner_profile_id = $newProfId,
                       owner_nickname   = $newOwner,
                       awarded_at       = $newAt,
                       signature        = $newSig
                 WHERE password_hash    = $hash;
            ";
            cmd.Parameters.AddWithValue("$newId", newPickId);
            cmd.Parameters.AddWithValue("$newProfId", newOwnerProfileId);
            cmd.Parameters.AddWithValue("$newOwner", newOwnerNickname);
            cmd.Parameters.AddWithValue("$newAt", newAwardedAt);
            cmd.Parameters.AddWithValue("$newSig", newSignature);
            cmd.Parameters.AddWithValue("$hash", passwordHash);
            cmd.ExecuteNonQuery();
        }
    }

    // increments kill_count for a pick, ONLY if the killer's profileId matches the pick's
    // stable owner (or — for legacy rows where owner_profile_id is NULL — falls back to a
    // nickname comparison). on match, also refreshes owner_nickname to the killer's CURRENT
    // nickname so renames propagate to the leaderboard for free. legacy rows with NULL
    // profileId get filled in with the killer's profileId on the first matching kill,
    // upgrading them in place.
    public int? RecordKillIfOwner(string pickId, string killerProfileId, string killerNickname)
    {
        if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(killerProfileId) || string.IsNullOrEmpty(killerNickname))
            return null;
        lock (_lock)
        {
            string? rowProfileId, rowNickname;
            using (var sel = _db.CreateCommand())
            {
                sel.CommandText = "SELECT owner_profile_id, owner_nickname FROM awarded_picks WHERE pick_id = $id;";
                sel.Parameters.AddWithValue("$id", pickId);
                using var r = sel.ExecuteReader();
                if (!r.Read()) return null;
                rowProfileId = r.IsDBNull(0) ? null : r.GetString(0);
                rowNickname  = r.IsDBNull(1) ? null : r.GetString(1);
            }

            // primary check: profileId match. fallback for legacy NULL-profileId rows:
            // nickname match (case-insensitive). either way, on success we WRITE the
            // killer's profileId+nickname back to refresh display + upgrade legacy rows.
            bool matched;
            if (!string.IsNullOrEmpty(rowProfileId))
                matched = string.Equals(rowProfileId, killerProfileId, StringComparison.Ordinal);
            else
                matched = string.Equals(rowNickname, killerNickname, StringComparison.OrdinalIgnoreCase);
            if (!matched) return null;

            using (var upd = _db.CreateCommand())
            {
                upd.CommandText = @"
                    UPDATE awarded_picks
                       SET kill_count       = kill_count + 1,
                           owner_profile_id = $profId,
                           owner_nickname   = $nick
                     WHERE pick_id = $id;
                ";
                upd.Parameters.AddWithValue("$id", pickId);
                upd.Parameters.AddWithValue("$profId", killerProfileId);
                upd.Parameters.AddWithValue("$nick", killerNickname);
                upd.ExecuteNonQuery();
            }

            using (var read = _db.CreateCommand())
            {
                read.CommandText = "SELECT kill_count FROM awarded_picks WHERE pick_id = $id;";
                read.Parameters.AddWithValue("$id", pickId);
                var v = read.ExecuteScalar();
                return v == null ? (int?)null : Convert.ToInt32(v);
            }
        }
    }

    public sealed record LeaderboardEntry(
        string PickId, string? OwnerProfileId, string OwnerNickname, long AwardedAt,
        string? SheenColorHex, string? CustomName, string? CustomDescription,
        int? PickNumber, int KillCount);

    // every pick in circulation, ordered by kill_count desc, pick_number asc. returns the FULL
    // metadata payload — the leaderboard HTML renders color swatch / number / name / kills.
    public List<LeaderboardEntry> GetLeaderboard()
    {
        var rows = new List<LeaderboardEntry>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT pick_id, owner_profile_id, owner_nickname, awarded_at,
                       sheen_color_hex, custom_name, custom_description, pick_number, kill_count
                FROM awarded_picks
                ORDER BY kill_count DESC, pick_number ASC NULLS LAST, awarded_at ASC;
            ";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new LeaderboardEntry(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetString(2),
                    r.GetInt64(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : (int?)r.GetInt32(7),
                    r.IsDBNull(8) ? 0 : r.GetInt32(8)));
            }
        }
        return rows;
    }

    // selective wipe: removes ONLY crate-derived picks (no authored cosmetics) + all crates
    // + all raid counters. PRESERVES custom picks — defined as any row where at least one of
    // sheen_color_hex, custom_name, custom_description, password_hash is non-null. that's the
    // exact distinction between admin-grant (has authored fields) and crate-derived (all NULL
    // except pick_number).
    //
    // returns (picksDeleted, picksKept, cratesDeleted, countersDeleted) so the admin endpoint
    // can log + acknowledge what was preserved.
    public (int picksDeleted, int picksKept, int cratesDeleted, int countersDeleted) WipeCrateDerived()
    {
        // any of these being non-null marks the row as a custom/admin-granted pick — keep it.
        const string KEEP_PREDICATE = @"
              sheen_color_hex    IS NOT NULL
           OR custom_name        IS NOT NULL
           OR custom_description IS NOT NULL
           OR password_hash      IS NOT NULL
        ";

        lock (_lock)
        {
            int picksKept, picksDeleted, cratesDeleted, countersDeleted;

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM awarded_picks WHERE NOT ({KEEP_PREDICATE});";
                picksDeleted = Convert.ToInt32(cmd.ExecuteScalar());
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM awarded_picks WHERE {KEEP_PREDICATE};";
                picksKept = Convert.ToInt32(cmd.ExecuteScalar());
            }
            using (var cmd = _db.CreateCommand()) { cmd.CommandText = "SELECT COUNT(*) FROM awarded_crates;";    cratesDeleted   = Convert.ToInt32(cmd.ExecuteScalar()); }
            using (var cmd = _db.CreateCommand()) { cmd.CommandText = "SELECT COUNT(*) FROM profile_raids;"; countersDeleted = Convert.ToInt32(cmd.ExecuteScalar()); }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM awarded_picks WHERE NOT ({KEEP_PREDICATE});";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _db.CreateCommand()) { cmd.CommandText = "DELETE FROM awarded_crates;";    cmd.ExecuteNonQuery(); }
            using (var cmd = _db.CreateCommand()) { cmd.CommandText = "DELETE FROM profile_raids;"; cmd.ExecuteNonQuery(); }

            return (picksDeleted, picksKept, cratesDeleted, countersDeleted);
        }
    }

    // current survived-raid count for a profile. used by the client-side debug overlay to
    // rehydrate state on game start (otherwise it sits at 0 until the next raid_result
    // broadcast arrives). returns 0 if the profile has never raided.
    public int GetSurvivedCount(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return 0;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT survived_count FROM profile_raids WHERE profile_id = $pid;";
            cmd.Parameters.AddWithValue("$pid", profileId);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
        }
    }

    public (int profilesTracked, int totalAwards) Stats()
    {
        lock (_lock)
        {
            using var c1 = _db.CreateCommand();
            c1.CommandText = "SELECT COUNT(*) FROM profile_raids;";
            var profiles = Convert.ToInt32(c1.ExecuteScalar());
            using var c2 = _db.CreateCommand();
            c2.CommandText = "SELECT COUNT(*) FROM awarded_crates;";
            var awards = Convert.ToInt32(c2.ExecuteScalar());
            return (profiles, awards);
        }
    }

    public void Dispose() => _db.Dispose();
}
