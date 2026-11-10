## 1.0.36
- Tidier settings: webhooks and IDs are under Show Advanced and test keys are hidden.
- Chat relay and Discord role names keep working after a relog.
- A player who joins and later hosts in the same session now relays logs.

## 1.0.34
- Minor fixes.

## 1.0.31
- In-game shout, say and whisper now reach Discord on dedicated servers, with a new ChatRelayIgnorePrefixes option to keep bot commands out.
- Random events (raids) now post to Discord from the server (new EventsWebhookUrl option); the status message retries quietly when Discord has errors.

## 1.0.25
- Updated for Valheim 1.0.
- Discord server commands, more screenshot options, and performance fixes.

## 1.0.6
- Screenshots posted to Discord on death (captioned with the cause of death), on achievements, and on skill level-ups.
- Expand World Prefabs can trigger a screenshot with a `screenshot` poke on a player.
- All screenshots go to one webhook (ScreenshotsWebhookUrl), falling back to the general webhook. Each source has its own toggle, all on by default.

## 1.0.0
- First release.
