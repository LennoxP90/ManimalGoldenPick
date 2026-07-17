# Design: Remove the external relay, fold logic into the SPT server mod

**Date:** 2026-07-17
**Fork:** `LennoxP90/ManimalGoldenPick` (upstream `danauraborealis/ManimalGoldenPick`)
**Branch:** `remove-relay`

## Problem

The mod currently depends on an external service, `GoldenPickRelay`, deployed by the
upstream author to Fly.io at `goldenpan-relay-manimal.fly.dev`. That relay is not just a
leaderboard — it is the **sole arbiter of the crate drop**: the survived-raid counter, the
0.51%-every-5th-raid roll, and Ed25519 signing all live there, plus a WebSocket toast hub
and the public leaderboard. Two endpoints are hardcoded against it:

- SPT server mod → `https://goldenpan-relay-manimal.fly.dev` (`GoldenPickServer/RaidProgress/GoldenPickRelayClient.cs`)
- EFT client plugin → `wss://goldenpan-relay-manimal.fly.dev/ws` (`GoldenPickClient/Plugin.cs`)

Running against someone else's server means: no privacy over our group's activity, a hard
dependency on a box we don't control (if it goes down or is retired, the mod's reward loop
dies), and a shared global leaderboard we don't want to be part of.

## Goal

Make the mod fully self-contained inside our own SPT server. No external service, no
WebSocket, no Fly.io, no keypair. This runs on a private FIKA server for a small group of
friends, where the SPT server is already the trusted authority every player connects to.

## Non-goals

- Preserving the shared/global leaderboard or any interoperability with the upstream relay.
- Anti-cheat hardening. This is a trusted friends' server; we accept that a determined
  player who mods their own client/our server DLL could cheat. Not our threat model.
- Backward compatibility with unmodified upstream clients.

## Decisions (locked)

1. **Fold everything into the SPT server mod** — no standalone relay container.
2. **Drop anti-cheat** — no Ed25519 signing, no signature store, no counterfeit audit, no
   "red rebel" confiscation. The SPT server mints and mails crates directly; players cannot
   forge server-mailed items without also modding the server.
3. **Drop live cross-player toasts** — SPT has no native server→client push; the relay's
   WebSocket was that channel. Each player still sees their own local reveal + notification
   on unpack. No "X just got a pick!" broadcast to others.
4. **Keep the leaderboard, self-hosted** — picks + kill counts, served by our SPT server.
5. **Drop admin grants and password-redeem** — only raid-earned crates exist.
6. **Kill-count anti-abuse:** keep the cheap owner-gate (a player may only raise the kill
   count on a pick they own); drop the relay's per-profile cooldowns / rate-limiting.

## Target architecture

```
BEFORE:  EFT client (BepInEx) ──WS──► Fly.io relay ◄──HTTP── SPT server mod
                                        (oracle + signer + leaderboard + toasts)

AFTER:   EFT client (BepInEx) ──HTTP──► SPT server mod ──► local SQLite
                                        (oracle + leaderboard, in-process)
```

All state lives in one SQLite file owned by the SPT server mod. The client makes only local
HTTP calls to its own SPT server (unpack lookup, kill submission, raid-state for the debug
overlay). No outbound internet traffic from either component.

## Server mod (`GoldenPickServer`) changes

### New: `GoldenPickStore` (SQLite) — replaces the relay's `RaidStore`

Owns all the state the relay used to hold. Schema and roll logic port directly from
`GoldenPickRelay/RaidStore.cs` and `GoldenPickRelay/Program.cs` `/raid/end`:

- **Survived-raid counter** per `profileId`.
- **Drop roll** — constants `RaidCycleSize = 5`, `DropProbability = 0.0051`; roll only on a
  fully-survived (non-runthrough) raid, on every 5th such raid.
- **Global "Pick #N" counter** — monotonic, assigned at crate-award time.
- **Pick registry** for the leaderboard: `pickId, ownerProfileId, ownerNickname,
  pickNumber, killCount, awardedAt`.

Exact column set and SQL are finalized against `RaidStore.cs` during implementation; this
store is effectively `RaidStore` minus the signature/blacklist/password columns.

### Changed

- **`SurvivedRaidCrateService`** — `NotifyRelayAndMaybeMail` becomes `RollAndMaybeMail`:
  increment the local counter, roll locally, and on a win mint a fresh `MongoId` crate and
  mail it. The existing `GrantCrate` body is reused verbatim minus the signature-record line.
  The `GoldenPickRelayClient` dependency is removed.
- **`CrateSignatureStore`** — slimmed to a **crate → pickNumber** map (drop the signature
  column). Its `/goldenpick/cratesig` route returns `{ valid, pickNumber }` so unpack knows
  the crate was server-minted and which number to stamp on the resulting pick. (Rename to
  `CrateRecordStore` if it reads more clearly; decided in implementation.)
- **Pick registration** — the work the relay did at `/pick/register-crate-derived` becomes a
  local insert into `GoldenPickStore` at unpack / inherit time (via the existing
  `InheritPickMetaRouter` / `PickMetadataStore` path).
- **New route `/goldenpick/leaderboard`** — JSON, picks ordered by kill count desc.
- **New route `/goldenpick/pick/kill`** — increment kill count; owner-gated (killer must own
  the pick). No rate-limiting.
- **`/goldenpick/raidstate`** — re-pointed at the local counter (was reading via the relay).
- **`leaderboard.html`** — relocated from `GoldenPickRelay/` into `ServerModFiles/` and
  served by the SPT mod (or opened directly), pointed at `/goldenpick/leaderboard`.

### Deleted

- `RaidProgress/GoldenPickRelayClient.cs` — the HTTP client to the relay.
- `RaidProgress/LeaderboardBackfillOnLoad.cs` — its only job was pushing local records up to
  the remote relay; with local state authoritative there is nothing to back-fill.
- `Audit/CounterfeitPickAudit.cs` and `Audit/MailDeliveryAuditPatch.cs` — anti-cheat.
- `Commands/RedeemPickCommand.cs` — password-redeem (dropped feature).
- The admin-grant / update-pick / redeem routers (`GrantPickRouter`, `UpdatePickMetaRouter`,
  and related request DTOs) — dropped features. Retain only what the raid-earn + unpack +
  leaderboard loop needs. Exact router list confirmed during implementation.

## Client mod (`GoldenPickClient`) changes

### Deleted

- `Net/RelayClient.cs` — the WebSocket link.
- `Net/CrateGrantBridge.cs`, `Net/PickGrantBridge.cs`, `Net/PickMetadataUpdateBridge.cs`,
  `Net/EarnEvent.cs`, `Net/ServerMail.cs` — relay-broadcast plumbing (grants now happen
  entirely server-side at raid end; toasts dropped).
- `Unbox/CounterfeitDetector.cs` — anti-cheat.
- Relay wiring in `Plugin.cs`: `RelayUrl`, `RelayKey`, the `Relay` field, `Relay.Start()`,
  `BuildRelayUrl()`, and the entire relay-event drain loop in `Update()`.

### Changed

- **`Unbox/GoldenCrateUnpackPatch.cs`** — drop the signature-verification step. It confirms
  the crate is server-minted via `/goldenpick/cratesig` and plays the reveal; the pickNumber
  comes back in that same response.
- **`Net/PickKillBridge.cs`** — re-pointed from the relay to the local
  `/goldenpick/pick/kill` route.

### Untouched

- All sheen/cosmetic rendering (`GoldenPickSheen/*`), reveal FX, the local `PickNotifier`,
  and the Red Rebel extract-bypass patches. The player's own "you unpacked a pick"
  notification still fires; only the cross-player broadcast is gone.

## Whole `GoldenPickRelay` project

Deleted from the solution (`GoldenPick.sln`) and disk: `Program.cs`, `CrateSigner.cs`,
`RaidStore.cs`, `Dockerfile`, `fly.toml`, `.dockerignore`, `test-button.html`, `.csproj`.
`leaderboard.html` is relocated into `ServerModFiles/` first.

## Data flow after the change

**Raid-earn (the core loop):**
1. `SurvivedRaidCrateService.EndLocalRaid` fires after SPT's normal raid-end processing.
2. `RollAndMaybeMail` increments the local survived counter; on a 5th survived raid it rolls.
3. On a win: mint `MongoId` crate, record `crateId → pickNumber` locally, mail the crate.
4. Player unpacks → client `GoldenCrateUnpackPatch` calls `/goldenpick/cratesig`, gets
   `{ valid: true, pickNumber }`, plays the reveal. The pick is registered in the leaderboard
   store at inherit time with owner + pickNumber.

**Kill tracking:** client detects a golden-pick kill → POSTs to `/goldenpick/pick/kill` →
server increments `killCount` if the killer owns the pick.

**Leaderboard:** `GET /goldenpick/leaderboard` → JSON of picks by kill count → `leaderboard.html`.

## Build & deploy considerations

- Two build targets: the SPT server mod (.NET, references SPT 4.0 server assemblies) and the
  BepInEx client plugin (.NET, references EFT + BepInEx assemblies). Reference paths resolve
  via `Directory.Build.props`; confirm they point at a local SPT install during setup.
- Deploy: server mod DLL into the SPT server's mods dir; client DLL delivered to players via
  the existing NarcoSync path (drop into `BepInEx/plugins`). See related notes on the SPT
  docker server + NarcoSync launcher.
- SQLite DB path: a file under the mod's own directory (mirrors the relay's
  `GOLDENPAN_DB_PATH` default of a local file).

## Testing

- Unit-level: the roll + counter logic (deterministic seam — inject the RNG so a forced roll
  can assert award/no-award at the cycle boundary).
- Integration: a raid-end that survives 5 times triggers a roll; a forced win mails a crate;
  unpack stamps the correct pickNumber; a kill increments only the owner's count; the
  leaderboard route returns the expected ordering.
- Manual: full loop on the live FIKA server with a boosted drop probability to earn a crate
  quickly, unpack, confirm reveal + sheen + leaderboard entry, land a kill, confirm count.

## Risks / notes

- **Signature payload removal must be complete on both sides.** If the client still tries to
  verify a signature the server no longer produces, unpack breaks. The unpack patch and the
  cratesig route must be changed together.
- **Pick metadata propagation** (owner, pickNumber into the pick's tooltip/sheen) runs
  through `InheritPickMetaRouter` / `PickMetadataStore` — these are read carefully during
  implementation to ensure registration into the leaderboard store hooks the right seam.
- **`SurvivedRaidCrateService` DI override** (`typeOverride: LocationLifecycleService`, 26
  passthrough deps) is delicate — the roll change stays inside the existing methods so the DI
  surface is untouched.
