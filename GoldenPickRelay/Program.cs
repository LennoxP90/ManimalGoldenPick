using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Serialization;
using GoldenPickRelay;

// GoldenPick relay: WebSocket broadcast hub + crate-award oracle. all admin routes
// are soft-gated by GOLDENPAN_KEY (shipped to every player → not real security; the
// actual protection is Ed25519 signing with a private key kept as a Fly secret).
// see README for the route inventory.

var builder = WebApplication.CreateBuilder(args);

// CORS: the test-button.html page is loaded from file:// or any other host — without
// permissive CORS the browser blocks all cross-origin POST/fetch calls (with a NetworkError
// at the preflight stage, before our endpoint even gets hit). everything served here is
// public + soft-key-gated, so AllowAnyOrigin is the right tradeoff for a debug surface.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

var startupLog = app.Services.GetRequiredService<ILogger<Program>>();
var signer = CrateSigner.LoadOrGenerate(startupLog);
var dbPath = Environment.GetEnvironmentVariable("GOLDENPAN_DB_PATH") ?? "goldenpick-relay.db";
var store = new RaidStore(dbPath, startupLog);

app.UseCors();
app.UseWebSockets();

var clients = new ConcurrentDictionary<Guid, ClientConn>();
var ipCounts = new ConcurrentDictionary<string, int>();

// soft-gate key. shipped in BepInEx source — NOT real security, just stops casual scanners.
// gates routes the SPT server posts to (raid-end, crate-derived register, owner-profile fill).
var apiKey = Environment.GetEnvironmentVariable("GOLDENPAN_KEY");

// admin key — Fly-secret only, NEVER shipped to any client. gates the destructive routes
// (grant-pick, update-pick, admin/reset, test/grant-crate). if unset, those routes 401 every
// request — fail closed to avoid an unconfigured prod deploy leaving them wide open.
var adminKey = Environment.GetEnvironmentVariable("GOLDENPAN_ADMIN_KEY");
bool IsAdmin(HttpContext ctx) =>
    !string.IsNullOrEmpty(adminKey) && ctx.Request.Query["adminKey"].ToString() == adminKey;

// --- limits (the actual abuse mitigation) ---
const double MinSendIntervalSeconds = 2.0; // per-connection: 1 event / 2s
const int MaxMessageBytes = 1024;          // earn events are tiny
const int MaxConnectionsPerIp = 5;         // stops one actor opening many sockets
const int MaxTotalConnections = 2000;      // global connection ceiling
const int MaxGlobalMessagesPerSecond = 50; // global flood guard across all connections

// --- crate drop logic (the relay is the SOLE arbiter) ---
const int RaidCycleSize = 5;            // roll once every Nth survived raid
const double DropProbability = 0.0051;  // 0.51% — roll once every 5th survived raid
// per-profile minimum spacing on raid-end posts. SPT raids run minutes, this just stops
// a single forked client from spamming raid-end calls to mass-roll for a drop.
const double MinRaidEndIntervalSeconds = 30.0;
var lastRaidEndAt = new ConcurrentDictionary<string, DateTime>();

// global fixed-window message-rate counter
var rateLock = new object();
long windowStartTicks = 0;
int windowCount = 0;

bool AllowGlobalMessage()
{
    lock (rateLock)
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - windowStartTicks >= TimeSpan.TicksPerSecond)
        {
            windowStartTicks = now;
            windowCount = 0;
        }
        if (windowCount >= MaxGlobalMessagesPerSecond) return false;
        windowCount++;
        return true;
    }
}

// real client IP behind Fly's proxy (RemoteIpAddress would just be the proxy)
static string ResolveIp(HttpContext ctx)
{
    var fly = ctx.Request.Headers["Fly-Client-IP"].ToString();
    if (!string.IsNullOrEmpty(fly)) return fly;
    var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrEmpty(xff)) return xff.Split(',')[0].Trim();
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

app.MapGet("/", () => $"GoldenPick relay up. {clients.Count} connected.");

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!string.IsNullOrEmpty(apiKey) && context.Request.Query["key"].ToString() != apiKey)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var ip = ResolveIp(context);

    // global connection ceiling
    if (clients.Count >= MaxTotalConnections)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    // per-IP connection cap
    var ipCount = ipCounts.AddOrUpdate(ip, 1, (_, c) => c + 1);
    if (ipCount > MaxConnectionsPerIp)
    {
        ipCounts.AddOrUpdate(ip, 0, (_, c) => Math.Max(0, c - 1));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var conn = new ClientConn(socket);
    clients[conn.Id] = conn;
    Console.WriteLine($"[+] {conn.Id} ip={ip} connected ({clients.Count} total)");

    var buffer = new byte[4096];
    var open = true;
    try
    {
        while (open && socket.State == WebSocketState.Open)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    open = false;
                    break;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            if (!open) break;

            // per-connection rate limit
            var now = DateTime.UtcNow;
            if ((now - conn.LastAccepted).TotalSeconds < MinSendIntervalSeconds) continue;
            conn.LastAccepted = now;

            var msg = sb.ToString();
            if (Encoding.UTF8.GetByteCount(msg) > MaxMessageBytes) continue;

            // global flood guard
            if (!AllowGlobalMessage()) continue;

            // pick_earned events are spoofable — anyone with the WS could fire one and make
            // every client show a toast for any nickname. drop client-sent pick_earned at
            // the relay; real pick_earned events are generated SERVER-SIDE inside the
            // grant/redeem/register-crate-derived endpoints (where there's actual signed
            // state behind the claim). this catches old clients too — no client update needed.
            if (msg.Contains("\"pick_earned\"") || msg.Contains("\"Type\":\"pick_earned\""))
            {
                app.Logger.LogWarning("[ws] dropped client-sent pick_earned from {ip} (spoofing attempt or legacy client broadcast)", ip);
                continue;
            }

            await Broadcast(msg, conn.Id);
        }
    }
    catch
    {
        // client dropped mid-read — fall through to cleanup
    }

    clients.TryRemove(conn.Id, out _);
    ipCounts.AddOrUpdate(ip, 0, (_, c) => Math.Max(0, c - 1));
    Console.WriteLine($"[-] {conn.Id} ip={ip} disconnected ({clients.Count} total)");
});

// builds the legacy pick_earned message shape that pre-v1.0 clients listen for. emitted
// SERVER-SIDE only — the WS receive loop drops client-sent pick_earned to stop spoofing.
// kept identical to the old payload so old clients render the same toast as before.
string EmitLegacyPickEarned(string nickname) => System.Text.Json.JsonSerializer.Serialize(new
{
    Type = "pick_earned",
    Player = nickname,
    Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
});

// rebroadcast to everyone except the sender. per-socket SendLock because two senders
// can broadcast concurrently and a websocket allows only one outstanding send.
async Task Broadcast(string msg, Guid from)
{
    var bytes = Encoding.UTF8.GetBytes(msg);
    foreach (var kv in clients)
    {
        if (kv.Key == from) continue;
        var c = kv.Value;
        if (c.Socket.State != WebSocketState.Open) continue;

        await c.SendLock.WaitAsync();
        try
        {
            await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            // dead socket — its own receive loop will clean it up
        }
        finally
        {
            c.SendLock.Release();
        }
    }
}

// --- HTTP endpoints for the SPT server mod ---

// returns the Ed25519 public key in base64. NOT a secret — public keys never are.
// hardcode this into the BepInEx client mod source so unpack can verify offline.
app.MapGet("/pubkey", () => Results.Text(Convert.ToBase64String(signer.PublicKey.GetEncoded())));

// the only legitimate path to mint a crate. SPT server mod POSTs here at every raid end;
// the relay decides whether a crate gets awarded and (on award) returns its signature.
app.MapPost("/raid/end", async (RaidEndRequest req, HttpContext ctx) =>
{
    if (!string.IsNullOrEmpty(apiKey) && ctx.Request.Query["key"].ToString() != apiKey)
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.ProfileId) || string.IsNullOrWhiteSpace(req.Nickname))
        return Results.BadRequest(new { error = "profileId and nickname required" });

    // per-profile cooldown — stops a forked client from spamming raid-end calls to mass-roll
    var now = DateTime.UtcNow;
    if (lastRaidEndAt.TryGetValue(req.ProfileId, out var last)
        && (now - last).TotalSeconds < MinRaidEndIntervalSeconds)
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    lastRaidEndAt[req.ProfileId] = now;

    int newCount = 0;
    bool awarded = false;
    CrateAward? award = null;

    // gate: only fully-survived raids count. RUNNER (runthrough), KILLED, LEFT,
    // MISSINGINACTION, TRANSIT all return without bumping the counter.
    if (req.Survived && !req.Runthrough)
    {
        newCount = store.IncrementSurvived(req.ProfileId, req.Nickname);

        // cycle boundary check + roll
        if (newCount % RaidCycleSize == 0)
        {
            var roll = Random.Shared.NextDouble();
            app.Logger.LogInformation("[raid/end] profile={pid} nickname={nick} count={n} (cycle hit) — rolled {r:F5}, need < {p:F5}",
                req.ProfileId, req.Nickname, newCount, roll, DropProbability);

            if (roll < DropProbability)
            {
                var crateId = MintMongoId();
                var awardedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sig = signer.SignCrateAward(crateId, req.ProfileId, awardedAt);
                // auto-incremented "Pick #N" — assigned at crate-award time so it's a single
                // global counter across all relay-tracked crates. propagates through the
                // crate → pick unpack flow and lands in the pick's metadata at unpack time.
                var pickNumber = store.NextCratePickNumber();
                store.RecordAward(crateId, req.ProfileId, req.Nickname, awardedAt, sig, pickNumber);
                award = new CrateAward(crateId, awardedAt, sig, pickNumber);
                awarded = true;
                app.Logger.LogInformation("[raid/end] AWARD profile={pid} crate={cid} pick#={n}", req.ProfileId, crateId, pickNumber);
            }
        }
    }

    // push the result to every connected BepInEx client so the debug overlay updates without
    // polling. recipient self-selects by nickname. fire BEFORE returning so the broadcast
    // doesnt depend on the HTTP response completing.
    var raidResultMsg = System.Text.Json.JsonSerializer.Serialize(new
    {
        Type = "raid_result",
        Player = req.Nickname,
        NewCount = newCount,
        Awarded = awarded,
        LastResult = req.LastResult ?? "unknown",
    });
    await Broadcast(raidResultMsg, Guid.Empty);

    return Results.Ok(new RaidEndResponse(awarded, award, newCount));
});

// MongoId helper — same shape EFT uses, 24-char lowercase hex
static string MintMongoId()
{
    var bytes = new byte[12];
    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

// admin: grant a CUSTOM PICK directly to a player (no crate). request carries the full
// metadata — sheen color, custom name/description, pick number, optional password. relay
// signs the pick id + persists the metadata + broadcasts to clients (recipient self-selects
// by nickname).
//
// password_hash is computed here from the plain `password` field; the relay stores ONLY
// the hash. plain password is what the user types into the console for redemption (Stage C).
//
// gated by GOLDENPAN_KEY same as other admin/test routes — soft-gate, but the only path
// admin route — gated by GOLDENPAN_ADMIN_KEY (Fly secret, NEVER shipped to clients).
// mints a custom-signed pick. requires ?adminKey=... query param.
app.MapPost("/admin/grant-pick", async (AdminGrantPickRequest req, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Nickname))
        return Results.BadRequest(new { error = "nickname required" });

    var pickId = MintMongoId();
    var awardedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    // sign over the same canonical payload as crate awards — client verifies via embedded
    // public key, so the format MUST stay identical to CrateSigner.BuildCrateAwardPayload.
    // we reuse the same scheme for picks (logical id discriminator is the prefix "crate|"
    // — could swap to "pick|" later, but consistency-with-client-verifier matters more).
    var sig = signer.SignCrateAward(pickId, req.Nickname, awardedAt);

    // optional password — hash with SHA256 for storage. plain password never persisted.
    string? pwHash = null;
    if (!string.IsNullOrEmpty(req.Password))
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(req.Password));
        pwHash = Convert.ToHexString(b).ToLowerInvariant();
    }

    // admin grants don't know the recipient's profileId yet — only the nickname they typed
    // in the form. record owner_profile_id = NULL; SPT-side fills it in via
    // /pick/update-owner-profile after the pick lands in the recipient's mailbox (server
    // knows its own sessionId at that point). kill verification falls back to nickname
    // comparison for that brief window.
    store.RecordPickAward(pickId, ownerProfileId: null, req.Nickname, awardedAt, sig,
        req.SheenColorHex, req.CustomName, req.CustomDescription, req.PickNumber, pwHash);

    var grantMsg = System.Text.Json.JsonSerializer.Serialize(new
    {
        Type = "pick_grant",
        Player = req.Nickname,
        PickId = pickId,
        Signature = sig,
        AwardedAt = awardedAt,
        SheenColorHex = req.SheenColorHex,
        CustomName = req.CustomName,
        CustomDescription = req.CustomDescription,
        PickNumber = req.PickNumber,
    });
    await Broadcast(grantMsg, Guid.Empty);
    // legacy pick_earned for OLD clients — they still listen for it but we no longer
    // accept it from the WS. server-issued here means only real grants trigger toasts.
    await Broadcast(EmitLegacyPickEarned(req.Nickname), Guid.Empty);

    app.Logger.LogInformation("[admin/grant-pick] BROADCAST nickname={n} pickId={p} number={num} color={c}",
        req.Nickname, pickId, req.PickNumber?.ToString() ?? "(none)", req.SheenColorHex ?? "(default)");
    return Results.Ok(new { ok = true, pickId, awardedAt });
});

// admin update: change metadata fields (color, name, desc, number) on an EXISTING pick
// identified by password. pick_id + owner stay the same — the player's in-stash pick keeps
// working, just with new cosmetics. broadcasts pick_metadata_update so the recipient client
// invalidates its cache + the SPT server overwrites its on-disk metadata.
app.MapPost("/admin/update-pick", async (AdminUpdatePickRequest req, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "password required to identify the pick" });

    string passwordHash;
    using (var sha = System.Security.Cryptography.SHA256.Create())
    {
        var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(req.Password));
        passwordHash = Convert.ToHexString(b).ToLowerInvariant();
    }

    var pickId = store.UpdatePickMetadataByPassword(
        passwordHash, req.SheenColorHex, req.CustomName, req.CustomDescription, req.PickNumber);
    if (pickId == null)
        return Results.NotFound(new { ok = false, reason = "no pick found for that password" });

    // look up the owner so we can target the broadcast to the right client (matches the
    // nickname-self-select convention used by pick_grant / crate_grant).
    var row = store.FindPickByPasswordHash(passwordHash);
    var owner = row?.OwnerNickname ?? "";

    var msg = System.Text.Json.JsonSerializer.Serialize(new
    {
        Type = "pick_metadata_update",
        Player = owner,
        PickId = pickId,
        SheenColorHex = req.SheenColorHex,
        CustomName = req.CustomName,
        CustomDescription = req.CustomDescription,
        PickNumber = req.PickNumber,
    });
    await Broadcast(msg, Guid.Empty);

    app.Logger.LogInformation("[admin/update-pick] pickId={p} owner={o} number={num} color={c}",
        pickId, owner, req.PickNumber?.ToString() ?? "(none)", req.SheenColorHex ?? "(unchanged)");
    return Results.Ok(new { ok = true, pickId, owner });
});

// password-based pick redemption. SPT chat command on the client side POSTs here with the
// player's CURRENT nickname (server-trusted, not client-claimed) + the typed password. relay
// hashes the password, looks up the metadata, mints a fresh pick id, signs it with the
// current nickname, updates the row (so future redemptions reflect the latest identity),
// and broadcasts a pick_grant carrying the original metadata.
//
// authority is the password alone — nickname can change across profile resets and the pick
// still flows to whoever has the password. NO api-key gate on this endpoint: it's player-
// facing and the password itself is the access credential.
app.MapPost("/pick/redeem", async (RedeemRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.CurrentNickname)
        || string.IsNullOrWhiteSpace(req.CurrentProfileId))
        return Results.BadRequest(new { error = "password, currentNickname and currentProfileId required" });

    string passwordHash;
    using (var sha = System.Security.Cryptography.SHA256.Create())
    {
        var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(req.Password));
        passwordHash = Convert.ToHexString(b).ToLowerInvariant();
    }

    var existing = store.FindPickByPasswordHash(passwordHash);
    if (existing == null)
    {
        app.Logger.LogInformation("[pick/redeem] no match for password (nickname={n})", req.CurrentNickname);
        return Results.NotFound(new { ok = false, reason = "no_match" });
    }

    // mint a fresh pick id + re-sign with the CURRENT nickname (so the canonical payload
    // matches what the client will reconstruct under the new identity). owner_profile_id
    // updates to whoever's redeeming — that's the password-survives-rename guarantee.
    var newPickId = MintMongoId();
    var newAwardedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var newSig = signer.SignCrateAward(newPickId, req.CurrentNickname, newAwardedAt);
    store.UpdatePickAfterRedemption(passwordHash, newPickId, req.CurrentProfileId, req.CurrentNickname, newAwardedAt, newSig);

    // broadcast pick_grant with original metadata (sheen color, name, description, number)
    // and the new id/signature. recipient self-selects by nickname → BepInEx → local SPT
    // mints + mails the pick.
    var grantMsg = System.Text.Json.JsonSerializer.Serialize(new
    {
        Type = "pick_grant",
        Player = req.CurrentNickname,
        PickId = newPickId,
        Signature = newSig,
        AwardedAt = newAwardedAt,
        SheenColorHex = existing.SheenColorHex,
        CustomName = existing.CustomName,
        CustomDescription = existing.CustomDescription,
        PickNumber = existing.PickNumber,
    });
    await Broadcast(grantMsg, Guid.Empty);
    // legacy pick_earned toast for OLD clients
    await Broadcast(EmitLegacyPickEarned(req.CurrentNickname), Guid.Empty);

    app.Logger.LogInformation("[pick/redeem] REDEEMED nickname={n} oldOwner={o} newPickId={p}",
        req.CurrentNickname, existing.OwnerNickname, newPickId);
    return Results.Ok(new { ok = true, pickId = newPickId, customName = existing.CustomName, pickNumber = existing.PickNumber });
});

// register a crate-derived pick into awarded_picks so it shows up on the leaderboard. SPT
// server calls this at inherit time (new unpacks) and at server startup (backfill for
// already-unpacked crate-derived picks). idempotent — repeated calls for the same pickId
// are a no-op.
//
// admin-key gated: this writes to authoritative state. SPT server has the key in its
// relay-client config; player-only clients (BepInEx) never call this directly.
app.MapPost("/pick/register-crate-derived", async (RegisterCrateDerivedRequest req, HttpContext ctx) =>
{
    if (!string.IsNullOrEmpty(apiKey) && ctx.Request.Query["key"].ToString() != apiKey)
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.PickId) || string.IsNullOrWhiteSpace(req.OwnerNickname)
        || string.IsNullOrWhiteSpace(req.Signature))
        return Results.BadRequest(new { error = "pickId, ownerNickname, signature required" });

    var inserted = store.RegisterCrateDerivedPickIfAbsent(
        req.PickId, req.OwnerProfileId, req.OwnerNickname, req.AwardedAt, req.Signature, req.PickNumber);
    if (inserted)
    {
        app.Logger.LogInformation("[pick/register-crate-derived] NEW pickId={p} profileId={pid} nickname={n} #{num}",
            req.PickId, req.OwnerProfileId ?? "(none)", req.OwnerNickname, req.PickNumber?.ToString() ?? "?");
        // legacy pick_earned toast for OLD clients — only on first insert so idempotent
        // re-registrations from the startup backfill dont retrigger toasts every restart.
        await Broadcast(EmitLegacyPickEarned(req.OwnerNickname), Guid.Empty);
    }
    return Results.Ok(new { ok = true, inserted });
});

// SELECTIVE wipe — removes only crate-derived picks (no authored cosmetics) plus all
// crates and raid counters. PRESERVES custom picks (any row with a sheen color, name,
// description, or password). dev-only, admin-key gated, requires ?confirm=yes so a stray
// curl can't accidentally clear test data.
app.MapPost("/admin/reset", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    if (ctx.Request.Query["confirm"].ToString() != "yes")
        return Results.BadRequest(new { error = "add ?confirm=yes to actually wipe" });

    var r = store.WipeCrateDerived();
    app.Logger.LogWarning("[admin/reset] picks deleted={pd} kept={pk}, crates deleted={cd}, counters deleted={ctd}",
        r.picksDeleted, r.picksKept, r.cratesDeleted, r.countersDeleted);
    return Results.Ok(new
    {
        ok = true,
        deleted = new { picks = r.picksDeleted, crates = r.cratesDeleted, counters = r.countersDeleted },
        kept    = new { customPicks = r.picksKept }
    });
});

// SPT-side fills in (or refreshes) the identity of a pick after delivery. used after
// admin-grant lands in the mailbox (where SPT first learns the recipient's profileId)
// and any time we want to refresh the stored nickname for a profileId (rename catch-up).
app.MapPost("/pick/update-owner-profile", (UpdateOwnerProfileRequest req, HttpContext ctx) =>
{
    if (!string.IsNullOrEmpty(apiKey) && ctx.Request.Query["key"].ToString() != apiKey)
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.PickId) || string.IsNullOrWhiteSpace(req.ProfileId)
        || string.IsNullOrWhiteSpace(req.Nickname))
        return Results.BadRequest(new { error = "pickId, profileId, nickname required" });

    var updated = store.UpdateOwnerProfile(req.PickId, req.ProfileId, req.Nickname);
    if (!updated)
    {
        // either the pick doesn't exist OR the row already has a different owner_profile_id
        // (credit-steal attempt). either way, refuse silently with a clear log line.
        app.Logger.LogWarning("[pick/update-owner-profile] REJECTED pickId={p} attempt-profileId={pid} (pick missing or owner mismatch)",
            req.PickId, req.ProfileId);
        return Results.NotFound(new { ok = false, reason = "pick_missing_or_owner_mismatch" });
    }
    app.Logger.LogInformation("[pick/update-owner-profile] pickId={p} profileId={pid} nickname={n}",
        req.PickId, req.ProfileId, req.Nickname);
    return Results.Ok(new { ok = true });
});

// kill submission — client POSTs after a confirmed golden-pick kill. owner-gated: kill is
// only counted if the killer nickname matches the pick's current owner_nickname. that lets
// players inflate ONLY their own picks (annoying but bounded — not a vector for griefing
// other players' stats). public route, no admin key — picks come from authenticated raid
// activity that already went through the signed-grant pipeline upstream.
app.MapPost("/pick/kill", (PickKillRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.PickId) || string.IsNullOrWhiteSpace(req.KillerProfileId)
        || string.IsNullOrWhiteSpace(req.KillerNickname))
        return Results.BadRequest(new { error = "pickId, killerProfileId, killerNickname required" });

    // RecordKillIfOwner verifies by profileId, falls back to nickname for legacy rows.
    // on match it ALSO writes the killer's current nickname back so renames propagate.
    var newCount = store.RecordKillIfOwner(req.PickId, req.KillerProfileId, req.KillerNickname);
    if (newCount == null)
    {
        app.Logger.LogWarning("[pick/kill] REJECTED pickId={p} killerProfile={kp} killerNick={kn} (not owner or unknown pick)",
            req.PickId, req.KillerProfileId, req.KillerNickname);
        return Results.NotFound(new { ok = false, reason = "not_owner_or_unknown" });
    }

    app.Logger.LogInformation("[pick/kill] pickId={p} killer={k} newCount={c}",
        req.PickId, req.KillerNickname, newCount);
    return Results.Ok(new { ok = true, killCount = newCount });
});

// public leaderboard JSON — every pick in circulation, ordered by kill_count desc. consumed
// by /leaderboard.html (served below) and any third-party that wants to embed the standings.
app.MapGet("/leaderboard", () =>
{
    var rows = store.GetLeaderboard();
    return Results.Json(new { ok = true, picks = rows });
});

// static leaderboard page — served from disk so we can iterate on the HTML without rebuilding
// the binary. lives alongside the binary in the deploy.
app.MapGet("/leaderboard.html", async (HttpContext ctx) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "leaderboard.html");
    if (!File.Exists(path))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("leaderboard.html missing from deploy");
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(path);
});

// per-profile current survived-raid count. used by the BepInEx debug overlay to rehydrate
// on game start (the overlay only updates from raid_result broadcasts otherwise — restart
// = no broadcasts yet = stuck at 0). public read; profileId is the sole input + the access
// key, no admin gating.
app.MapGet("/raid/state", (string profileId) =>
{
    if (string.IsNullOrWhiteSpace(profileId))
        return Results.BadRequest(new { error = "profileId required" });
    var count = store.GetSurvivedCount(profileId);
    return Results.Ok(new { ok = true, survivedCount = count });
});

app.MapGet("/health", () =>
{
    var (profiles, awards) = store.Stats();
    return Results.Json(new { ok = true, connectedSockets = clients.Count, profilesTracked = profiles, totalAwards = awards });
});

// admin-debug helper used by the HTML test page. mints + signs a crate award identical to
// what a winning roll would produce, then BROADCASTS it via /ws as a `crate_grant` event so
// connected BepInEx clients can self-select by nickname and forward to their local SPT
// server. signature still comes from THIS relay's private key, so the award is verifiable.
//
// for testing only — gated by the same GOLDENPAN_KEY soft-gate, but this is "admin tools"
// not "anti-cheat"; anyone with the key + a target nickname can mint a free crate. don't
// share the key publicly.
app.MapPost("/test/grant-crate", async (TestGrantRequest req, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Nickname))
        return Results.BadRequest(new { error = "nickname required" });

    // we don't have a profileId for the test path. sign with nickname as the profile-id
    // placeholder — the signature payload is `crate|crateId|<id>|awardedAt`, and the client
    // must reconstruct the SAME id to verify. so for the test grant we sign with nickname,
    // and the SPT server's grant-crate route happens to use the recipient's sessionId for
    // its OWN signature payload reconstruction. WAIT — actually for verification to work
    // both must agree, so just sign with nickname here AND the client verifies with nickname.
    var crateId = MintMongoId();
    var awardedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var sig = signer.SignCrateAward(crateId, req.Nickname, awardedAt);
    var pickNumber = store.NextCratePickNumber();
    store.RecordAward(crateId, req.Nickname, req.Nickname, awardedAt, sig, pickNumber);

    // broadcast over /ws to every connected client — the recipient self-selects by nickname
    var grantMsg = System.Text.Json.JsonSerializer.Serialize(new
    {
        Type = "crate_grant",
        Player = req.Nickname,
        CrateId = crateId,
        Signature = sig,
        AwardedAt = awardedAt,
        PickNumber = pickNumber,
    });
    await Broadcast(grantMsg, Guid.Empty);  // Guid.Empty = no excluded sender (we ARE the sender)

    app.Logger.LogInformation("[test/grant-crate] BROADCAST nickname={n} crateId={c}", req.Nickname, crateId);
    return Results.Ok(new { ok = true, crateId, awardedAt });
});

app.Run();

// --- DTOs (records for clean System.Text.Json serialization) ---

public sealed record RaidEndRequest(
    [property: JsonPropertyName("profileId")]  string ProfileId,
    [property: JsonPropertyName("nickname")]   string Nickname,
    [property: JsonPropertyName("survived")]   bool   Survived,
    [property: JsonPropertyName("runthrough")] bool   Runthrough,
    // raw ExitStatus string from SPT — purely for the debug overlay to show "last: killed"
    // / "last: survived" etc. relay broadcasts it back unchanged.
    [property: JsonPropertyName("lastResult")] string? LastResult
);

public sealed record CrateAward(
    [property: JsonPropertyName("crateId")]    string CrateId,
    [property: JsonPropertyName("awardedAt")]  long   AwardedAt,
    [property: JsonPropertyName("signature")]  string Signature,
    [property: JsonPropertyName("pickNumber")] int    PickNumber
);

public sealed record RaidEndResponse(
    [property: JsonPropertyName("awarded")]  bool        Awarded,
    [property: JsonPropertyName("crate")]    CrateAward? Crate,
    [property: JsonPropertyName("newCount")] int         NewCount
);

public sealed record TestGrantRequest(
    [property: JsonPropertyName("nickname")] string Nickname
);

public sealed record RedeemRequest(
    [property: JsonPropertyName("password")]         string Password,
    [property: JsonPropertyName("currentNickname")]  string CurrentNickname,
    [property: JsonPropertyName("currentProfileId")] string CurrentProfileId
);

// crate-derived pick registration body. crate-derived picks have no authored cosmetics
// (color/name/desc) — those default to hash-color / "GoldenPick" / no description. only
// the pick_number from the crate carries through. ownerProfileId is the SPT sessionId
// (= profileId) of the recipient — stable identity that survives renames.
public sealed record RegisterCrateDerivedRequest(
    [property: JsonPropertyName("pickId")]         string  PickId,
    [property: JsonPropertyName("ownerProfileId")] string? OwnerProfileId,
    [property: JsonPropertyName("ownerNickname")]  string  OwnerNickname,
    [property: JsonPropertyName("awardedAt")]      long    AwardedAt,
    [property: JsonPropertyName("signature")]      string  Signature,
    [property: JsonPropertyName("pickNumber")]     int?    PickNumber
);

// kill submission body. relay verifies owner_profile_id matches (nickname fallback for
// legacy NULL rows). on success, owner_nickname is refreshed to the killer's current
// nickname — rename propagation for free.
public sealed record PickKillRequest(
    [property: JsonPropertyName("pickId")]          string PickId,
    [property: JsonPropertyName("killerProfileId")] string KillerProfileId,
    [property: JsonPropertyName("killerNickname")]  string KillerNickname
);

// SPT-side calls this after admin-grant delivery (when it finally knows the recipient's
// profileId) or any time it observes a fresh nickname for a profileId. fills/refreshes
// the identity columns on an existing pick.
public sealed record UpdateOwnerProfileRequest(
    [property: JsonPropertyName("pickId")]    string PickId,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("nickname")]  string Nickname
);

// admin/grant-pick body — all metadata fields nullable except nickname. nickname is who
// receives the pick (recipient self-selects by matching). pick_number is admin-input (no
// auto-counter on this path; crate-derived picks will get auto-numbering in a later pass).
public sealed record AdminGrantPickRequest(
    [property: JsonPropertyName("nickname")]          string  Nickname,
    [property: JsonPropertyName("sheenColorHex")]     string? SheenColorHex,
    [property: JsonPropertyName("customName")]        string? CustomName,
    [property: JsonPropertyName("customDescription")] string? CustomDescription,
    [property: JsonPropertyName("pickNumber")]        int?    PickNumber,
    [property: JsonPropertyName("password")]          string? Password
);

// admin update — password identifies which existing row to mutate. all metadata fields
// nullable; null values write NULL into the row (so admin can "clear" a previously-set name
// by sending null). pick_id + owner_nickname are NEVER touched here.
public sealed record AdminUpdatePickRequest(
    [property: JsonPropertyName("password")]          string  Password,
    [property: JsonPropertyName("sheenColorHex")]     string? SheenColorHex,
    [property: JsonPropertyName("customName")]        string? CustomName,
    [property: JsonPropertyName("customDescription")] string? CustomDescription,
    [property: JsonPropertyName("pickNumber")]        int?    PickNumber
);

sealed class ClientConn
{
    public Guid Id { get; } = Guid.NewGuid();
    public WebSocket Socket { get; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public DateTime LastAccepted { get; set; } = DateTime.MinValue;
    public ClientConn(WebSocket socket) => Socket = socket;
}
