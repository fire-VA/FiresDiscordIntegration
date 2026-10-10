* v1.0.34 - updates and optimizations for Valheim 1.0
* v1.0.31 - updates and optimizations for Valheim 1.0
  - Random events (raids) now post to Discord from the server (new EventsWebhookUrl option); heartbeat edits retry quietly on Discord errors.

* v1.0.30 - updates and optimizations for Valheim 1.0
  - In-game shout, say and whisper now reach Discord on dedicated servers, with a new ChatRelayIgnorePrefixes option to keep bot commands out.

* v1.0.25 - updated for Valheim 1.0
  - Discord server commands, more screenshot options, and performance fixes.

* v1.0.9 - updated for Valheim 1.0

* v1.0.6
  - Screenshots posted to Discord on death (captioned with the cause of death), on achievements, and on skill level-ups.
  - ExpandWorldPrefabs can trigger a screenshot with a `screenshot` poke on a player.
  - All screenshots route to one webhook (`Discord.Webhooks` / `ScreenshotsWebhookUrl`), falling back to the general webhook. Per-source toggles, all on by default.
* v1.0.0 first release
