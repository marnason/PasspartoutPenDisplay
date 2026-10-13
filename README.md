# PasspartoutPenDisplay

A BepInEx 5 plugin for *Passpartout: The Starving Artist* (Steam, Windows) that opens a second
Unity display on a monitor of your choice (meant for a pen display), fills it with black, and
mirrors only the painting canvas and the tool palette onto it. The main monitor is unchanged.

The canvas and palette appear on the pen display at exactly the same screen position as on the main
monitor, so a pen mapped to the pen display paints where you see it.

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

Map your pen display to itself (the normal full-monitor mapping in your tablet driver) and draw on it.
The game reads pen input as pixel coordinates of the display the pen is on, and the plugin places the
canvas and palette at the same pixel coordinates as on the main monitor, so strokes land where the
pen is. For that to line up, the pen display should run at the same resolution as the main monitor.

## Known limitations

- The palette on the pen display is a display-only copy of the game's tool panel. It follows the
  selected colour, the brush size and the selected tool, but not hover effects, the "new tool
  unlocked" highlight, nor the greyed-out look of tools that are not unlocked yet.
- The canvas and palette stay on the pen display while you are not painting, showing the last painting.
- The mirror uses the main monitor's pixel layout; if the pen display has a different resolution, the
  canvas is cut off or surrounded by more black, and pen input may be offset.
- The second display opens at its native resolution and cannot be closed without quitting the game.

## Building

`dotnet build -c Release`. The project references Unity 2019.4 assemblies from NuGet, so no game files
are needed. If `UnityEngine.UI.dll` exists under `GameDir` (default `O:\SteamLibrary\steamapps\common\Passpartout`,
override with `-p:GameDir=...`), it is used instead of the NuGet copy.

## License

CC0 1.0. Do whatever you like with it.
