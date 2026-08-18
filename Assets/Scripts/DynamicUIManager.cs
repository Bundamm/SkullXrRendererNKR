using Cysharp.Threading.Tasks;
using UnityEngine;
using MixedReality.Toolkit.UX;
using SkullXrRendererNKR.App;

namespace SkullXrRendererNKR.UI
{
    /// <summary>
    /// Adapter suwaków MRTK (menu na dłoni w goglach) na fasadę VolumeSession. Suwaki MRTK operują
    /// zawsze na zakresie 0..1, a docelowe wartości mają własne jednostki (mm, HU, wysokość cięcia) —
    /// mapowanie tych zakresów jest jedynym powodem istnienia tej klasy.
    ///
    /// Wszystko idzie przez VolumeSession, NIE wprost do LoadDicomData: ta sama wartość jest
    /// jednocześnie widoczna w panelu na monitorze, a tylko sesja rozgłasza zmianę do obu warstw.
    /// Zapis wprost sprawiłby, że suwak przesunięty w goglach nie ruszyłby suwaka na monitorze.
    ///
    /// W Inspektorze podpinaj metody z GÓRNEJ sekcji „Dynamic SliderEventData” — wybór z sekcji
    /// „Static Parameters” przekazuje stałą z Inspektora zamiast wartości suwaka.
    /// </summary>
    public class DynamicUIManager : MonoBehaviour
    {
        [Header("Referencje (puste = znajdź w scenie)")]
        public VolumeSession session;

        [Header("Zakresy Wartości (Mapowanie z 0.0 - 1.0)")]
        public Vector2 cutHeightRange = new Vector2(-1f, 1f);
        public Vector2 surfaceThresholdRange = new Vector2(0.01f, 0.99f);

        [Tooltip("Zakres rozmiaru pędzla (mm) dla suwaka Brush Radius — dolna granica pozwala na bardzo precyzyjne cięcia blisko wrażliwych struktur.")]
        public Vector2 brushRadiusRangeMM = new Vector2(1f, 15f);

        private void Start()
        {
            if (session == null) session = VolumeSession.Instance;
            if (session == null) session = FindObjectOfType<VolumeSession>();

            if (session == null)
                Debug.LogWarning("[UI] DynamicUIManager: brak VolumeSession w scenie — menu na dłoni nie będzie miało czym sterować.");
        }

        // --- MAPOWANE SUWAKI (WARTOSCI 0.0 - 1.0) ---

        public void OnCutHeightSliderChanged(float normalizedValue)
        {
            // Wysokość płaszczyzny cięcia jest czysto wizualna i nie przechodzi przez stan sesji —
            // to jedyna wartość ustawiana wprost, bo ma też drugie, równoprawne źródło: uchwyt
            // płaszczyzny, który użytkownik może dowolnie obracać w scenie (patrz SetCutHeight).
            if (session == null || session.dicomData == null) return;
            session.dicomData.SetCutHeight(Mathf.Lerp(cutHeightRange.x, cutHeightRange.y, normalizedValue));
        }

        public void OnSurfaceThresholdSliderChanged(float normalizedValue)
        {
            if (session == null) return;
            session.SurfaceThreshold = Mathf.Lerp(surfaceThresholdRange.x, surfaceThresholdRange.y, normalizedValue);
        }

        public void OnVesselColorLowHueSliderChanged(float normalizedValue)
        {
            if (session == null) return;
            session.VesselHueLow = normalizedValue;
        }

        public void OnVesselColorHighHueSliderChanged(float normalizedValue)
        {
            if (session == null) return;
            session.VesselHueHigh = normalizedValue;
        }

        // Rozmiar pędzla dla Cut/TunnelCut — pozwala zejść do bardzo małych, precyzyjnych wartości
        // blisko wrażliwych struktur (np. tętnic), zamiast na sztywno trzymać się wartości z Inspektora.
        public void OnBrushRadiusSliderChanged(float normalizedValue)
        {
            if (session == null) return;
            session.BrushRadiusMM = Mathf.Lerp(brushRadiusRangeMM.x, brushRadiusRangeMM.y, normalizedValue);
        }

        // --- PRZYCISKI ---

        public void OnResetCutsButtonPressed()
        {
            if (session == null) return;
            session.ResetCutsAsync().Forget();
        }

        // Wydziela AKTUALNIE SPICKOWANĄ wyspę (Picker) jako osobny, niezależnie chwytalny i dalej
        // cięty obiekt zamiast permanentnie ją kasować — „Usuń spickowaną wyspę” jest osobnym
        // przyciskiem obok tego.
        public void OnExtractIslandButtonPressed()
        {
            if (session == null) return;
            session.ExtractPickedIslandAsync().Forget();
        }

        public void OnDeletePickedIslandButtonPressed()
        {
            if (session == null) return;
            session.DeletePickedIslandAsync().Forget();
        }

        public void OnGenerateMaskButtonPressed()
        {
            if (session == null) return;
            session.GenerateMaskAsync().Forget();
        }

        public void OnResetPositionButtonPressed()
        {
            if (session == null) return;
            session.ResetModelPosition();
        }

        // --- OVERLOADY DLA MRTK3 (SliderEventData) ---
        // Pozwalają podpiąć zdarzenie OnValueUpdated bezpośrednio w Inspektorze. Celowo NIE logują —
        // przy przeciąganiu suwaka zdarzenie leci co klatkę, a logowanie każdego zjadało wydajność
        // dokładnie wtedy, gdy trwa interakcja.

        public void OnCutHeightSliderChanged(SliderEventData eventData) => OnCutHeightSliderChanged(eventData.NewValue);
        public void OnSurfaceThresholdSliderChanged(SliderEventData eventData) => OnSurfaceThresholdSliderChanged(eventData.NewValue);
        public void OnVesselColorLowHueSliderChanged(SliderEventData eventData) => OnVesselColorLowHueSliderChanged(eventData.NewValue);
        public void OnVesselColorHighHueSliderChanged(SliderEventData eventData) => OnVesselColorHighHueSliderChanged(eventData.NewValue);
        public void OnBrushRadiusSliderChanged(SliderEventData eventData) => OnBrushRadiusSliderChanged(eventData.NewValue);
    }
}
