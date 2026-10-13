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

            // The game reads Input.mousePosition and maps it onto the main display's canvas in pixels. A pen on
            // the second display reports coordinates relative to that display, so for pen input to land where the
            // user sees it, everything must sit at exactly the same pixel coordinates as on the main display.
            // Both canvases are Screen Space Overlay (world units = pixels) and this one has no CanvasScaler, so
            // copying the originals' world transforms every frame gives a pixel-exact mirror.
            penRoot = new GameObject("PenDisplayCanvas");
            penRoot.SetActive(false);
            DontDestroyOnLoad(penRoot);
            var canvas = penRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = display;

            // Painting: a RawImage showing the same RenderTexture the brushes paint into.
            var mirror = new GameObject("Painting").AddComponent<RawImage>();
            mirror.transform.SetParent(penRoot.transform, false);
            mirror.rectTransform.pivot = raw.rectTransform.pivot;
            mirror.rectTransform.sizeDelta = raw.rectTransform.sizeDelta;
            mirror.texture = raw.texture;
            Event<RenderTexture>(painter, "renderTex").AddListener(t => mirror.texture = t);
            mirror.gameObject.AddComponent<Follow>().source = raw.transform;

            // Palette: a copy of the game's Tool Panel with its game scripts removed, kept in sync by the
            // same events the game uses. Input goes to the game's own panel, which sits at the same place.
            var clone = Instantiate(toolPanel.gameObject, penRoot.transform);
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb.GetType().Assembly != typeof(Graphic).Assembly) DestroyImmediate(mb);
            foreach (var s in clone.GetComponentsInChildren<Selectable>(true)) s.interactable = true;
            clone.AddComponent<Follow>().source = toolPanel;

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

        private class Follow : MonoBehaviour
        {
            public Transform source;

            private void LateUpdate()
            {
                if (source == null) return;
                transform.position = source.position;
                transform.rotation = source.rotation;
                transform.localScale = source.lossyScale;
            }
        }

        // CanvasPainter exposes ObservableValue<T> properties, which derive from UnityEvent<T>.
        private static UnityEvent<T> Event<T>(Component c, string property) =>
            (UnityEvent<T>)c.GetType().GetProperty(property).GetValue(c, null);
    }
}
