using System;
using System.Collections;
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
        private static bool patched, penSeen;

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

            if (!patched)
            {
                patched = true;
                var drawUpdate = AccessTools.Method(AccessTools.TypeByName("BrushTool"), "DrawUpdate");
                if (drawUpdate != null)
                    new Harmony(PluginInfo.PLUGIN_GUID).Patch(drawUpdate, new HarmonyMethod(typeof(Plugin), nameof(RemapPenPosition)));
                else
                    Logger.LogError("BrushTool.DrawUpdate not found; pen input on the pen display will not be remapped.");
            }

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
        // a point on its own canvas, which is wrong when the pointer is on the pen display; replace it with the pen's
        // position on our copy of the canvas, which has the same size as the original.
        private static void RemapPenPosition(ref Vector2 canvasMousePos)
        {
            if (mirror == null || !mirror.enabled) return;
            var p = PenPosition();
            if (p == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(mirror.rectTransform, p.Value, null, out var local);
            // Local points are relative to each rect's pivot.
            canvasMousePos = local + Vector2.Scale(mirror.rectTransform.pivot - original.rectTransform.pivot, original.rectTransform.rect.size);
        }

        // Where the pointer is on the pen display, in that display's pixels, or null when it is on another display.
        // This is the same logic uGUI's GraphicRaycaster uses for canvases on secondary displays.
        private static Vector2? PenPosition()
        {
            var rel = Display.RelativeMouseAt(Input.mousePosition);
            if (rel == Vector3.zero || (int)rel.z != display) return null;
            var d = Display.displays[display];
            var size = penCanvas.pixelRect.size;
            var p = new Vector2(rel.x / d.systemWidth * size.x, rel.y / d.systemHeight * size.y);
            if (!penSeen)
            {
                penSeen = true;
                Log.LogInfo($"Pointer on display {display}: mousePosition {Input.mousePosition}, relative {rel}, system {d.systemWidth}x{d.systemHeight}, canvas {size} -> {p}");
            }
            return p;
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
    }
}
