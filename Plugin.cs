using System;
using System.Collections;
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
        private static Canvas penCanvas;
        private static RawImage original; // the game's painting RawImage (the one with CanvasPainter)
        private static RawImage mirror;   // our copy of it on the pen display
        private static RectTransform ring; // where the game will paint, drawn on the pen display
        private static float brushSize = 1f;
        private static IntPtr mainWindow, penWindow;
        private static bool osInputBroken, loggedPen, loggedOs;

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
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            yield return null;
            mainWindow = FindUnityWindow(IntPtr.Zero); // before the second display exists, the only Unity window is the main one
            if (mainWindow == IntPtr.Zero)
            {
                osInputBroken = true;
                Logger.LogWarning("Could not find the game's main window; falling back to Unity's own multi-display pointer.");
            }
            Display.displays[display].Activate();
            for (int i = 0; i < Display.displays.Length; i++)
            {
                var d = Display.displays[i];
                Logger.LogInfo($"Display {i}: system {d.systemWidth}x{d.systemHeight}, rendering {d.renderingWidth}x{d.renderingHeight}, active {d.active}");
            }

            var cam = new GameObject("PenDisplayCamera").AddComponent<Camera>();
            DontDestroyOnLoad(cam.gameObject);
            cam.targetDisplay = display;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.Patch(AccessTools.Method(typeof(Display), nameof(Display.RelativeMouseAt)),
                new HarmonyMethod(typeof(Plugin), nameof(RelativeMouseAtPrefix)));
            var drawUpdate = AccessTools.Method(AccessTools.TypeByName("BrushTool"), "DrawUpdate");
            if (drawUpdate != null)
                harmony.Patch(drawUpdate, new HarmonyMethod(typeof(Plugin), nameof(RemapPenPosition)));
            else
                Logger.LogError("BrushTool.DrawUpdate not found; pen input on the pen display will not be remapped.");

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
            penCanvas = penRoot.AddComponent<Canvas>();
            penCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            penCanvas.targetDisplay = display;
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

            // Brush ring: shows where the game will paint for the current pen position.
            var ringImage = new GameObject("PenCursor").AddComponent<RawImage>();
            ringImage.transform.SetParent(mirror.transform, false);
            ringImage.raycastTarget = false;
            ringImage.texture = RingTexture();
            ring = ringImage.rectTransform;
            ringImage.enabled = false;
            Event<float>(painter, "brushSize").AddListener(s => brushSize = s);
            brushSize = (float)Event<float>(painter, "brushSize").GetType().GetProperty("Value").GetValue(Event<float>(painter, "brushSize"), null);

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

            var transforms = Pairs<Transform>(toolPanel, clone);
            var images = Pairs<Image>(toolPanel, clone);
            var outlines = Pairs<Outline>(toolPanel, clone);
            var texts = Pairs<Text>(toolPanel, clone);
            var selectables = Pairs<Selectable>(toolPanel, clone);
            penRoot.AddComponent<Sync>().action = () =>
            {
                if (original == null) return;
                bool show = original.gameObject.activeInHierarchy; // only while painting
                mirror.enabled = show;
                mirror.texture = original.texture;
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
            var ringImage = ring == null ? null : ring.GetComponent<RawImage>();
            if (mirror == null || !mirror.enabled) return;
            var n = PenNormalized();
            if (n == null) { if (ringImage) ringImage.enabled = false; return; }

            var corners = new Vector3[4];
            penCanvas.GetComponent<RectTransform>().GetWorldCorners(corners); // 0 = bottom-left, 2 = top-right
            var world = new Vector3(corners[0].x + n.Value.x * (corners[2].x - corners[0].x),
                                    corners[0].y + n.Value.y * (corners[2].y - corners[0].y), corners[0].z);
            Vector2 local = mirror.rectTransform.InverseTransformPoint(world); // relative to the mirror's pivot (its centre)
            var mapped = local + Vector2.Scale(mirror.rectTransform.pivot - original.rectTransform.pivot, original.rectTransform.rect.size);

            ringImage.enabled = true;
            ring.anchoredPosition = local;
            ring.sizeDelta = Vector2.one * (brushSize * 2f + 4f);

            if (!loggedPen)
            {
                loggedPen = true;
                var m = new Vector3[4];
                mirror.rectTransform.GetWorldCorners(m);
                Log.LogInfo($"Pen mapping: normalized {n.Value} -> canvas world {world} (canvas corners {corners[0]}..{corners[2]}, " +
                            $"mirror corners {m[0]}..{m[2]}) -> local {local} -> brush point {mapped} of {original.rectTransform.rect.size}; " +
                            $"game's own point was {canvasMousePos}");
            }
            canvasMousePos = mapped;
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

        // Where the pointer is inside Unity's window on the pen display, as a fraction of that window's client area
        // (x to the right, y upwards, both 0..1), or null when it is not over that window. Asked of Windows directly:
        // the cursor position and the window's client rectangle come from the same API in the same DPI context, so the
        // fraction is right whatever the monitors' resolutions, scaling factors or arrangement.
        private static Vector2? PenNormalized()
        {
            if (osInputBroken) return UnityFallback();
            try
            {
                if (penWindow == IntPtr.Zero || !Win32.IsWindow(penWindow))
                {
                    penWindow = FindUnityWindow(mainWindow);
                    if (penWindow == IntPtr.Zero) return null;
                    Win32.GetClientRect(penWindow, out var r);
                    var d = Display.displays[display];
                    Log.LogInfo($"Pen display window 0x{penWindow.ToInt64():X} (client {r.right}x{r.bottom}, Unity renders {d.renderingWidth}x{d.renderingHeight}); main window 0x{mainWindow.ToInt64():X}.");
                }
                Win32.GetCursorPos(out var p);
                var screen = p;
                Win32.ScreenToClient(penWindow, ref p);
                Win32.GetClientRect(penWindow, out var c);
                if (c.right <= 0 || c.bottom <= 0 || p.x < 0 || p.y < 0 || p.x >= c.right || p.y >= c.bottom) return null;
                var n = new Vector2((p.x + 0.5f) / c.right, 1f - (p.y + 0.5f) / c.bottom);
                if (!loggedOs)
                {
                    loggedOs = true;
                    int rx = 0, ry = 0;
                    var args = new object[] { (int)Input.mousePosition.x, (int)Input.mousePosition.y, rx, ry };
                    int z = (int)AccessTools.Method(typeof(Display), "RelativeMouseAtImpl").Invoke(null, args);
                    Log.LogInfo($"Pointer on pen display: OS cursor ({screen.x},{screen.y}) = client ({p.x},{p.y}) of {c.right}x{c.bottom} -> normalized {n}; " +
                                $"Unity says mousePosition {Input.mousePosition}, RelativeMouseAt ({args[2]},{args[3]}) on display {z}.");
                }
                return n;
            }
            catch (Exception e)
            {
                osInputBroken = true;
                Log.LogError($"Cannot read the pointer from Windows ({e.GetType().Name}: {e.Message}); falling back to Unity's own multi-display pointer.");
                return UnityFallback();
            }
        }

        private static Vector2? UnityFallback()
        {
            int rx = 0, ry = 0;
            var args = new object[] { (int)Input.mousePosition.x, (int)Input.mousePosition.y, rx, ry };
            if ((int)AccessTools.Method(typeof(Display), "RelativeMouseAtImpl").Invoke(null, args) != display) return null;
            var d = Display.displays[display];
            return new Vector2((int)args[2] / (float)d.systemWidth, (int)args[3] / (float)d.systemHeight);
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

        private static Texture2D RingTexture()
        {
            const int n = 64; const float c = (n - 1) / 2f;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float white = Mathf.Clamp01(1.5f - Mathf.Abs(d - 28f));
                    float black = Mathf.Clamp01(1.5f - Mathf.Abs(d - 30f));
                    t.SetPixel(x, y, new Color(white, white, white, Mathf.Max(white, black)));
                }
            t.Apply();
            return t;
        }

        // CanvasPainter exposes ObservableValue<T> properties, which derive from UnityEvent<T>.
        private static UnityEngine.Events.UnityEvent<T> Event<T>(Component c, string property) =>
            (UnityEngine.Events.UnityEvent<T>)c.GetType().GetProperty(property).GetValue(c, null);

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
