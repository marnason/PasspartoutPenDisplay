# PasspartoutPenDisplay

A BepInEx 5 plugin for *Passpartout: The Starving Artist* (Steam, Windows) that opens a second
Unity display on a monitor of your choice (meant for a pen display), fills it with black, and shows
only the painting canvas, enlarged to the display's full height, with the tool palette beside it.
You paint and pick tools directly on the pen display. The main monitor is unchanged.

## Install

1. Install [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases) (`BepInEx_win_x64_5.4.*.zip`)
   into the game folder (the one containing `Passpartout.exe`).
2. Run the game once and quit, so BepInEx creates its folders.
3. Download `PasspartoutPenDisplay.dll` from the [latest release](../../releases/latest) and drop it
   into `BepInEx/plugins/`.
4. If the wrong monitor turns black, edit `PenDisplayIndex` in `BepInEx/config/PasspartoutPenDisplay.cfg`.
   It is an index into Unity's display list (`0` is the game's main display, `1` the next, and so on),
   which may not match the Windows monitor numbering. Restart the game after changing it.

If fewer displays are connected than the index needs, the plugin logs a warning and does nothing.

## Tablet mapping

Map the pen display to itself, the normal full-monitor mapping in your tablet driver, and draw on it.
Pen position is taken from Unity's own multi-display pointer API (the same one Unity UI uses for
buttons on secondary displays), so monitors with different resolutions, aspect ratios and DPI
settings are fine. If strokes still land in the wrong place, send the lines starting with
`Display` and `Pointer on display` from `BepInEx/LogOutput.log`.

## How it works

- The canvas on the pen display is a `RawImage` showing the very RenderTexture the game paints into.
- The palette is a copy of the game's tool panel. Clicks, toggles and slider drags on the copy are
  forwarded to the game's real controls, and the copy takes its look from them every frame.
- One Harmony prefix on `BrushTool.DrawUpdate` replaces the brush position with the pen's position
  on the enlarged canvas whenever the pointer is on the pen display.

## Known limitations

- Hover effects, click sounds and the brush-size scroll wheel are not available on the pen display's palette.
- The spray tool's stroke statistics (used for the painting's score, not for drawing) are computed
  from the raw pointer position and are off for strokes made on the pen display.
- The layout is fixed and made for a landscape pen display (16:9 or 16:10).
- The second display opens at its native resolution and cannot be closed without quitting the game.

## Building

`dotnet build -c Release`. The project references Unity 2019.4 assemblies from NuGet, so no game files
are needed. If `UnityEngine.UI.dll` exists under `GameDir` (default `O:\SteamLibrary\steamapps\common\Passpartout`,
override with `-p:GameDir=...`), it is used instead of the NuGet copy.

## License

CC0 1.0. Do whatever you like with it.
