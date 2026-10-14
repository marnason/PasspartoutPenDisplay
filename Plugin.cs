using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PasspartoutPenDisplay
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private static ManualLogSource Log;
        private static int display;
        private static RectTransform penCanvas;
        private static RawImage original;  // the game's painting RawImage (the one with CanvasPainter)
        private static RawImage mirror;    // our copy of it on the pen display
        private static RawImage penCursor; // the game's brush cursor, drawn by us on the pen display
        private static Texture2D blank;
        private static Vector2? penPoint;  // this frame's brush point on the game's canvas, when the pen is on the pen display
        private static IntPtr mainWindow, penWindow;
        private static bool loggedPen;

        private Component painter;
        private GameObject penRoot;

        private void Awake()
        {
            Log = Logger;
            display = Config.Bind("General", "PenDisplayIndex", 1,
                "Index into UnityEngine.Display.displays of the monitor to use as the pen display.").Value;
            if (Display.displays.Length <= display)
            {
                Logger.LogWarning($"PenDisplayIndex is {display} but only {Display.displays.Length} display(s) were found; doing nothing.");
                return;
            }

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            var brushTool = AccessTools.TypeByName("BrushTool");
            var sprayTool = AccessTools.TypeByName("SprayBrushTool");
            if (brushTool == null || sprayTool == null)
            {
                Logger.LogError("BrushTool or SprayBrushTool not found; doing nothing.");
                return;
            }
            harmony.Patch(AccessTools.Method(brushTool, "DrawUpdate"), new HarmonyMethod(typeof(Plugin), nameof(RemapPenPosition)));
            harmony.Patch(AccessTools.Method(sprayTool, "OnDrawUpdate"), transpiler: new HarmonyMethod(typeof(Plugin), nameof(SprayPointerTranspiler)));
            harmony.Patch(AccessTools.Method(typeof(Cursor), nameof(Cursor.SetCursor), new[] { typeof(Texture2D), typeof(Vector2), typeof(CursorMode) }),
                new HarmonyMethod(typeof(Plugin), nameof(DrawCursorOnPenDisplay)));
            harmony.Patch(AccessTools.Method(typeof(Display), nameof(Display.RelativeMouseAt)),
                new HarmonyMethod(typeof(Plugin), nameof(RelativeMouseAtPrefix)));

            blank = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            blank.SetPixel(0, 0, Color.clear);
            blank.Apply();

            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            yield return null;
            mainWindow = FindUnityWindow(IntPtr.Zero); // before the second display exists, the only Unity window is the main one
            if (mainWindow == IntPtr.Zero) Logger.LogWarning("Could not find the game's main window; pen input on the pen display will not work.");
            Display.displays[display].Activate();
            for (int i = 0; i < Display.displays.Length; i++)
            {
                var d = Display.displays[i];
                Logger.LogInfo($"Display {i}: system {d.systemWidth}x{d.systemHeight}, rendering {d.renderingWidth}x{d.renderingHeight}, active {d.active}");
            }

            // With two displays, VSync waits for both monitors' refreshes and stalls periodically (Unity issue UUM-40440),
            // so cap the frame rate at the main monitor's refresh rate instead.
            QualitySettings.vSyncCount = 0;
            int hz = Screen.currentResolution.refreshRate;
            Application.targetFrameRate = hz > 0 ? hz : -1;

            var cam = new GameObject("PenDisplayCamera").AddComponent<Camera>();
            DontDestroyOnLoad(cam.gameObject);
            cam.targetDisplay = display;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;

            // The painting UI lives in a DontDestroyOnLoad prefab that is loaded asynchronously at startup,
            // so poll until it exists (and again if it ever goes away).
            while (true)
            {
                if (painter == null)
                {
                    painter = FindPainter();
                    if (painter != null)
                    {
                        if (penRoot != null) Destroy(penRoot);
                        Mirror(painter);
                        Logger.LogInfo($"Mirroring painting canvas and tool palette to display {display}.");
                    }
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        private static Component FindPainter()
        {
            foreach (var raw in Resources.FindObjectsOfTypeAll<RawImage>())
            {
                if (!raw.gameObject.scene.IsValid()) continue; // skip the prefab asset itself
                var p = raw.GetComponent("CanvasPainter");
                if (p != null) return p;
            }
            return null;
        }

        private void Mirror(Component painter)
        {
            original = painter.GetComponent<RawImage>();
            var toolPanel = original.transform.parent.parent.Find("Tool Panel"); // PainterHolder/Tool Panel

            penRoot = new GameObject("PenDisplayCanvas");
            penRoot.SetActive(false);
            DontDestroyOnLoad(penRoot);
            var canvas = penRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = display;
            penCanvas = (RectTransform)penRoot.transform;
            var scaler = penRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            penRoot.AddComponent<GraphicRaycaster>(); // the game's EventSystem then delivers pen-display clicks to the palette copy

            // Painting: a RawImage showing the same RenderTexture the brushes paint into, filling the display's height.
            // Its local units are the original canvas's units, so a point on it maps 1:1 onto the original.
            mirror = new GameObject("Painting").AddComponent<RawImage>();
            mirror.transform.SetParent(penRoot.transform, false);
            mirror.raycastTarget = false;
            var rt = mirror.rectTransform;
            rt.sizeDelta = original.rectTransform.rect.size;
            rt.anchoredPosition = new Vector2(-230f, 0f);
            rt.localScale = Vector3.one * (1000f / Mathf.Max(rt.sizeDelta.x, rt.sizeDelta.y));
            Instantiate(original.transform.Find("Rim"), mirror.transform); // the frame around the canvas

            // Palette: a copy of the game's Tool Panel. Input on the copy is forwarded to the original controls, so all
            // game logic runs on the originals; the copy's visual state is taken from the originals every frame.
            var clone = Instantiate(toolPanel.gameObject, penRoot.transform);
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb.GetType().Assembly != typeof(Graphic).Assembly) DestroyImmediate(mb);
            var prt = (RectTransform)clone.transform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = new Vector2(560f, 0f);
            prt.localScale = Vector3.one;

            ForEachPair<Button>(toolPanel, clone, (o, c) =>
            {
                c.onClick = new Button.ButtonClickedEvent();
                c.onClick.AddListener(() => o.onClick.Invoke());
            });
            ForEachPair<Toggle>(toolPanel, clone, (o, c) =>
            {
                c.onValueChanged = new Toggle.ToggleEvent();
                c.onValueChanged.AddListener(on => { if (on) o.isOn = true; });
            });
            ForEachPair<Slider>(toolPanel, clone, (o, c) =>
            {
                c.onValueChanged = new Slider.SliderEvent();
                c.onValueChanged.AddListener(v => o.value = v);
            });
            ForEachPair<EventTrigger>(toolPanel, clone, (o, c) =>
            {
                for (int i = 0; i < c.triggers.Count; i++)
                {
                    var callback = o.triggers[i].callback;
                    c.triggers[i].callback = new EventTrigger.TriggerEvent();
                    c.triggers[i].callback.AddListener(d => callback.Invoke(d));
                }
            });

            // Cursor: drawn last so it is on top of everything.
            penCursor = new GameObject("Cursor").AddComponent<RawImage>();
            penCursor.transform.SetParent(penRoot.transform, false);
            penCursor.raycastTarget = false;
            penCursor.enabled = false;

            var transforms = Pairs<Transform>(toolPanel, clone);
            var images = Pairs<Image>(toolPanel, clone);
            var outlines = Pairs<Outline>(toolPanel, clone);
            var texts = Pairs<Text>(toolPanel, clone);
            var selectables = Pairs<Selectable>(toolPanel, clone);
            penRoot.AddComponent<Sync>().action = () =>
            {
                if (original == null) return;
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0; // the game's options menu can turn it back on
                bool show = original.gameObject.activeInHierarchy; // only while painting
                mirror.enabled = show;
                mirror.texture = original.texture;
                if (mirror.rectTransform.localRotation != original.transform.rotation)
                    mirror.rectTransform.localRotation = original.transform.rotation; // portrait or landscape
                clone.SetActive(show);
                for (int i = 1; i < transforms.o.Length; i++) transforms.c[i].gameObject.SetActive(transforms.o[i].gameObject.activeSelf);
                for (int i = 0; i < images.o.Length; i++)
                {
                    images.c[i].sprite = images.o[i].sprite;
                    images.c[i].color = images.o[i].color;
                    images.c[i].enabled = images.o[i].enabled;
                }
                for (int i = 0; i < outlines.o.Length; i++) outlines.c[i].enabled = outlines.o[i].enabled;
                for (int i = 0; i < texts.o.Length; i++) texts.c[i].text = texts.o[i].text;
                for (int i = 0; i < selectables.o.Length; i++)
                {
                    selectables.c[i].interactable = selectables.o[i].interactable;
                    if (selectables.o[i] is Toggle t) ((Toggle)selectables.c[i]).isOn = t.isOn;
                    if (selectables.o[i] is Slider s) ((Slider)selectables.c[i]).value = s.value;
                }
            };

            penRoot.SetActive(true);
        }

        // Harmony prefix for BrushTool.DrawUpdate(Vector2 canvasMousePos, ...). The game turns Input.mousePosition into
        // a point on its own canvas, which is meaningless when the pointer is on the pen display; replace it with the
        // pen's position on our copy of the canvas, whose local units are the original canvas's units.
        private static void RemapPenPosition(ref Vector2 canvasMousePos)
        {
            penPoint = null;
            if (mirror == null || !mirror.enabled) return;
            var n = PenNormalized();
            if (n == null) return;
            Vector2 local = mirror.rectTransform.InverseTransformPoint(PenWorld(n.Value)); // relative to the mirror's pivot (its centre)
            canvasMousePos = local + Vector2.Scale(mirror.rectTransform.pivot - original.rectTransform.pivot, original.rectTransform.rect.size);
            penPoint = canvasMousePos;
        }

        // Harmony transpiler for SprayBrushTool.OnDrawUpdate, which records stroke statistics (used for the painting's
        // score) from Input.mousePosition rather than from the canvas point. Give it the main-display pointer position
        // that corresponds to the pen's brush point, so the statistics come out the same on either display.
        private static IEnumerable<CodeInstruction> SprayPointerTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var mousePosition = AccessTools.PropertyGetter(typeof(Input), nameof(Input.mousePosition));
            var replacement = AccessTools.Method(typeof(Plugin), nameof(MainDisplayPointer));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(mousePosition)) instruction.operand = replacement;
                yield return instruction;
            }
        }

        private static Vector3 MainDisplayPointer() =>
            penPoint == null ? Input.mousePosition : original.rectTransform.TransformPoint(penPoint.Value);

        // Harmony prefix for Cursor.SetCursor. The game sets its brush cursor as a software cursor every frame while
        // painting, and Unity draws software cursors at the wrong place on a secondary display. When the pen is on the
        // pen display, draw that cursor texture ourselves at the pen and give Unity a blank one. Hardware cursors
        // (CursorMode.Auto) are positioned by Windows and need no help.
        private static void DrawCursorOnPenDisplay(ref Texture2D texture, ref Vector2 hotspot, CursorMode cursorMode)
        {
            if (penCursor == null || original == null) return;
            var n = cursorMode == CursorMode.ForceSoftware && texture != null ? PenNormalized() : null;
            if (n == null)
            {
                penCursor.enabled = false;
                return;
            }
            var rt = penCursor.rectTransform;
            penCursor.texture = texture;
            rt.pivot = new Vector2(hotspot.x / texture.width, 1f - hotspot.y / texture.height); // hotspot is from the top-left
            // One cursor texel is one main-display pixel; show it at the mirror's magnification.
            rt.sizeDelta = new Vector2(texture.width, texture.height) * (mirror.transform.localScale.x / original.canvas.scaleFactor);
            rt.position = PenWorld(n.Value);
            penCursor.enabled = true;
            texture = blank;
            hotspot = Vector2.zero;
        }

        // Harmony prefix for Display.RelativeMouseAt: Unity UI (the palette copy's buttons and slider) asks it where the
        // pointer is on which display. Answer from the OS when the pointer is on the pen display.
        private static bool RelativeMouseAtPrefix(ref Vector3 __result)
        {
            var n = PenNormalized();
            if (n == null) return true;
            var d = Display.displays[display];
            __result = new Vector3(n.Value.x * d.systemWidth, n.Value.y * d.systemHeight, display);
            return false;
        }

        // A point given as a fraction of the pen display, in the pen canvas's world space (its pixels).
        private static Vector3 PenWorld(Vector2 n)
        {
            var r = penCanvas.rect;
            return penCanvas.TransformPoint(r.xMin + n.x * r.width, r.yMin + n.y * r.height, 0f);
        }

        // Where the pointer is inside Unity's window on the pen display, as a fraction of that window's client area
        // (x to the right, y upwards, both 0..1), or null when it is not over that window. Asked of Windows directly:
        // the cursor position and the window's client rectangle come from the same API in the same DPI context, so the
        // fraction is right whatever the monitors' resolutions, scaling factors or arrangement.
        private static Vector2? PenNormalized()
        {
            if (mainWindow == IntPtr.Zero) return null;
            if (penWindow == IntPtr.Zero || !Win32.IsWindow(penWindow))
            {
                penWindow = FindUnityWindow(mainWindow);
                if (penWindow == IntPtr.Zero) return null;
                Log.LogInfo($"Pen display window 0x{penWindow.ToInt64():X}; main window 0x{mainWindow.ToInt64():X}.");
            }
            Win32.GetCursorPos(out var p);
            Win32.ScreenToClient(penWindow, ref p);
            Win32.GetClientRect(penWindow, out var c);
            if (c.right <= 0 || c.bottom <= 0 || p.x < 0 || p.y < 0 || p.x >= c.right || p.y >= c.bottom) return null;
            var n = new Vector2((p.x + 0.5f) / c.right, 1f - (p.y + 0.5f) / c.bottom);
            if (!loggedPen)
            {
                loggedPen = true;
                Log.LogInfo($"Pointer on pen display: client ({p.x},{p.y}) of {c.right}x{c.bottom} -> {n}.");
            }
            return n;
        }

        // A visible top-level Unity player window of this process other than `except`.
        private static IntPtr FindUnityWindow(IntPtr except)
        {
            var found = IntPtr.Zero;
            uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var cls = new StringBuilder(64);
            Win32.EnumWindows((h, _) =>
            {
                Win32.GetWindowThreadProcessId(h, out var wp);
                if (wp != pid || h == except || !Win32.IsWindowVisible(h)) return true;
                cls.Length = 0;
                Win32.GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "UnityWndClass") return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static (T[] o, T[] c) Pairs<T>(Component original, GameObject copy) where T : Component =>
            (original.GetComponentsInChildren<T>(true), copy.GetComponentsInChildren<T>(true));

        private static void ForEachPair<T>(Component original, GameObject copy, Action<T, T> f) where T : Component
        {
            var p = Pairs<T>(original, copy);
            for (int i = 0; i < p.o.Length; i++) f(p.o[i], p.c[i]);
        }

        private class Sync : MonoBehaviour
        {
            public Action action;
            private void LateUpdate() => action();
        }

        private static class Win32
        {
            [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
            [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
            public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
            [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
            [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
            [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);
        }
    }
}
