The soundboard you play with a flick: hold one key, flick your mouse toward a sound on a radial wheel, release, and it plays through your mic in game.

## Download

**JriksSoundboard.exe** (below, under *Assets*) is a single file for Windows 10/11 (64-bit): no installer, no .NET install needed.

Windows may show *"Windows protected your PC"* because the app isn't code-signed yet. Click **More info → Run anyway**.

## Setup

1. Install [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (free) and reboot.
2. Run **JriksSoundboard.exe**.
3. **Steam → Settings → Voice → Voice Input Device:** choose *CABLE Output (VB-Audio Virtual Cable)* and turn *Voice Transmission Threshold* off.
4. Add sounds, drag them onto the wheel, then in game hold **Mouse 4**, flick toward a sound and release (while holding your push-to-talk).

## What's new in v0.1.2

- **Full names, always readable** — the overlay shows the full name of the slice you're pointing at above the wheel, and the editor does the same when you hover a slice. Long names (e.g. `what_are_you_talking_about_1_fast_bored`) now shrink and wrap on the slices instead of being cut to a few letters.
- **Smarter auto-cancel** — the timer restarts every time you point at a different slice, so it only runs out once you stop browsing (can be turned off in Settings → Wheel).
- **Cancel key** — press it while holding the wheel key to close the wheel instantly without playing anything. It may be another wheel's key, e.g. Mouse 5 (Settings → Hotkeys).
- **Save / load configuration** — Settings → Backup saves your whole setup (library, wheels, folders, slice positions, hotkeys, settings — and optionally the audio files) as one .zip and loads it back. Your current setup is backed up automatically before every load, and a daily automatic backup keeps the last 10 copies.

## Earlier

- v0.1.1 — app icon (a radial wheel with a music note).

## Features

- Radial sound wheels: 4–16 sounds per wheel, picked by flick direction; works while the game locks your cursor
- Folders inside the wheel (rest on one to open it) — up to 256 sounds on a single key
- As many wheels as you want, each on its own hotkey
- Overlay on top of the game (windowed / fullscreen-windowed) or over the app window, with a time-left ring and playback timer
- Auto-cancel when you hold the wheel without choosing, with an optional cue only you hear
- Library with drag & drop, previews in your headphones, per-sound volume and loudness levelling
- Microphone modes: game push-to-talk, app push-to-talk, voice activation, or off
- No injection, no game hooks, no simulated input — see the README's anti-cheat section

Full guide: [README](https://github.com/jriiik/jriks-soundboard#readme)
