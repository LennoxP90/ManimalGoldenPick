# Remove External Relay — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove ManimalGoldenPick's dependency on the external Fly.io relay by folding the crate drop-roll + leaderboard into the SPT server mod, and stripping anti-cheat, live toasts, and admin/redeem features.

**Architecture:** The SPT server mod becomes fully self-contained. It owns the survived-raid counter, the drop roll, and the pick/kill leaderboard in local JSON stores (the codebase's existing persistence pattern — NOT SQLite; only the deleted relay used SQLite). The client mod loses its WebSocket link and signature verification; it only plays the local reveal on unpack and posts kills to the local server. No outbound internet from either side.

**Tech Stack:** C# / .NET 9 (server mod, built via NuGet `SPTarkov.*` 4.0.13, runs on the Linux container), C# / netstandard2.1 (BepInEx client plugin, built against local EFT at `F:\Games\SPT-4.0`), Harmony, SPT StaticRouter DI.

## Global Constraints

- Server mod target: `net9.0`, `AnyCPU`, `Nullable=enable`. Built via NuGet; runs unchanged on the Linux container. Copied verbatim from `GoldenPickServer.csproj`.
- Client mod target: `netstandard2.1`, `Nullable=disable`. References local EFT/BepInEx under **`F:\Games\SPT-4.0`** (contains `EscapeFromTarkov_Data\Managed\` and `BepInEx\`).
- Version single-sourced from `Directory.Build.props` `<ModVersion>` (currently `1.0.6`). Bump it there, nowhere else.
- Plugin/mod GUID `com.manimal.goldenpick` and item tpls — crate `9c2f1a0b7e6d4c83a5f10b2e`, pick `6a371980784a6d8a3ec033ed` — MUST stay unchanged (they key existing profiles/items and the WTT custom-item registration).
- Drop-roll constants copied verbatim from the relay: `RaidCycleSize = 5`, `DropProbability = 0.0051`. Roll only on a fully-survived, non-runthrough raid, on every 5th such raid.
- No new external dependencies. Remove `BouncyCastle.Cryptography` (only anti-cheat used it).
- Toolchain present: `dotnet 10.0.301`; `ssh spt` = `dlennox@192.168.30.11` (key auth).
- Work happens on branch `remove-relay` in `F:\Code\SPT docker server\mods\ManimalGoldenPick`. Commit after every task.

**Note on testing:** this repo has **no unit-test harness**, and the bulk of the code is Harmony patches / SPT DI services that only run inside a live game+server. Introducing a test framework is out of scope. Verification is therefore: (a) `dotnet build` succeeds with zero errors for each changed project, and (b) targeted in-game / server-log observation at the integration milestones (Tasks 12–13). The one piece of pure logic — the drop roll — is isolated into a `DropOracle` with an injectable RNG so it can be exercised deterministically by a temporary boosted probability during the manual test.

---

## Part A — Server mod: new local state + drop oracle

### Task 1: Prove the baseline builds locally (retarget client references)

The client `.csproj` hardcodes the upstream author's `D:\SPTDev` paths. Retarget them to `F:\Games\SPT-4.0` and confirm both projects build **before** changing any behavior — this de-risks the whole plan by proving the toolchain first.

**Files:**
- Modify: `GoldenPickClient/GoldenPickClient.csproj` (all `D:\SPTDev` occurrences + PostBuild)

- [ ] **Step 1: Retarget every reference HintPath and the PostBuild copy**

In `GoldenPickClient/GoldenPickClient.csproj`, replace every occurrence of `D:\SPTDev\` with `F:\Games\SPT-4.0\`. There are reference `HintPath`s (lines ~62–153) and the PostBuild `Exec` copy target (lines ~159–165). After the replace, the references resolve as:
- `F:\Games\SPT-4.0\BepInEx\core\0Harmony.dll`, `...\BepInEx.dll`
- `F:\Games\SPT-4.0\BepInEx\plugins\spt\spt-common.dll`, `...\spt-reflection.dll`
- `F:\Games\SPT-4.0\EscapeFromTarkov_Data\Managed\*.dll`
- PostBuild target dir: `F:\Games\SPT-4.0\BepInEx\plugins\GoldenPick`

- [ ] **Step 2: Restore + build the client**

Run:
```bash
cd "F:/Code/SPT docker server/mods/ManimalGoldenPick"
dotnet build GoldenPickClient/GoldenPickClient.csproj -c Debug
```
Expected: `Build succeeded. 0 Error(s)`. `GoldenPickClient.dll` appears under `GoldenPickClient/bin/Debug/netstandard2.1/` and is copied to `F:\Games\SPT-4.0\BepInEx\plugins\GoldenPick\`. If NuGet can't resolve `WTT-ClientCommonLib` / `BouncyCastle.Cryptography`, run `dotnet restore` first and confirm internet access.

- [ ] **Step 3: Build the server**

Run:
```bash
dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug
```
Expected: `Build succeeded. 0 Error(s)`. (This also builds the client first via the ProjectReference and produces the release zip via `PackageModForDistribution`; that's fine for the baseline.)

- [ ] **Step 4: Commit**

```bash
git add GoldenPickClient/GoldenPickClient.csproj
git commit -m "build: retarget client references to F:\\Games\\SPT-4.0"
```

---

### Task 2: `RaidCounterStore` — local survived-raid counter

Replaces the relay's `profile_raids` table with a JSON store matching the existing `PickMetadataStore` / `CrateSignatureStore` pattern.

**Files:**
- Create: `GoldenPickServer/RaidProgress/RaidCounterStore.cs`

**Interfaces:**
- Produces: `RaidCounterStore` (DI singleton) with `int IncrementSurvived(string profileId, string nickname)` and `int GetCount(string profileId)`.

- [ ] **Step 1: Write the store**

Create `GoldenPickServer/RaidProgress/RaidCounterStore.cs`:
```csharp
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// per-profile survived-raid counter. JSON side-channel at <mod-dir>/data/raid_counters.json,
// keyed by profileId (SPT sessionId). replaces the relay's SQLite profile_raids table — the
// data volume (a handful of players) doesn't justify a DB, and this matches the existing
// PickMetadataStore / CrateSignatureStore file-store pattern.
[Injectable(InjectionType.Singleton)]
public class RaidCounterStore(ISptLogger<RaidCounterStore> logger)
{
    public sealed record Counter(int SurvivedCount, string Nickname, long LastUpdated);

    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(typeof(RaidCounterStore).Assembly.Location)!,
        "data", "raid_counters.json");

    private Dictionary<string, Counter>? _counters;
    private readonly object _lock = new();

    private Dictionary<string, Counter> Load()
    {
        if (_counters != null) return _counters;
        try
        {
            _counters = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Counter>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] raid counter load failed, starting fresh: {e.Message}");
            _counters = new();
        }
        return _counters;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_counters,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { logger.Error($"[GoldenPick] raid counter save failed: {e.Message}"); }
    }

    // bump the survived counter for this profile and return the new total (starts at 1).
    public int IncrementSurvived(string profileId, string nickname)
    {
        lock (_lock)
        {
            var map = Load();
            var next = (map.TryGetValue(profileId, out var c) ? c.SurvivedCount : 0) + 1;
            map[profileId] = new Counter(next, nickname, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Save();
            return next;
        }
    }

    public int GetCount(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return 0;
        lock (_lock)
        {
            return Load().TryGetValue(profileId, out var c) ? c.SurvivedCount : 0;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add GoldenPickServer/RaidProgress/RaidCounterStore.cs
git commit -m "feat(server): local survived-raid counter store"
```

---

### Task 3: `DropOracle` — the roll, isolated and testable

**Files:**
- Create: `GoldenPickServer/RaidProgress/DropOracle.cs`

**Interfaces:**
- Produces: `static class DropOracle` with `const int RaidCycleSize = 5;`, `const double DropProbability = 0.0051;`, and `bool ShouldAward(int survivedCount, Func<double> nextRandom)`.

- [ ] **Step 1: Write the oracle**

Create `GoldenPickServer/RaidProgress/DropOracle.cs`:
```csharp
namespace GoldenPick.RaidProgress;

// the crate drop decision, ported verbatim from the relay's /raid/end handler. pure logic +
// an injected RNG so it's deterministic under test: a cycle-boundary raid rolls nextRandom()
// and awards when the roll lands under DropProbability.
public static class DropOracle
{
    public const int RaidCycleSize = 5;        // roll once every Nth survived raid
    public const double DropProbability = 0.0051; // 0.51%

    // survivedCount is the post-increment total. returns true only on a cycle boundary AND a
    // winning roll. nextRandom must yield [0,1); pass () => Random.Shared.NextDouble() in prod.
    public static bool ShouldAward(int survivedCount, Func<double> nextRandom)
    {
        if (survivedCount <= 0 || survivedCount % RaidCycleSize != 0) return false;
        return nextRandom() < DropProbability;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Manually verify the boundary logic (throwaway check)**

Reason: no test harness. Do a 30-second sanity check with `dotnet run` on a scratch, or reason it through: `ShouldAward(5, () => 0.0)` → true; `ShouldAward(4, () => 0.0)` → false (not a boundary); `ShouldAward(5, () => 0.9)` → false (roll too high). Confirm by inspection that the code matches these three cases, then move on.

- [ ] **Step 4: Commit**

```bash
git add GoldenPickServer/RaidProgress/DropOracle.cs
git commit -m "feat(server): DropOracle roll logic with injectable RNG"
```

---

### Task 4: `CrateSignatureStore` → `CrateRecordStore` (drop signatures, add pick-number counter)

The crate record no longer holds a signature — only what's needed to carry the pick number from award through unpack, plus the global "Pick #N" counter (was `RaidStore.NextCratePickNumber`).

**Files:**
- Rename+rewrite: `GoldenPickServer/RaidProgress/CrateSignatureStore.cs` → `GoldenPickServer/RaidProgress/CrateRecordStore.cs`

**Interfaces:**
- Produces: `CrateRecordStore` (DI singleton) with `record CrateRecord(long AwardedAt, string ProfileId, int? PickNumber)`, `void Record(string crateId, long awardedAt, string profileId, int? pickNumber)`, `CrateRecord? TryGet(string crateId)`, `int NextPickNumber()`.
- Consumed by: Tasks 5 (InheritPickMetaRouter), 6 (CrateSignatureRouter→CrateRecordRouter), 7 (SurvivedRaidCrateService).

- [ ] **Step 1: Delete the old file, create the new one**

```bash
git rm GoldenPickServer/RaidProgress/CrateSignatureStore.cs
```
Create `GoldenPickServer/RaidProgress/CrateRecordStore.cs`:
```csharp
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// per-crate-id record persisted at <mod-dir>/data/crate_records.json. carries the pick number
// (and award time) from the server-side drop roll through to the client's unpack, and hands out
// the global auto-incremented "Pick #N". no signature: anti-cheat was removed — the server mints
// the crate, so a record's mere existence means "this server minted it" (enough to reject a
// console-spawned crate at unpack).
[Injectable(InjectionType.Singleton)]
public class CrateRecordStore(ISptLogger<CrateRecordStore> logger)
{
    public sealed record CrateRecord(long AwardedAt, string ProfileId, int? PickNumber);

    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(typeof(CrateRecordStore).Assembly.Location)!,
        "data", "crate_records.json");

    private Dictionary<string, CrateRecord>? _records;
    private readonly object _lock = new();

    private Dictionary<string, CrateRecord> Load()
    {
        if (_records != null) return _records;
        try
        {
            _records = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, CrateRecord>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] crate record load failed, starting fresh: {e.Message}");
            _records = new();
        }
        return _records;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_records,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { logger.Error($"[GoldenPick] crate record save failed: {e.Message}"); }
    }

    public void Record(string crateId, long awardedAt, string profileId, int? pickNumber)
    {
        lock (_lock)
        {
            var map = Load();
            map[crateId] = new CrateRecord(awardedAt, profileId, pickNumber);
            Save();
        }
    }

    public CrateRecord? TryGet(string crateId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(crateId, out var r) ? r : null;
        }
    }

    // global "Pick #N": one more than the max pick number across all recorded crates. lock-held
    // so two concurrent awards can't collide. starts at 1.
    public int NextPickNumber()
    {
        lock (_lock)
        {
            var max = 0;
            foreach (var r in Load().Values)
                if (r.PickNumber is int n && n > max) max = n;
            return max + 1;
        }
    }
}
```

- [ ] **Step 2: Build (expect errors in consumers — that's the next tasks)**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: FAIL — `CrateSignatureStore` no longer exists, so `CrateSignatureRouter.cs`, `InheritPickMetaRouter.cs`, `GrantCrateRouter.cs`, `SurvivedRaidCrateService.cs` won't compile. That's expected; Tasks 5–8 fix them. Do not commit yet.

- [ ] **Step 3: Commit (after Task 8 makes it build)**

Deferred — commit `CrateRecordStore` together with its consumers at the end of Task 8 so the tree always builds green at a commit.

---

### Task 5: `PickMetadataStore` — drop signature, add kill count + leaderboard

**Files:**
- Modify: `GoldenPickServer/RaidProgress/PickMetadataStore.cs`

**Interfaces:**
- Produces: `PickMetadata` record gains `int KillCount = 0`, loses `Signature`. New methods `int? IncrementKill(string pickId, string killerProfileId, string killerNickname)` (owner-gated) and `List<LeaderboardEntry> GetLeaderboard()` with `record LeaderboardEntry(string PickId, string? OwnerProfileId, string OwnerNickname, long AwardedAt, string? SheenColorHex, string? CustomName, string? CustomDescription, int? PickNumber, int KillCount)`.
- Removes: `Snapshot()` (only the deleted `LeaderboardBackfillOnLoad` used it) and `Count` (only the deleted counterfeit audit used it).

- [ ] **Step 1: Update the `PickMetadata` record**

In `PickMetadataStore.cs`, replace the record (drop `Signature`, add `KillCount`):
```csharp
    public sealed record PickMetadata(
        string? OwnerProfileId,
        string OwnerNickname,
        long AwardedAt,
        string? SheenColorHex,
        string? CustomName,
        string? CustomDescription,
        int? PickNumber,
        int KillCount = 0);
```
(`KillCount` has a default so existing `pick_metadata.json` records without the field deserialize to 0, and the named-argument `Put(...)` call in `InheritPickMetaRouter` still compiles.)

- [ ] **Step 2: Delete the now-orphaned `Count` and `Snapshot()` members**

Remove the `public int Count { ... }` property and the `public IReadOnlyList<KeyValuePair<string, PickMetadata>> Snapshot()` method — their only callers (`CounterfeitPickAudit`, `LeaderboardBackfillOnLoad`) are deleted in Task 9. `UpdateCosmetics` stays as-is (its `with` expression doesn't touch `Signature` or `KillCount`).

- [ ] **Step 3: Add kill increment + leaderboard methods**

Add inside the class (before the closing brace):
```csharp
    // owner-gated kill increment. matches on OwnerProfileId when present, falls back to
    // case-insensitive nickname for legacy rows. on match, refreshes owner identity (rename
    // propagation) and returns the new kill count; null if the pick is unknown or not owned
    // by the killer. no rate-limiting — this is a trusted private server.
    public int? IncrementKill(string pickId, string killerProfileId, string killerNickname)
    {
        if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(killerProfileId) || string.IsNullOrEmpty(killerNickname))
            return null;
        lock (_lock)
        {
            var map = Load();
            if (!map.TryGetValue(pickId, out var m)) return null;

            bool owns = !string.IsNullOrEmpty(m.OwnerProfileId)
                ? string.Equals(m.OwnerProfileId, killerProfileId, StringComparison.Ordinal)
                : string.Equals(m.OwnerNickname, killerNickname, StringComparison.OrdinalIgnoreCase);
            if (!owns) return null;

            var updated = m with
            {
                OwnerProfileId = killerProfileId,
                OwnerNickname  = killerNickname,
                KillCount      = m.KillCount + 1,
            };
            map[pickId] = updated;
            Save();
            return updated.KillCount;
        }
    }

    public sealed record LeaderboardEntry(
        string PickId, string? OwnerProfileId, string OwnerNickname, long AwardedAt,
        string? SheenColorHex, string? CustomName, string? CustomDescription,
        int? PickNumber, int KillCount);

    // every registered pick, ordered by kills desc then pick number asc. drives /goldenpick/leaderboard.
    public List<LeaderboardEntry> GetLeaderboard()
    {
        lock (_lock)
        {
            return Load()
                .Select(kv => new LeaderboardEntry(
                    kv.Key, kv.Value.OwnerProfileId, kv.Value.OwnerNickname, kv.Value.AwardedAt,
                    kv.Value.SheenColorHex, kv.Value.CustomName, kv.Value.CustomDescription,
                    kv.Value.PickNumber, kv.Value.KillCount))
                .OrderByDescending(e => e.KillCount)
                .ThenBy(e => e.PickNumber ?? int.MaxValue)
                .ThenBy(e => e.AwardedAt)
                .ToList();
        }
    }
```
Add `using System.Linq;` at the top if not already present (with `ImplicitUsings=enable` on this project it is implicit — confirm the build).

- [ ] **Step 4: Build (still expect Task-4 consumer errors)**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: FAIL only on the `CrateSignatureStore` consumers (Tasks 6–8) and on `PickMetaRouter`/`InheritPickMetaRouter` referencing `Signature` (fixed in Task 6). No new unexpected errors from this file.

- [ ] **Step 5: Commit (deferred with Task 8)** — keep with the consumer fixes so every commit builds.

---

### Task 6: Update routers that consumed the old stores/fields

Fix the three routers that referenced `CrateSignatureStore` or `PickMetadata.Signature`, and re-point `InheritPickMetaRouter` to register the pick locally instead of calling the relay.

**Files:**
- Modify: `GoldenPickServer/Router/CrateSignatureRouter.cs`
- Modify: `GoldenPickServer/Router/PickMetaRouter.cs`
- Modify: `GoldenPickServer/Router/InheritPickMetaRouter.cs`

**Interfaces:**
- Consumes: `CrateRecordStore` (Task 4), `PickMetadataStore.PickMetadata` without `Signature` (Task 5).

- [ ] **Step 1: `CrateSignatureRouter` → return `{found, pickNumber}`**

Replace the body of `CrateSignatureRouter.cs`'s route action. Keep the class name and route path `/goldenpick/cratesig` (the client already posts there). Swap the store type and payload:
```csharp
[Injectable]
public class CrateSignatureRouter(JsonUtil jsonUtil, CrateRecordStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<CrateSignatureRequest>(
                "/goldenpick/cratesig",
                (url, info, sessionId, output) =>
                {
                    var rec = store.TryGet(info.CrateId);
                    if (rec == null) return new ValueTask<string>("{\"found\":false}");
                    var payload = JsonSerializer.Serialize(new
                    {
                        found = true,
                        pickNumber = rec.PickNumber,
                    });
                    return new ValueTask<string>(payload);
                }
            ),
        ]
    ) { }
```
Leave `CrateSignatureRequest.cs` unchanged (it still carries `crateId`).

- [ ] **Step 2: `PickMetaRouter` — remove the `signature` field from the response**

In `PickMetaRouter.cs`, delete the `signature = r.Signature,` line from the serialized payload. The client's `MetaResp` never read it, so this is a clean removal.

- [ ] **Step 3: `InheritPickMetaRouter` — local register, no relay, no signature**

Rewrite `InheritPickMetaRouter.cs`: drop the `GoldenPickRelayClient` dependency and the fire-and-forget `RegisterCrateDerivedPick` call; use `CrateRecordStore`; build `PickMetadata` without `Signature`:
```csharp
using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// called by the client at the unpack moment. copies the source crate's pick_number into a fresh
// PickMetadataStore record keyed by the new pickId, which ALSO registers the pick on the local
// leaderboard (GetLeaderboard enumerates the store). crate-derived picks get default cosmetics.
[Injectable]
public class InheritPickMetaRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Models.Utils.ISptLogger<InheritPickMetaRouter> logger,
    CrateRecordStore crateStore,
    PickMetadataStore pickStore,
    ProfileHelper profileHelper)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<InheritPickMetaRequest>(
                "/goldenpick/inherit-pickmeta",
                (url, info, sessionId, output) =>
                {
                    try
                    {
                        var crate = crateStore.TryGet(info.SourceCrateId);
                        if (crate == null)
                        {
                            logger.Warning($"[GoldenPick] inherit-pickmeta: source crate {info.SourceCrateId} not in store; pick {info.PickId} gets no number");
                            return new ValueTask<string>("{\"ok\":false,\"reason\":\"crate_missing\"}");
                        }

                        var pmc = profileHelper.GetPmcProfile(sessionId);
                        var nickname = pmc?.Info?.Nickname ?? crate.ProfileId;

                        pickStore.Put(info.PickId, new PickMetadataStore.PickMetadata(
                            OwnerProfileId: sessionId.ToString(),
                            OwnerNickname: nickname,
                            AwardedAt: crate.AwardedAt,
                            SheenColorHex: null,
                            CustomName: null,
                            CustomDescription: null,
                            PickNumber: crate.PickNumber));

                        logger.Info($"[GoldenPick] inherit-pickmeta: pick {info.PickId} ← crate {info.SourceCrateId} (#{crate.PickNumber?.ToString() ?? "none"}) owner={nickname}");
                        return new ValueTask<string>("{\"ok\":true}");
                    }
                    catch (Exception e)
                    {
                        logger.Error($"[GoldenPick] inherit-pickmeta failed: {e.Message}");
                        return new ValueTask<string>("{\"ok\":false,\"reason\":\"exception\"}");
                    }
                }
            ),
        ]
    ) { }
```

- [ ] **Step 4: Build (expect only Task-7/8 errors remain)**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: FAIL only on `GrantCrateRouter.cs` and `SurvivedRaidCrateService.cs` (still reference `CrateSignatureStore` / relay). Fixed next.

---

### Task 7: Rewrite `SurvivedRaidCrateService` — roll locally, mint, mail

**Files:**
- Modify: `GoldenPickServer/RaidProgress/SurvivedRaidCrateService.cs`

**Interfaces:**
- Consumes: `RaidCounterStore` (Task 2), `DropOracle` (Task 3), `CrateRecordStore` (Task 4).

- [ ] **Step 1: Swap dependencies and the post-raid flow**

In `SurvivedRaidCrateService.cs`:

1. In the "our own deps" section of the primary constructor, **remove** `GoldenPickRelayClient relayClient,`, `CrateSignatureStore signatureStore,`, and `Audit.CounterfeitPickAudit counterfeitAudit,`. **Add** `RaidCounterStore raidCounters,` and `CrateRecordStore crateStore,`. Leave every base-service passthrough dependency untouched (the `LocationLifecycleService(...)` base call and its 26 args stay exactly as they are).

2. In `EndLocalRaid`, **remove** the counterfeit-audit line:
```csharp
        _ = counterfeitAudit.ScanProfile(sessionId);
```
Keep `base.EndLocalRaid(...)` first and the `_ = NotifyRelayAndMaybeMail(...)` fire-and-forget call (renamed below).

3. Replace `NotifyRelayAndMaybeMail` with a local roll (rename to `RollAndMaybeMail`):
```csharp
    private async Task RollAndMaybeMail(MongoId sessionId, EndLocalRaidRequestData request)
    {
        await Task.Yield(); // keep the async signature; work below is synchronous + cheap
        try
        {
            var survived   = request.Results?.Result == ExitStatus.SURVIVED;
            var runthrough = request.Results?.Result == ExitStatus.RUNNER;
            if (!survived || runthrough) return;

            var pmc = profileHelper.GetPmcProfile(sessionId);
            var nickname = pmc?.Info?.Nickname ?? "An operative";

            var newCount = raidCounters.IncrementSurvived(sessionId.ToString(), nickname);
            ourLogger.Info($"[GoldenPick] survived raid — count={newCount}");

            if (!DropOracle.ShouldAward(newCount, () => Random.Shared.NextDouble())) return;

            // WIN: mint a crate id, assign the next global pick number, record + mail.
            var crateId   = MintMongoId();
            var awardedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pickNumber = crateStore.NextPickNumber();
            crateStore.Record(crateId, awardedAt, sessionId.ToString(), pickNumber);
            GrantCrate(sessionId, crateId);
            ourLogger.Info($"[GoldenPick] AWARD count={newCount} crate={crateId} pick#={pickNumber}");
        }
        catch (Exception e) { ourLogger.Error($"[GoldenPick] post-raid roll failed: {e.Message}"); }
    }

    // 24-char lowercase hex, EFT MongoId shape
    private static string MintMongoId()
    {
        var bytes = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
```
Update the caller in `EndLocalRaid` to `_ = RollAndMaybeMail(sessionId, request);`.

4. Replace `GrantCrate(MongoId, CrateAward)` with the signature-free version:
```csharp
    private void GrantCrate(MongoId sessionId, string crateId)
    {
        try
        {
            var (ok, tpl) = itemHelper.GetItem(CrateTpl);
            if (!ok || tpl == null) { ourLogger.Error($"[GoldenPick] crate tpl '{CrateTpl}' not in db — cant mail"); return; }

            var crate = new Item
            {
                Id = new MongoId(crateId),
                Template = tpl.Id,
                Upd = itemHelper.GenerateUpdForItem(tpl),
            };
            itemHelper.SetFoundInRaid(new List<Item> { crate });
            mailSendService.SendSystemMessageToPlayer(sessionId, MailMessage, new List<Item> { crate });
            ourLogger.Info($"[GoldenPick] crate mailed to {sessionId} (id={crateId})");
        }
        catch (Exception e) { ourLogger.Error($"[GoldenPick] crate grant failed: {e.Message}"); }
    }
```

5. Delete the now-unused `CrateAward` reference usage — it lived in `GoldenPickRelayClient.cs` (deleted in Task 9), so no local definition remains to remove here.

- [ ] **Step 2: Build together with the deletions** — proceed to Task 8/9 before building green.

---

### Task 8: Delete `GrantCrateRouter` + its request, then build green

`GrantCrateRouter` only existed to let the client forward a relay `crate_grant` broadcast to be minted server-side. Crates are now minted directly in `RollAndMaybeMail`, so this route is dead.

**Files:**
- Delete: `GoldenPickServer/Router/GrantCrateRouter.cs`, `GoldenPickServer/Router/GrantCrateRequest.cs`

- [ ] **Step 1: Remove the files**

```bash
git rm GoldenPickServer/Router/GrantCrateRouter.cs GoldenPickServer/Router/GrantCrateRequest.cs
```

- [ ] **Step 2: Build the server green**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`. (Tasks 4–8 now cohere: new stores + oracle in, old signature/relay consumers gone.) If errors mention `GoldenPickRelayClient`, `CounterfeitPickAudit`, or `LeaderboardBackfillOnLoad`, they'll be resolved by Task 9 — if the build still references them here, temporarily proceed to Task 9 and build after.

- [ ] **Step 3: Commit the whole server-state refactor**

```bash
git add -A GoldenPickServer/
git commit -m "feat(server): fold drop roll + pick registration into local stores"
```

---

### Task 9: Delete the relay client, backfill, audit, and admin/redeem code

**Files:**
- Delete: `GoldenPickServer/RaidProgress/GoldenPickRelayClient.cs`
- Delete: `GoldenPickServer/RaidProgress/LeaderboardBackfillOnLoad.cs`
- Delete: `GoldenPickServer/Audit/CounterfeitPickAudit.cs`, `GoldenPickServer/Audit/MailDeliveryAuditPatch.cs`
- Delete: `GoldenPickServer/Commands/RedeemPickCommand.cs`
- Delete: `GoldenPickServer/Router/GrantPickRouter.cs`, `GoldenPickServer/Router/GrantPickRequest.cs`
- Delete: `GoldenPickServer/Router/UpdatePickMetaRouter.cs`, `GoldenPickServer/Router/UpdatePickMetaRequest.cs`
- Delete: `GoldenPickServer/Router/GoldenPickStaticRouter.cs`, `GoldenPickServer/Router/GoldenPickAnnounceRequest.cs`

- [ ] **Step 1: Remove all of them**

```bash
git rm \
  GoldenPickServer/RaidProgress/GoldenPickRelayClient.cs \
  GoldenPickServer/RaidProgress/LeaderboardBackfillOnLoad.cs \
  GoldenPickServer/Audit/CounterfeitPickAudit.cs \
  GoldenPickServer/Audit/MailDeliveryAuditPatch.cs \
  GoldenPickServer/Commands/RedeemPickCommand.cs \
  GoldenPickServer/Router/GrantPickRouter.cs \
  GoldenPickServer/Router/GrantPickRequest.cs \
  GoldenPickServer/Router/UpdatePickMetaRouter.cs \
  GoldenPickServer/Router/UpdatePickMetaRequest.cs \
  GoldenPickServer/Router/GoldenPickStaticRouter.cs \
  GoldenPickServer/Router/GoldenPickAnnounceRequest.cs
```

- [ ] **Step 2: Confirm nothing else references the deleted types**

Run:
```bash
cd "F:/Code/SPT docker server/mods/ManimalGoldenPick"
grep -rEl "GoldenPickRelayClient|CounterfeitPickAudit|MailDeliveryAudit|RedeemPickCommand|LeaderboardBackfill|GrantPickR|UpdatePickMetaR|GoldenPickStaticRouter|GoldenPickAnnounceRequest|CrateSignatureStore" GoldenPickServer/
```
Expected: no matches (empty output). If any file matches, open it and remove the reference.

- [ ] **Step 3: Build green**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add -A GoldenPickServer/
git commit -m "chore(server): delete relay client, backfill, audit, admin/redeem"
```

---

### Task 10: Add the leaderboard + kill routes

**Files:**
- Create: `GoldenPickServer/Router/PickKillRouter.cs`, `GoldenPickServer/Router/PickKillRequest.cs`
- Create: `GoldenPickServer/Router/LeaderboardRouter.cs`, `GoldenPickServer/Router/LeaderboardRequest.cs`

**Interfaces:**
- Consumes: `PickMetadataStore.IncrementKill` + `GetLeaderboard` (Task 5).
- Produces: routes `/goldenpick/pick/kill` and `/goldenpick/leaderboard`.

- [ ] **Step 1: Kill request DTO**

Create `GoldenPickServer/Router/PickKillRequest.cs`:
```csharp
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

public record PickKillRequest : IRequestData
{
    [JsonPropertyName("pickId")]          public required string PickId          { get; set; }
    [JsonPropertyName("killerProfileId")] public required string KillerProfileId { get; set; }
    [JsonPropertyName("killerNickname")]  public required string KillerNickname  { get; set; }
}
```

- [ ] **Step 2: Kill router**

Create `GoldenPickServer/Router/PickKillRouter.cs`:
```csharp
using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// client posts a confirmed golden-pick kill here. owner-gated increment (a player can only
// raise their own pick's count); unknown/not-owned picks return {ok:false}.
[Injectable]
public class PickKillRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Models.Utils.ISptLogger<PickKillRouter> logger,
    PickMetadataStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<PickKillRequest>(
                "/goldenpick/pick/kill",
                (url, info, sessionId, output) =>
                {
                    var newCount = store.IncrementKill(info.PickId, info.KillerProfileId, info.KillerNickname);
                    if (newCount == null)
                    {
                        logger.Warning($"[GoldenPick] kill rejected pickId={info.PickId} killer={info.KillerNickname} (unknown or not owner)");
                        return new ValueTask<string>("{\"ok\":false,\"reason\":\"not_owner_or_unknown\"}");
                    }
                    logger.Info($"[GoldenPick] kill pickId={info.PickId} killer={info.KillerNickname} newCount={newCount}");
                    return new ValueTask<string>(JsonSerializer.Serialize(new { ok = true, killCount = newCount }));
                }
            ),
        ]
    ) { }
```

- [ ] **Step 3: Leaderboard request DTO + router**

Create `GoldenPickServer/Router/LeaderboardRequest.cs`:
```csharp
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// no input fields — the leaderboard is a full snapshot. present so StaticRouter's RouteAction<T>
// has a body type to bind.
public record LeaderboardRequest : IRequestData { }
```
Create `GoldenPickServer/Router/LeaderboardRouter.cs`:
```csharp
using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// public leaderboard JSON — every registered pick ordered by kills. consumed by leaderboard.html
// and any tool using the SPT client HTTP transport.
[Injectable]
public class LeaderboardRouter(JsonUtil jsonUtil, PickMetadataStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<LeaderboardRequest>(
                "/goldenpick/leaderboard",
                (url, info, sessionId, output) =>
                {
                    var picks = store.GetLeaderboard();
                    return new ValueTask<string>(JsonSerializer.Serialize(new { ok = true, picks }));
                }
            ),
        ]
    ) { }
```

- [ ] **Step 4: Build green**

Run: `dotnet build GoldenPickServer/GoldenPickServer.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add GoldenPickServer/Router/PickKillRouter.cs GoldenPickServer/Router/PickKillRequest.cs GoldenPickServer/Router/LeaderboardRouter.cs GoldenPickServer/Router/LeaderboardRequest.cs
git commit -m "feat(server): leaderboard + owner-gated kill routes"
```

---

## Part B — Client mod: strip the relay + signature verification

### Task 11: Gut the relay wiring from the client

**Files:**
- Delete: `GoldenPickClient/Net/RelayClient.cs`, `GoldenPickClient/Net/CrateGrantBridge.cs`, `GoldenPickClient/Net/PickGrantBridge.cs`, `GoldenPickClient/Net/PickMetadataUpdateBridge.cs`, `GoldenPickClient/Net/EarnEvent.cs`, `GoldenPickClient/Net/ServerMail.cs`
- Delete: `GoldenPickClient/Unbox/CounterfeitDetector.cs`
- Delete: `GoldenPickClient/RaidCounter/RaidCounterOverlay.cs`
- Create: `GoldenPickClient/Unbox/CrateRecordCheck.cs`
- Modify: `GoldenPickClient/Plugin.cs`, `GoldenPickClient/Earn/GoldenPickEarner.cs`, `GoldenPickClient/Net/PickKillBridge.cs`, `GoldenPickClient/Unbox/GoldenCrateUnpackPatch.cs`

**Interfaces:**
- Produces: `static class CrateRecordCheck` with `bool IsServerMinted(string crateId)`.
- Consumes: server routes `/goldenpick/cratesig` (Task 6), `/goldenpick/pick/kill` (Task 10).

- [ ] **Step 1: Delete the relay + anti-cheat + overlay files**

```bash
cd "F:/Code/SPT docker server/mods/ManimalGoldenPick"
git rm \
  GoldenPickClient/Net/RelayClient.cs \
  GoldenPickClient/Net/CrateGrantBridge.cs \
  GoldenPickClient/Net/PickGrantBridge.cs \
  GoldenPickClient/Net/PickMetadataUpdateBridge.cs \
  GoldenPickClient/Net/EarnEvent.cs \
  GoldenPickClient/Net/ServerMail.cs \
  GoldenPickClient/Unbox/CounterfeitDetector.cs \
  GoldenPickClient/RaidCounter/RaidCounterOverlay.cs
```

- [ ] **Step 2: Add the lightweight server-minted check (replaces CounterfeitDetector)**

Create `GoldenPickClient/Unbox/CrateRecordCheck.cs`:
```csharp
using System;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Unbox
{
    // asks the local SPT server whether it minted this crate (POST /goldenpick/cratesig).
    // NOT anti-cheat — there's no signature anymore — just enough to stop a console-spawned
    // crate (no server record) from unpacking into a real pick. fail-open on network error so
    // a transient hiccup never blocks a legitimately-earned crate.
    internal static class CrateRecordCheck
    {
        public static bool IsServerMinted(string crateId)
        {
            if (string.IsNullOrEmpty(crateId)) return false;
            try
            {
                var body = JsonConvert.SerializeObject(new Req { crateId = crateId });
                var resp = RequestHandler.PostJson("/goldenpick/cratesig", body);
                var parsed = JsonConvert.DeserializeObject<Resp>(resp);
                return parsed != null && parsed.found;
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogWarning($"[GoldenPick] crate record check errored ({e.GetType().Name}): {e.Message} — allowing unpack");
                return true; // fail-open
            }
        }

        private class Req { public string crateId; }
        private class Resp { public bool found; public int? pickNumber; }
    }
}
```

- [ ] **Step 3: `GoldenCrateUnpackPatch` — swap the counterfeit check**

In `GoldenPickClient/Unbox/GoldenCrateUnpackPatch.cs`, replace the counterfeit block (the `if (!CounterfeitDetector.IsLegitimate(targetItem.Id))` stanza, ~lines 58–68) with:
```csharp
            // only crates the server actually minted may unpack (blocks console-spawned crates).
            if (!CrateRecordCheck.IsServerMinted(targetItem.Id))
            {
                try { Notify.PickNotifier.Show("This Golden Crate wasn't earned in raid and cannot be unpacked."); }
                catch { /* notifier not ready */ }
                __result = Task.FromResult<IResult>(new FailedResult("Golden Crate cannot be unpacked.", 0));
                return false;
            }
```
Everything else in the patch (reveal FX, placement, `InheritPickMetaBridge.Forward`) stays unchanged.

- [ ] **Step 4: `GoldenPickEarner` — drop the relay broadcast + server mail**

In `GoldenPickClient/Earn/GoldenPickEarner.cs`, edit `EarnGoldenPick` to keep only the local toast; remove the `ServerMail.Announce(...)` line and the entire `if (Plugin.Relay != null) { ... }` block. Result:
```csharp
        public static void EarnGoldenPick(string source)
        {
            Plugin.LogSource?.LogInfo($"[GoldenPick] EarnGoldenPick fired (source: {source})");
            PickNotifier.Show("You just received a Golden Ice Pick!!");
        }
```
Also remove the now-unused `using Manimal.GoldenPick.Net;` if the build flags it as unnecessary (harmless to leave; `ResolveLocalProfileId`/`ResolveLocalNickname` stay — they're used by `GoldKillHandler`).

- [ ] **Step 5: `PickKillBridge` — post to the local server, not the relay**

Replace `GoldenPickClient/Net/PickKillBridge.cs` with:
```csharp
using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Net
{
    // submits a confirmed golden-pick kill to the LOCAL SPT server (/goldenpick/pick/kill).
    // fire-and-forget; a rejection (not owner / unknown pick) is logged and dropped — the kill
    // already happened, only the leaderboard increment is missed.
    internal static class PickKillBridge
    {
        public static void Submit(string pickId, string killerProfileId, string killerNickname)
        {
            if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(killerProfileId) || string.IsNullOrEmpty(killerNickname)) return;
            Task.Run(() =>
            {
                try
                {
                    var body = JsonConvert.SerializeObject(new { pickId, killerProfileId, killerNickname });
                    var resp = RequestHandler.PostJson("/goldenpick/pick/kill", body);
                    Plugin.LogSource?.LogInfo($"[GoldenPick] pick kill recorded ({pickId}): {resp}");
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] pick kill submit failed: {e.Message}");
                }
            });
        }
    }
}
```
(`GoldKillHandler` already calls `PickKillBridge.Submit(pickId, killerProfId, killerNick)` — signature unchanged, so no edit there.)

- [ ] **Step 6: `Plugin.cs` — remove all relay + overlay wiring**

Edit `GoldenPickClient/Plugin.cs`:
1. Delete the constants `RelayKey` and `RelayUrl`.
2. Delete the `internal static RelayClient Relay;` field and its comment.
3. Delete the `RaidCounterOverlayEnabled` config field and, in `Awake`, the `RaidCounterOverlayEnabled = Config.Bind(...)` block plus `gameObject.AddComponent<RaidCounter.RaidCounterOverlay>();`.
4. In `Awake`, delete the `Relay = new RelayClient(BuildRelayUrl()); Relay.Start();` lines.
5. Delete the entire `Update()` method (it only drained relay events).
6. In `OnDestroy`, delete `Relay?.Stop();` (keep `GoldKillHandler.Shutdown();`).
7. Delete the `BuildRelayUrl()` helper.
8. Remove the now-unused `using Manimal.GoldenPick.Net;` and `using Manimal.GoldenPick.Notify;` only if the build flags them (RevealSize/preview-cube config binds and the sheen/unbox patches stay).

Leave everything else in `Awake` (RevealSizePx + preview-cube config, `GoldenCrateUnpackPatch().Enable()`, `GoldKillHandler.Init()`, all `SafeEnable(...)` sheen/red-rebel patches) untouched.

- [ ] **Step 7: Drop BouncyCastle from the client `.csproj`**

In `GoldenPickClient/GoldenPickClient.csproj`:
- Remove the `<PackageReference Include="BouncyCastle.Cryptography" Version="2.4.0" />` line (and its comment).
- Remove the second `<Exec Command="copy ... BouncyCastle.Cryptography.dll ..." />` line in the PostBuild target (and its comment).

- [ ] **Step 8: Build the client green**

Run: `dotnet build GoldenPickClient/GoldenPickClient.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`. If it complains about a leftover `Plugin.Relay` / `EarnEvent` / `ServerMail` / `RaidCounterOverlay` reference, grep for the symbol and remove the straggler:
```bash
grep -rEn "Plugin\.Relay|RelayClient|EarnEvent|ServerMail|RaidCounterOverlay|CounterfeitDetector|RelayUrl|RelayKey" GoldenPickClient/
```
Expected after fixes: empty.

- [ ] **Step 9: Commit**

```bash
git add -A GoldenPickClient/
git commit -m "feat(client): remove relay link, WS drain, signature verify, debug overlay"
```

---

## Part C — Relay removal, build config, deploy, verify

### Task 12: Delete the relay project; relocate leaderboard.html; finalize solution + packaging

**Files:**
- Delete: entire `GoldenPickRelay/` directory
- Modify: `GoldenPick.sln`
- Move: `GoldenPickRelay/leaderboard.html` → `ServerModFiles/leaderboard.html` (before deleting the dir)
- Modify: `GoldenPickServer/GoldenPickServer.csproj` (PostBuild dev dir + packaging BouncyCastle removal)

- [ ] **Step 1: Preserve the leaderboard page, then remove the relay project**

```bash
cd "F:/Code/SPT docker server/mods/ManimalGoldenPick"
git mv GoldenPickRelay/leaderboard.html ServerModFiles/leaderboard.html
git rm -r GoldenPickRelay
```

- [ ] **Step 2: Point `leaderboard.html` at the local route**

Open `ServerModFiles/leaderboard.html`, find where it fetches the relay `/leaderboard` (a `fetch('https://goldenpan-relay-manimal.fly.dev/leaderboard')` or similar). Change the fetch URL to the SPT server route. Since SPT's HTTP transport is not a plain browser GET, use POST with an empty JSON body:
```javascript
const resp = await fetch(`${SERVER}/goldenpick/leaderboard`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: '{}',
});
```
where `SERVER` is `http://192.168.30.11:6969` (SPT's default port — confirm the container's mapped port in Step 6's verify). Keep the existing render code; the JSON shape (`{ ok, picks: [{ ownerNickname, pickNumber, killCount, sheenColorHex, customName, ... }] }`) matches what `GetLeaderboard` returns.

- [ ] **Step 3: Remove the two relay `ProjectConfigurationPlatforms`/`Project(...)` blocks from `GoldenPick.sln`**

`GoldenPick.sln` currently lists only GoldenPickServer + GoldenPickClient (the relay was never added to the sln — confirm with a grep). Run:
```bash
grep -n "Relay" GoldenPick.sln
```
Expected: no matches. If any appear, delete the `Project("...") = "GoldenPickRelay"...EndProject` block and its `{GUID}.*` lines under `ProjectConfigurationPlatforms`.

- [ ] **Step 4: Server `.csproj` — retarget dev deploy + drop BouncyCastle from packaging**

In `GoldenPickServer/GoldenPickServer.csproj`:
- Change `<DevModDir>D:\SPTDev\SPT\user\mods\GoldenPickServer</DevModDir>` to a repo-local staging dir: `<DevModDir>$(ProjectDir)..\dist\SPT\user\mods\GoldenPickServer</DevModDir>` (Dylan has no local server; deploy is via scp in Task 13 from this staging dir / the zip).
- In `PackageModForDistribution`, delete the `<ClientBouncyCastleDll>...</ClientBouncyCastleDll>` property, the `<Error ... BouncyCastle ...>` check, and the `<Copy SourceFiles="$(ClientBouncyCastleDll)" ...>` line. The client no longer ships BouncyCastle.

- [ ] **Step 5: Full solution build + package**

Run:
```bash
dotnet build GoldenPick.sln -c Release
```
Expected: `Build succeeded. 0 Error(s)`, and `Manimal-GoldenPick-1.0.6.zip` is produced at the repo root with layout:
```
BepInEx/plugins/GoldenPick/GoldenPickClient.dll   (no BouncyCastle.Cryptography.dll)
SPT/user/mods/GoldenPickServer/GoldenPickServer.dll + db/... + leaderboard.html
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: delete relay project, relocate leaderboard.html, fix packaging"
```

---

### Task 13: Deploy to the server + local client, and verify the full loop

**Files:** none (deploy + manual verification)

- [ ] **Step 1: Inspect the container layout (confirm paths + port)**

Run:
```bash
ssh spt "ls -d /opt/server/user/mods/ /opt/server/BepInEx/plugins/ 2>/dev/null; echo '--- current goldenpick ---'; ls -la /opt/server/user/mods/GoldenPickServer 2>/dev/null; echo '--- compose port ---'; docker ps --format '{{.Names}} {{.Ports}}' | grep -i fika"
```
Expected: confirms the server-mod dir `/opt/server/user/mods/`, the NarcoNet client-plugin dir `/opt/server/BepInEx/plugins/`, and the mapped SPT port (for `leaderboard.html`). Adjust the paths below if the container differs.

- [ ] **Step 2: Deploy the server mod (DLL + ServerModFiles) to the container**

From the packaged zip's server tree (or the `dist/` staging dir):
```bash
cd "F:/Code/SPT docker server/mods/ManimalGoldenPick"
scp -r dist/SPT/user/mods/GoldenPickServer/* spt:/opt/server/user/mods/GoldenPickServer/
```
(If `dist/` wasn't produced by the retargeted PostBuild, unzip `Manimal-GoldenPick-1.0.6.zip` and scp from `SPT/user/mods/GoldenPickServer/`.) Then restart the server container:
```bash
ssh spt "docker restart spt-fika"
```

- [ ] **Step 3: Verify the server loaded the mod (no relay traffic, stores init)**

Run:
```bash
ssh spt "docker logs --tail 80 spt-fika 2>&1 | grep -iE 'goldenpick|raid-store|error'"
```
Expected: GoldenPick mod loads; PreserveGoldenCrateIdPatch applies; **no** attempts to reach `goldenpan-relay-manimal.fly.dev`; no unhandled exceptions. The `data/` stores are created lazily on first write.

- [ ] **Step 4: Deploy the client DLL — local game client + NarcoNet distribution**

Local (already copied by the Debug PostBuild, but ensure the Release DLL is in place for real play):
```bash
cp "F:/Code/SPT docker server/mods/ManimalGoldenPick/GoldenPickClient/bin/Release/netstandard2.1/GoldenPickClient.dll" "F:/Games/SPT-4.0/BepInEx/plugins/GoldenPick/GoldenPickClient.dll"
```
NarcoNet distribution to players (drop into the server's plugins dir NarcoNet syncs from):
```bash
scp "F:/Code/SPT docker server/mods/ManimalGoldenPick/GoldenPickClient/bin/Release/netstandard2.1/GoldenPickClient.dll" spt:/opt/server/BepInEx/plugins/GoldenPick/GoldenPickClient.dll
```
Headless client (192.168.30.12): **only if GoldenPick is needed there** — it's a cosmetic/gameplay client mod, so the headless host normally doesn't need it. If it does, install manually by copying the same DLL into that machine's `BepInEx/plugins/GoldenPick/`. Skip otherwise.

- [ ] **Step 5: Verify the earn loop in-game (forced award)**

Because the real drop is 0.51%/5th raid, temporarily force a win to exercise the path end-to-end: in `DropOracle.ShouldAward`, temporarily change the roll to `return nextRandom() < 1.0;` (guaranteed on the 5th survived raid), rebuild+redeploy the server DLL, then survive 5 raids (or set the counter file directly). Confirm:
  1. Server log: `AWARD count=5 crate=… pick#=N` and `crate mailed`.
  2. In-game: the Sealed Golden Crate arrives in the messenger; unpacking plays the reveal and yields the pick with "Pick #N" on the tooltip.
  3. Server `data/crate_records.json` and `data/pick_metadata.json` contain the crate + pick.
  **Revert the `< 1.0` change back to `DropOracle.DropProbability` and redeploy** once verified.

- [ ] **Step 6: Verify kills + leaderboard**

Kill a bot/PMC with the golden pick. Confirm:
  1. Corpse gilds gold (unchanged behavior).
  2. Server log: `kill pickId=… newCount=1`.
  3. `pick_metadata.json` shows `KillCount: 1` on that pick.
  4. Open `ServerModFiles/leaderboard.html` (deployed to the mod dir) in a browser pointed at `http://<container-ip>:<port>` and confirm the pick appears with its kill count. If the browser POST is blocked by SPT's transport/CORS, fall back to verifying the JSON directly: `ssh spt "curl -s -XPOST localhost:<port>/goldenpick/leaderboard -d '{}' -H 'Content-Type: application/json'"` — a populated `picks` array is the guaranteed deliverable; the HTML is best-effort.

- [ ] **Step 7: Final commit + push the branch**

```bash
cd "F:/Code/SPT docker server/mods/ManimalGoldenPick"
git add -A && git commit -m "test: verified self-hosted earn + kill + leaderboard loop" --allow-empty
git push -u origin remove-relay
```

---

## Self-review notes

- **Spec coverage:** drop oracle (Tasks 2–3, 7), signing/anti-cheat removed (Tasks 4–6, 9, 11), toasts removed (Task 11), leaderboard kept self-hosted (Tasks 5, 10, 12), admin/redeem dropped (Task 9), kill owner-gate kept without rate-limiting (Task 5), relay project deleted (Task 12), both build environments real (Tasks 1, 12), deploy to container + NarcoNet + local, headless manual/optional (Task 13). All spec sections map to a task.
- **Type consistency:** `CrateRecordStore.CrateRecord(AwardedAt, ProfileId, PickNumber)` is consumed identically in Tasks 6–7; `PickMetadata` gains `KillCount` and loses `Signature` consistently across Tasks 5, 6; `PickKillBridge.Submit(pickId, killerProfileId, killerNickname)` matches `PickKillRequest` fields and `PickMetadataStore.IncrementKill` params.
- **Known soft spot:** the `leaderboard.html` browser fetch against SPT's HTTP transport is best-effort (Task 12 Step 2 / Task 13 Step 6) — the JSON route is the guaranteed deliverable, the HTML has a documented fallback.
