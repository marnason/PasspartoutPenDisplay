# PasspartoutPenDisplay

A mod for *Passpartout: The Starving Artist* (Steam, Windows) that puts the painting canvas and the
tool palette on a second monitor, meant for a pen display. The canvas is shown large, the palette
sits beside it, and the rest of the screen is black. You paint and pick colours and tools directly
on the pen display with your pen. The main monitor keeps showing the game as usual.

## Install

1. Download [BepInEx 5.4](https://github.com/BepInEx/BepInEx/releases) (the file named
   `BepInEx_win_x64_5.4.*.zip`) and unpack it into the game folder, the one containing `Passpartout.exe`.
   To find that folder in Steam: right-click the game, Manage, Browse local files.
2. Start the game once and quit it. BepInEx creates its folders on this first run.
3. Download `PasspartoutPenDisplay.dll` from the [latest release](../../releases/latest) and put it
   into the `BepInEx\plugins` folder inside the game folder.
4. Start the game. Your second monitor turns black and shows the canvas as soon as you start painting.

If the wrong monitor turns black, quit the game, open `BepInEx\config\PasspartoutPenDisplay.cfg` in
Notepad and change `PenDisplayIndex` from `1` to `2` (or `3`, and so on), then start the game again.

To remove the mod, delete `PasspartoutPenDisplay.dll` from `BepInEx\plugins`.

## Pen setup

In your tablet driver, map the pen display to itself, the way it normally is. Then draw on it.

For strokes that start the instant the pen touches the display, turn off Windows Ink for this game.
With a Wacom pen display:

1. Start the game, then open Wacom Tablet Properties.
2. In the Application row, press "+", pick Passpartout from the list of running programs and confirm.
   A Passpartout entry appears in the row; make sure it is selected.
3. On the Mapping tab, untick "Use Windows Ink".

This changes the setting for Passpartout only. Other programs keep using Windows Ink as before.
Huion, XP-Pen and Gaomon drivers have the same checkbox; some of them per program as well.
