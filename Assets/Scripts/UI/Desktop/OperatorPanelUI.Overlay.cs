using SkullXrRendererNKR.App;
using TMPro;
using UnityEngine;

namespace SkullXrRendererNKR.UI.Desktop
{
    /// <summary>
    /// Nakładka informacyjna w lewym górnym rogu widoku 3D: płynność obrazu, aktywne narzędzie,
    /// jakość próbkowania, przekrój i stan gogli. To, co dotąd trzeba było sprawdzać po zakładkach
    /// (albo w oknie Stats Edytora, którego w zbudowanej aplikacji nie ma), widać jednym rzutem oka.
    ///
    /// Nakładka nie przyjmuje kliknięć (raycastTarget = false) — leży nad modelem, a VolumePicker
    /// ignoruje mysz nad elementami UI, więc inaczej róg ekranu przestałby reagować na narzędzia.
    /// </summary>
    public partial class OperatorPanelUI
    {
        // Odświeżanie co ułamek sekundy, a nie co klatkę: liczby zmieniające się 60 razy na sekundę
        // są nieczytelne, a przebudowa tekstu TextMeshPro co klatkę to zbędny koszt.
        private const float OverlayRefreshInterval = 0.5f;

        private TextMeshProUGUI _overlayText;
        private float _overlayTimer;
        private int _overlayFrames;

        private void BuildInfoOverlay()
        {
            var box = DesktopUIFactory.CreatePanel(_canvas.transform, "InfoOverlay",
                                                   new Color(0.05f, 0.05f, 0.06f, 0.6f));
            box.raycastTarget = false;
            var boxRect = (RectTransform)box.transform;
            boxRect.anchorMin = new Vector2(0f, 1f);
            boxRect.anchorMax = new Vector2(0f, 1f);
            boxRect.pivot = new Vector2(0f, 1f);
            boxRect.anchoredPosition = new Vector2(12f, -12f);
            boxRect.sizeDelta = new Vector2(300f, 0f);
            DesktopUIFactory.AddVerticalLayout(box.gameObject, 0f, 8);
            var fitter = box.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
            fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;

            _overlayText = DesktopUIFactory.CreateText(box.transform, "", 13f, FontStyles.Normal,
                                                       TextAlignmentOptions.TopLeft);
            _overlayText.raycastTarget = false;
        }

        private void UpdateInfoOverlay()
        {
            if (_overlayText == null) return;

            _overlayFrames++;
            _overlayTimer += Time.unscaledDeltaTime;
            if (_overlayTimer < OverlayRefreshInterval) return;

            float fps = _overlayFrames / _overlayTimer;
            float frameMs = _overlayTimer * 1000f / _overlayFrames;
            _overlayFrames = 0;
            _overlayTimer = 0f;

            // Kolor FPS jako szybka ocena: poniżej 30 obraz w goglach zaczyna wyraźnie rwać.
            string fpsColor = fps >= 55f ? "#7BD88F" : fps >= 30f ? "#E5C07B" : "#E06C75";
            var sb = new System.Text.StringBuilder();
            sb.Append($"<color={fpsColor}><b>{fps:0} FPS</b></color> ({frameMs:0.0} ms)");

            int toolIndex = System.Array.IndexOf(ToolOrder, session.ToolMode);
            string toolName = toolIndex >= 0 ? ToolNames[toolIndex] : session.ToolMode.ToString();
            sb.Append($"\nTool: {toolName}");
            if (session.ToolMode == Helpers.ToolMode.Cut || session.ToolMode == Helpers.ToolMode.RemoveIsland ||
                session.ToolMode == Helpers.ToolMode.TunnelCut)
                sb.Append($", brush {session.BrushRadiusMM:0.0} mm");

            var resolved = session.ResolvedRaymarchQuality;
            sb.Append($"\nSampling: {ResolvedName(resolved)} ({LoadDicomData.SamplesPerModelFor(resolved)} samples)");

            if (session.ClipPlaneEnabled) sb.Append("\nClipping: on");

            if (_remoting != null)
                sb.Append($"\n<color=#9EA5AE>{_remoting.DescribeState(out _)}</color>");

            _overlayText.text = sb.ToString();
        }
    }
}
