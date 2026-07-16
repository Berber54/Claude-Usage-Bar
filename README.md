# Claude Usage Bar (tray icon)

A real Windows system tray icon — like Discord's — drawn as a small horizontal battery whose orange fill depletes as you use your Claude tokens. Because it's a genuine notification-area icon, Windows manages its position: it snaps in with the other icons and can't be accidentally moved.

## Setup (one time)

1. Double-click `build.bat` — it compiles `ClaudeUsageBar.exe` with the C# compiler built into Windows (no SDK needed) and installs it to `%LOCALAPPDATA%\ClaudeUsageBar`. This source folder can live anywhere; the installed exe doesn't depend on it.
2. Run the exe (build.bat offers to). The battery icon appears in the tray area.
3. **Pin it:** Windows puts new tray icons in the `^` overflow flyout by default. Open the flyout and drag the battery icon onto the taskbar (or Settings → Personalization → Taskbar → Other system tray icons → toggle ClaudeUsageBar on).
4. Right-click the icon → **Start with Windows**. If you later move the exe, run it once from the new spot and the startup entry updates itself.

## Using it

Two tray icons: the battery gauge, and the percentage digits on a Claude-grey badge
with a Claude-orange outline.

- **Hover** — tooltip with remaining % (5-hour window by default).
- **Left-click** — opens a dark flyout dashboard anchored to the tray: live countdown
  to 100%, per-window usage bars (5-hour session, weekly all-models, and weekly
  model-specific limits such as Fable), and last-update/error status. Click
  elsewhere or press Escape to close.
- **Double-click** — toggle the icons between the 5-hour window and the weekly limit.
- **Right-click** — Refresh now / Details / icon toggles / Start with Windows / Exit.
- Fill turns deeper orange below 15%; an orange `!` in the battery means an error (hover for the reason).

## Where the data comes from

It reads your Claude OAuth token from `%USERPROFILE%\.claude\.credentials.json` (written by Claude Code) and queries Anthropic's usage endpoint — the same data behind Claude Code's `/usage`. Refreshes every 5 minutes, backing off automatically if rate-limited.

**If the icon shows `!` with "no credentials":** that file doesn't exist yet. Either install Claude Code (`winget install Anthropic.ClaudeCode`), run `claude` and log in once — or create `%APPDATA%\ClaudeUsageBar\override.txt` containing a number 0–100 (percent remaining) and the icon will display that instead.

## Notes

- The usage endpoint is undocumented and could change; the override file always works as a fallback.
- Settings live in `%APPDATA%\ClaudeUsageBar\config.txt`.
