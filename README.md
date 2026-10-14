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
The pen position is asked of Windows directly: the cursor position and the game's window on the pen
display, both from the same Windows API in the same DPI context, give the pen's position as a fraction
of that window. Nothing depends on resolutions, aspect ratios, DPI scaling or monitor arrangement.
The game's own brush cursor follows the pen on the pen display.

For strokes that start the instant the pen touches the display, turn off Windows Ink for this game
only. Windows has no per-program switch for it, but the tablet driver does. With a Wacom driver:

1. Start the game, then open Wacom Tablet Properties.
2. In the Application row, press "+", pick Passpartout from the list of running programs and confirm.
   A Passpartout entry appears in the row; make sure it is selected.
3. On the Mapping tab, untick "Use Windows Ink". Settings made with Passpartout selected apply only
   while Passpartout is in front; every other program keeps its own settings.

Other drivers (Huion, XP-Pen, Gaomon) have the same checkbox, some of them per application as well.

If you keep Windows Ink on, Windows holds back a pen touch for a moment to see whether it is a
press-and-hold right-click, which loses the first millimetres of every stroke. The plugin switches
that wait off for the game's windows, which removes most of the effect but not all of it: the pen
still reaches the game through Windows Ink's mouse emulation rather than as a plain mouse.

## How it works

- The canvas on the pen display is a `RawImage` showing the very RenderTexture the game paints into.
- The palette is a copy of the game's tool panel. Clicks, toggles and slider drags on the copy are
  forwarded to the game's real controls, and the copy takes its look from them every frame.
- A Harmony prefix on `BrushTool.DrawUpdate` replaces the brush position with the pen's position
  on the enlarged canvas whenever the pointer is on the pen display, and a prefix on
  `Display.RelativeMouseAt` gives Unity UI the same answer for the palette's buttons and slider.
  Both use `GetCursorPos`, `ScreenToClient` and `GetClientRect` from `user32.dll` against the Unity
  window on the pen display, not Unity's own multi-display bookkeeping.
- The game sets its brush cursor as a software cursor every frame, and Unity draws software cursors
  at the wrong place on a second display. A prefix on `Cursor.SetCursor` draws that same cursor
  texture at the pen instead and hands Unity a blank one while the pen is on the pen display.
- The spray tool records stroke statistics (used for the painting's score) from the raw pointer
  position, which has no meaning on the pen display. A transpiler on `SprayBrushTool.OnDrawUpdate`
  feeds it the main-display pointer position that corresponds to the pen's brush point, so scores
  come out the same whichever display you paint on. The other tools already record from the canvas point.
- Windows Ink's press-and-hold and flick gestures are disabled for both game windows through the
  `MicrosoftTabletPenServiceProperty` window property, the switch Microsoft documents for this
  purpose. This is as far as an application can go; Windows Ink itself can only be turned off in
  the tablet driver.
- With two displays, VSync waits for both monitors' refreshes and stalls periodically (Unity issue
  UUM-40440, closed as a hardware limitation). The plugin turns VSync off and caps the frame rate at
  the main monitor's refresh rate instead. The VSync setting in the game's options menu has no effect
  while the plugin is active.

## Known limitations

- Hover effects and click sounds are not available on the pen display's palette.
- VSync is forced off, so camera pans on the main monitor may show tearing.
- The layout is fixed and made for a landscape pen display (16:9 or 16:10).
- The second display opens at its native resolution and cannot be closed without quitting the game.

## Building

`dotnet build -c Release`. The project references Unity 2019.4 assemblies from NuGet, so no game files
are needed. If `UnityEngine.UI.dll` exists under `GameDir` (default `O:\SteamLibrary\steamapps\common\Passpartout`,
override with `-p:GameDir=...`), it is used instead of the NuGet copy.

## License

CC0 1.0. Do whatever you like with it.
