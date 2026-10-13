# PasspartoutPenDisplay

A BepInEx 5 plugin for *Passpartout: The Starving Artist* (Steam, Windows) that opens a second
Unity display on a monitor of your choice (meant for a pen display), fills it with black, and
mirrors only the painting canvas and the tool palette onto it. The main monitor is unchanged.

The pen display is visual only. You keep painting on the main monitor; the second display just
shows the canvas large, with the palette beside it.

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

Map your tablet's active area to the **painting canvas region on the main monitor** (in your tablet
driver's mapping or screen-area setting), then look at the pen display while you draw. The canvas on
the pen display is the same texture the game paints into, so strokes appear on both at once. Nothing
is mapped on the pen display itself; it receives no input.

## Known limitations

- The palette on the pen display is a display-only copy of the game's tool panel. It follows the
  selected colour, the brush size and the selected tool, but not hover effects, the "new tool
  unlocked" highlight, nor the greyed-out look of tools that are not unlocked yet.
- The canvas and palette stay on the pen display while you are not painting, showing the last painting.
- Layout is fixed: the canvas fills the pen display's height with the palette to its right, laid out
  for a 16:9 or 16:10 landscape display.
- The second display opens at its native resolution and cannot be closed without quitting the game.

## Building

`dotnet build -c Release`. The project references Unity 2019.4 assemblies from NuGet, so no game files
are needed. If `UnityEngine.UI.dll` exists under `GameDir` (default `O:\SteamLibrary\steamapps\common\Passpartout`,
override with `-p:GameDir=...`), it is used instead of the NuGet copy.

## License

CC0 1.0. Do whatever you like with it.
