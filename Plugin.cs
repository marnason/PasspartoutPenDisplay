using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PasspartoutPenDisplay
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<int> penDisplayIndex;
        private Component painter; // the game's CanvasPainter, found by name
        private GameObject penRoot;

        private void Awake()
        {
            penDisplayIndex = Config.Bind("General", "PenDisplayIndex", 1,
                "Index into UnityEngine.Display.displays of the monitor to use as the pen display.");
            if (Display.displays.Length <= penDisplayIndex.Value)
            {
                Logger.LogWarning($"PenDisplayIndex is {penDisplayIndex.Value} but only {Display.displays.Length} display(s) were found; doing nothing.");
                return;
            }
            StartCoroutine(Run(penDisplayIndex.Value));
        }

        private IEnumerator Run(int display)
        {
            yield return null;
            Display.displays[display].Activate();

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
                        Mirror(painter, display);
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

        private void Mirror(Component painter, int display)
        {
            var raw = painter.GetComponent<RawImage>();
            var toolPanel = raw.transform.parent.parent.Find("Tool Panel"); // PainterHolder/Tool Panel

            penRoot = new GameObject("PenDisplayCanvas");
            penRoot.SetActive(false);
            DontDestroyOnLoad(penRoot);
            var canvas = penRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = display;
            var scaler = penRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            // Painting: a RawImage showing the same RenderTexture the brushes paint into.
            var mirror = new GameObject("Painting").AddComponent<RawImage>();
            mirror.transform.SetParent(penRoot.transform, false);
            var rt = mirror.rectTransform;
            rt.sizeDelta = raw.rectTransform.rect.size;
            rt.anchoredPosition = new Vector2(-230f, 0f);
            rt.localScale = Vector3.one * (1000f / Mathf.Max(rt.sizeDelta.x, rt.sizeDelta.y));
            rt.localRotation = raw.transform.rotation;
            mirror.texture = raw.texture;
            Event<RenderTexture>(painter, "renderTex").AddListener(t => mirror.texture = t);
            toolPanel.Find("Flip Button").GetComponent<Button>().onClick
                .AddListener(() => rt.localRotation = raw.transform.rotation);

            // Palette: a copy of the game's Tool Panel with its game scripts removed, kept in sync by the
            // same events the game uses. It is display-only; the pen display has no input.
            var clone = Instantiate(toolPanel.gameObject, penRoot.transform);
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb.GetType().Assembly != typeof(Graphic).Assembly) DestroyImmediate(mb);
            foreach (var s in clone.GetComponentsInChildren<Selectable>(true)) s.interactable = true;
            var prt = (RectTransform)clone.transform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = new Vector2(560f, 0f);
            prt.localScale = Vector3.one;

            var toggles = toolPanel.GetComponentsInChildren<Toggle>(true);
            var cloneToggles = clone.GetComponentsInChildren<Toggle>(true);
            for (int i = 0; i < toggles.Length; i++)
            {
                var c = cloneToggles[i];
                c.onValueChanged = new Toggle.ToggleEvent();
                toggles[i].onValueChanged.AddListener(on => c.isOn = on);
            }
            var cloneSlider = clone.GetComponentInChildren<Slider>(true);
            cloneSlider.onValueChanged = new Slider.SliderEvent();
            toolPanel.GetComponentInChildren<Slider>(true).onValueChanged.AddListener(v => cloneSlider.value = v);
            var swatches = clone.transform.Find("ColorPanel/Palette").GetComponentsInChildren<Image>(true);
            Event<Color>(painter, "color").AddListener(col =>
            {
                foreach (var s in swatches) s.GetComponent<Outline>().enabled = s.color == col;
            });

            penRoot.SetActive(true);
        }

        // CanvasPainter exposes ObservableValue<T> properties, which derive from UnityEvent<T>.
        private static UnityEvent<T> Event<T>(Component c, string property) =>
            (UnityEvent<T>)c.GetType().GetProperty(property).GetValue(c, null);
    }
}
