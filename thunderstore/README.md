# FiresDiscordIntegration

**Updated for Valheim 1.0.**

Two-way Discord ↔ Valheim chat, a live server-status panel with restart/stop reactions, automatic per-client login snapshots, death / achievement / skill-up screenshots posted to Discord, crash detection, GIF/image rendering in chat, and an auto-restart wrapper bat — all driven by a single Discord bot of your own.

> Need help, found a bug, want a feature? **https://discord.gg/H9uKGcAujs**

---

## What this mod does

| Feature | What you get |
|---------|--------------|
| **Server Manager panel** | A single live-updating embed in a Discord channel showing player count, names, uptime, last heartbeat. Click the 🔄 reaction to restart the server, ⛔ to stop it (waits for the next 🔄 click before coming back). |
| **Two-way chat relay** | Discord messages appear in-game prefixed with `[Discord] author:` in the author's Discord role color. In-game shouts get posted back to Discord through a webhook with the player's name + avatar. |
| **Inline GIFs and images** | Tenor / Giphy / Klipy share URLs and direct attachment uploads render as actual images (and animated GIFs) inside the in-game chat overlay. |
| **@mention resolution** | A player typing `@admin` in game chat sends a real `@admin` Discord ping by resolving the role name via the bot. `@everyone` and `@here` work as-is. |
| **Login snapshots** | Every player that logs in gets a Discord embed posted: their mod list, error count, warning count, the *actual* mods that produced those errors (no fuzzy-matching false positives), and a client-vs-server mod diff. Admins react to the embed to request the full log / errors-only report / mod list .txt / a session log captured at disconnect. Off by default (`NotifyClientLoginArtifacts`); while it is off, clients send no logs at all. |
| **Server commands from Discord** | `!players`, `!kick`, `!ban` / `!unban` and `!admin add / remove / list`, typed in the relay's chat channel. Off by default, and only allow-listed Discord users or roles can run them. |
| **Valheim's own achievements** | Vanilla achievement unlocks get a screenshot too, the same way FiresRPGmaker achievements do. |
| **Crash detection** | A separate watchdog process spawns alongside the server. If the server crashes or is killed, the watchdog posts a "Server Crash Detected" embed via webhook and updates the status panel. |
| **Auto-restart wrapper** | A ready-to-edit `valheim_server_wrapper.bat` is extracted to your config folder on first run. Configure `SERVER_NAME` / `SERVER_PORT` / `SERVER_WORLD` / `SERVER_PASSWORD` / `SERVER_PUBLIC` once, drop it next to `valheim_server.exe`, and run it instead of the exe — the wrapper handles auto-restart-on-crash and stays alive across Discord-triggered stops so a single 🔄 click brings the server back. |
| **Discord events** | Optional per-event webhook posts: server start, server stop, world save, player join, player leave, player death, player shout, random event, new day, anti-cheat kick / violation, hourly summary, midnight EST daily summary. Each one is a separate config toggle so you only ship the events you want. |
| **Screenshots on death + progress** | The player's client grabs a screenshot on death (captioned with the cause of death), on completing an achievement, and on a skill level-up, and posts it to Discord. ExpandWorldPrefabs can also trigger one with a `screenshot` poke on a player. Every source routes to one webhook (falls back to the general one). Per-source toggles, all on by default; achievements need FiresRPGmaker installed, the EWP poke needs ExpandWorldPrefabs. |

---

## Installation

**Requires [FiresUnifiedCore](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresUnifiedCore/)** — the shared Fires core library. On Thunderstore it installs automatically as a dependency; if you install manually, grab it alongside this mod.

1. Install BepInEx — the [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) on Thunderstore is the standard one.
2. Drop `FiresDiscordIntegration.dll` into `BepInEx/plugins/`. **Which side(s) need it depends on which features you want — see the table below.**
3. Start the server once. It will:
   - Create `BepInEx/config/com.Fire.FiresDiscordIntegration.cfg` with every setting commented for you.
   - Extract `BepInEx/config/FiresDiscordIntegration/valheim_server_wrapper.bat` (the auto-restart wrapper template).
4. Open the server config and fill in `[Discord] Enabled = true` plus the webhook URLs / bot token you set up below.

The DLL auto-detects which side it's running on at `ZNet.Awake` (it calls `IsServer()` and routes itself to `InitServer()` or `InitClient()`). The config file is only read on the side that needs it — clients don't need to configure anything.

### Where the DLL needs to go, per feature

| Feature | Server | Client |
|---------|:------:|:------:|
| Live server-status panel + restart/stop reactions | ✓ | — |
| In-game shouts → Discord (via webhook) | ✓ | — |
| Discord → in-game chat (plain text shows up) | ✓ | — |
| Per-user **Discord role color** on the relayed chat line | ✓ | ✓ |
| **GIF / image rendering** inside the in-game chat overlay | ✓ | ✓ |
| **Login snapshots** (mod list, errors, warnings, attached log) | ✓ | |
| On-demand log / errors / mods requests via Discord reactions | ✓ | ✓ |
| **Screenshots** (death / achievement / skill / EWP poke) | ✓ | ✓ |
| Crash-detection Discord post | ✓ | — |
| Wrapper bat auto-restart + Discord-triggered restart/stop | ✓ | — |

**Without the DLL on the client, the server sees no log data for that player and skips the snapshot entirely (no false-positive embed, no crash).

A player joining a server that has the mod installed but who doesn't have the mod themselves still works — they just don't contribute a snapshot. The rest of the server-side features (status panel, crash notifications, in-game-shout → Discord) work for everyone regardless.

---

## Setting up a Discord bot

If you only want **outgoing** webhooks (events posted to Discord), you don't need a bot at all — just create webhooks on the channels you want to receive each kind of post and paste the URLs into the config.

If you want the **server-manager panel**, the **chat relay back from Discord**, the **per-user role colors**, or the **reaction-based log requests**, you need a bot. Here's the 5-minute setup:

### 1. Create the application

1. Open https://discord.com/developers/applications and click **New Application**.
2. Give it a name (e.g. *My Valheim Server*). Click **Create**.

### 2. Add the bot user

1. In the left sidebar click **Bot**.
2. Set the avatar / display name you want players to see.
3. Under **Privileged Gateway Intents**, enable **Server Members Intent**. This is what lets the bot read each Discord user's role colors so the in-game chat can recolor their messages. (Message Content intent is **not** required — we use REST, not the gateway.)

### 3. Generate the bot token

1. Still on the **Bot** page, click **Reset Token** → **Yes, do it!**.
2. Copy the long string that appears.
3. The token is shown **once**. If you lose it, reset it again.

#### Where to paste the token

The token goes into a dedicated file at your **Valheim install root** (the folder that contains `valheim_server.exe`) — **not** into any BepInEx config:

```
<server folder>/FiresDiscordIntegration_BotToken.cfg
```

If the file doesn't exist yet, start the server once with the mod installed — it auto-creates an empty template there on first load. Open the file, paste your token after `BotToken = `, save, restart.

**Why it's not in `BepInEx/config/`**: Modman and other profile sharing mehods sync configs when shared, sharing a bot token with ANYONE is a bad idea. The dedicated file lives outside that path so the sync tools never touch it.

The wrapper bat reads the same file to drive the Discord-triggered restart polling, so the bat and the mod stay in sync automatically.

Other places the token can come from (first one found wins):

1. The `FIRES_DISCORD_BOT_TOKEN` environment variable. Best on a rented host: nothing on disk at all.
2. `[Discord.BotListener] BotTokenPath`, a file you choose inside the Valheim install (or the BepInEx folder). The config stores the **path**, never the token. A path inside `BepInEx/config` or `BepInEx/plugins` is refused, because those are the folders mod managers export.
3. `FiresDiscordIntegration_BotToken.cfg` at the server root (above).
4. `FiresDiscordIntegration_BotToken.cfg` in the BepInEx folder.

The token is only ever loaded by the dedicated server executable. A client, or any other process, never reads it.

### 4. Invite the bot to your Discord

1. In the left sidebar click **OAuth2** → **URL Generator** (or **Installation** depending on Discord version).
2. Scopes: tick **bot**.
3. Bot permissions: **View Channels**, **Send Messages**, **Read Message History**, **Add Reactions**, **Manage Messages** (lets the mod clean up old status messages on restart).
4. Copy the generated URL, paste it into your browser, pick your server, click **Authorize**.

### 5. Copy the channel + guild IDs you'll use

In Discord, **User Settings → Advanced → Developer Mode** turns on the "Copy ID" right-click option.

Then right-click and **Copy ID** on:
- **Your server** (the guild name in the sidebar) → `[Discord.BotListener] DiscordGuildId`
- **The chat channel** you want the bot to read and relay → `[Discord.BotListener] ChatChannelId`
- **The channel where the status panel + login snapshots should live** → `[Discord.BotListener] StatusChannelId` *(or `ServerManagerChannelId` if you want the panel in a separate channel from snapshots)*

### 6. Create the outgoing webhooks

In each channel that should receive event posts: **Edit Channel → Integrations → Webhooks → New Webhook → Copy URL**. Paste the URL into the matching `[Discord.Webhooks]` setting:
- `GeneralWebhookUrl` — start / stop / save / hourly summary / daily summary
- `ChatWebhookUrl` — relayed in-game chat (so messages show with the player's name + avatar instead of the bot's)
- `AntiCheatWebhookUrl` — kick / violation notifications
- `ClientLogWebhookUrl` — login snapshots + on-demand log artifacts

Webhooks and the bot can post into the same channel — Discord doesn't care.

---

## The wrapper bat (auto-restart + Discord-triggered restart/stop)

On first server load the mod extracts `valheim_server_wrapper.bat` to:

```
<server folder>\BepInEx\config\FiresDiscordIntegration\valheim_server_wrapper.bat
```

That's a **template**. Open it in a text editor and fill in the config block near the top:

```bat
SET SERVER_NAME=My Valheim Server (these don't need "")
SET SERVER_PORT=2456
SET SERVER_WORLD=Dedicated
SET SERVER_PASSWORD=changeme
SET SERVER_PUBLIC=1
SET RESTART_DELAY=10
```

These are the same flags you'd normally pass on the `valheim_server.exe` command line. Password must be 5+ chars and cannot appear inside the server name.

Then **copy the edited bat to your Valheim dedicated-server folder** (the one with `valheim_server.exe` in it). The bat can be renamed to anything — `run_my_server.bat`, `server.bat`, whatever — and you can drop a copy of it into multiple server folders if you run several instances on one machine. Each copy reads sentinel + flag paths *relative to its own location*, so they don't step on each other.

**This feature is mostly for server owners running their own server off of a spare pc using windows who dont have ftp setup or a convenient server console... you can stop and restart the server from your phone on discord if the heartbeat stops and the cmd window is still open**
**It does have its own heartbeat falure code to auto restart, but lets face it... its not working as intended yet (future feature maybe)**

Then run the **bat** instead of `valheim_server.exe`. The bat:
- Auto-restarts the server with a 10-second cooldown if it crashes / is killed.
- Auto-restarts immediately when a Discord admin clicks the 🔄 reaction on the status panel.
- Stops cleanly (and waits for the next 🔄 click) when a Discord admin clicks the ⛔ reaction.
- Falls through to a 15-second Discord polling loop while stopped, so you don't need a separate process to revive the server.

**Do not** add `setlocal enabledelayedexpansion` to this bat.(not like you will edit below the restart delay line) The inline PowerShell for polling discord hates it and it mangles the script. The template ships with delayed expansion **off** for that reason.

---

## Server commands from Discord

Turn on `[Discord.BotListener] EnableServerCommands`, then type these in the relay's chat channel (prefix `CommandPrefix`, default `!`):

```
!players | !who | !online          who is online, with ids
!kick  <name or id>
!ban   <name or id>                !unban <id>
!admin add <steam id> | !admin remove <steam id> | !admin list
!fireshelp                         this list
```

- **Only allow-listed people can run them.** A command runs for a Discord user id in `CommandUserIds` or someone holding a role in `CommandRoleIds`. Both empty = nobody, even with the feature on.
- A command is never relayed into game chat, even a refused one.
- `!admin add` writes Valheim's own admin list (`adminlist.txt`) and sends it to connected players right away. `AdminListPath` is an optional extra copy for hosts whose control panel reads its own file; it must be inside the Valheim install (or the BepInEx folder).
- Commands never replay: a command posted before the server started listening is ignored, so a restart can't kick or ban anyone.

## Valheim's own achievements

`VanillaAchievementScreenshots` (on by default) posts a screenshot when a player unlocks a Valheim achievement.

**Valheim does not unlock achievements on a modded game**, so by default nothing unlocks and nothing posts. That's expected. `AllowAchievementsOnModdedProfile` (off by default) lifts that block. Read this before turning it on:

- Unlocks are **real Steam achievements** on each player's profile, and they stay after the mod is removed.
- Cheats still don't count: a spawned kill, a no-cost craft or a cheated item records nothing.
- The in-game stat totals may look like they reset, because Valheim shows a separate stat counter for achievement-eligible play. The old totals are still saved.

---

## Config map (quick reference)

The `BepInEx/config/com.Fire.FiresDiscordIntegration.cfg` file has every setting documented inline — open it for the full list. Highlights:

| Section | Key | Notes |
|---------|-----|-------|
| `Discord` | `Enabled` | Master switch. |
| `Discord.BotListener` | `DiscordGuildId` | Required for @mention resolution + per-user role colors. |
| `Discord.Webhooks` | `GeneralWebhookUrl` / `ChatWebhookUrl` / `AntiCheatWebhookUrl` / `ClientLogWebhookUrl` | Outgoing webhooks. Empty = that channel disabled. |
| `Discord.Webhooks` | `ScreenshotsWebhookUrl` | Death / achievement / skill / EWP screenshots. Empty falls back to `GeneralWebhookUrl`. |
| `Discord.BotListener` | `EnableBotListener` | Master switch for everything that needs the bot token. |
| *(no key)* | `FiresDiscordIntegration_BotToken.cfg` at server root | Bot token lives in its own file outside `BepInEx/config/` — see step 3 above. |
| `Discord.BotListener` | `BotTokenPath` | Optional: your own token file location (the path only, never the token). |
| `Discord.BotListener` | `EnableServerCommands` / `CommandPrefix` / `CommandUserIds` / `CommandRoleIds` / `AdminListPath` | Server commands from Discord (see above). Off by default. |
| `Discord.BotListener` | `ChatChannelId` | Channel the bot reads Discord chat from. |
| `Discord.BotListener` | `StatusChannelId` | Channel where the live status panel lives. |
| `Discord.ServerManager` | `ServerManagerChannelId` | Optional — separate channel for the panel if you don't want it in `StatusChannelId`. |
| `Discord.Events` | `EnableStatusHeartbeat` | Turn the live panel on/off. |
| `Discord.Events` | `Notify*` | Per-event toggles. Default to sensible "on" values; turn down the noise as you like. |
| `Discord.Events` | `DeathScreenshots` / `AchievementScreenshots` / `SkillLevelUpScreenshots` / `EwpPokeScreenshots` | Screenshot source toggles, all on by default. `SkillLevelUpMinLevel` / `SkillLevelUpStep` throttle skill posts. |
| `Discord.Events` | `VanillaAchievementScreenshots` / `AllowAchievementsOnModdedProfile` | Valheim achievement screenshots (on) and the modded-game unlock override (off). See above. |
| `Discord.ClientLogs` | `NotifyClientLoginArtifacts` / `AttachFullClientLogOnLogin` / `AttachModListOnLogin` / `AttachErrorsWarningsOnLogin` | Control what shows up on each login. |
| `Discord.Appearance` | `WebhookDisplayName` / `WebhookAvatarUrl` | Override the author name / avatar for webhook posts. Empty falls back to the bot's `@me` username + avatar. |

---

## Compatibility

- Designed to run alongside other Valheim mods. Tested with the same heavy-modded environments as FiresGhettoNetworking.
- The chat-relay's TMP rich-text recoloring of `[Discord]`-prefixed lines happens in a Harmony postfix on `Chat.OnNewChatMessage` — should coexist with chat-channel mods as long as they don't strip the buffer entry before our patch runs.
- The wrapper bat reads the bot token from `FiresDiscordIntegration_BotToken.cfg` at the server root, and the channel IDs from this mod's BepInEx config. Other mods' configs are not touched.
- This mods Discord webhooks and Bot Listener are toggled off by default in the cfgs as a cognative test to prevent people from refusing to read. If you wish for them to actually do something you will need to toggle them on and setup your bot/webhooks.

## Credits

The Discord webhook + REST plumbing was originally built inside FiresGhettoNetworking. Split out into this standalone mod so people dont have a mod outside the scope of their networking mod wrapped up in one. 
decided to actually offer it as a stand alone after working on it for the last few months since I've noticed a few sub par versions surface lately that can't pull the log from discord itself and dont have cool features like gifs XD

## License

MIT — see `LICENSE`.

## Issues / questions

https://discord.gg/H9uKGcAujs
