using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace SkullXrRendererNKR.App
{
    /// <summary>
    /// Jedno źródło prawdy dla OBU warstw interfejsu — panelu operatora na monitorze i menu na dłoni
    /// w goglach. Obie warstwy działają JEDNOCZEŚNIE (sesja XR jest prowadzona przez Holographic
    /// Remoting z tego samego komputera), więc ta sama wartość jest widoczna i edytowalna w dwóch
    /// miejscach naraz. Gdyby każda warstwa pisała wprost po polach LoadDicomData/VolumePicker,
    /// zmiana zrobiona w jednej byłaby niewidoczna dla drugiej aż do jej ponownego otwarcia.
    ///
    /// Zasady:
    /// • Sesja NIE zawiera własnej logiki wolumetrycznej — wyłącznie deleguje do istniejących
    ///   komponentów i rozgłasza fakt zmiany.
    /// • Każdy setter emituje event TYLKO przy faktycznej zmianie wartości; widoki aktualizują się
    ///   z niego BEZ odsyłania zmiany z powrotem (inaczej suwak w goglach i na monitorze wpadłyby
    ///   w sprzężenie zwrotne).
    /// • Ciężkie operacje idą przez RunExclusiveAsync, które podnosi flagę IsBusy — obie warstwy mają
    ///   z czego wyszarzyć przyciski, zamiast pozwalać wystrzelić segmentację trzy razy pod rząd.
    /// </summary>
    public class VolumeSession : MonoBehaviour
    {
        /// <summary>
        /// Ustawiane w Awake, żeby elementy UI tworzone w runtime (wiersze list, strony menu na dłoni)
        /// nie musiały mieć referencji wstrzykiwanej w Inspektorze.
        /// </summary>
        public static VolumeSession Instance { get; private set; }

        [Header("Referencje (puste = znajdź w scenie)")]
        public LoadDicomData dicomData;
        public Helpers.VolumePicker volumePicker;
        public Helpers.VolumeObjectManager objectManager;

        /// <summary>
        /// Stan wyjściowy okna gęstości: zakres tak szeroki, że nic nie jest wygaszane. To NIE jest
        /// preset — użytkownik nie może go usunąć. Bez niego, po skasowaniu wszystkich własnych
        /// presetów, nie dałoby się wrócić do widoku „pokaż wszystko" inaczej niż ręcznym
        /// rozsuwaniem suwaków.
        /// </summary>
        public const float DefaultWindowCenterHU = 1000f;
        public const float DefaultWindowWidthHU = 6000f;

        [Header("Wartości startowe renderowania")]
        [Tooltip("Środek okna gęstości (HU). Te same wartości domyślne co w shaderze — trzymamy je tutaj, bo shader ich nie oddaje z powrotem, a UI musi startować z prawidłowo ustawionymi suwakami.")]
        public float windowCenterHU = 191f;
        public float windowWidthHU = 353f;
        [Range(0.01f, 0.99f)] public float surfaceThreshold = 0.25f;
        [Range(0f, 1f)] public float vesselHueLow = 0.08f;
        [Range(0f, 1f)] public float vesselHueHigh = 0.14f;

        // --- Eventy dla warstw UI ---
        public event Action<Helpers.ToolMode> OnToolModeChanged;
        public event Action<float> OnBrushRadiusChanged;
        public event Action OnRenderSettingsChanged;
        public event Action OnSegmentationSettingsChanged;
        /// <summary>Lista obiektów (wydzielone kawałki, kosze) się zmieniła.</summary>
        public event Action OnTargetsChanged;
        /// <summary>true = trwa ciężka operacja, UI ma się zablokować.</summary>
        public event Action<bool> OnBusyChanged;
        /// <summary>Krótki komunikat do paska stanu — nazwa trwającej operacji albo jej wynik.</summary>
        public event Action<string> OnStatusChanged;
        /// <summary>Wczytano (albo zwolniono) serię — ścieżka lub null.</summary>
        public event Action<string> OnScanChanged;
        public bool IsBusy { get; private set; }
        public string StatusMessage { get; private set; } = string.Empty;
        public bool IsVolumeReady => dicomData != null && dicomData.IsVolumeReady;
        public string CurrentScanPath => dicomData != null ? dicomData.CurrentSeriesPath : null;
        public System.Collections.Generic.IReadOnlyList<Helpers.VolumeRenderTarget> Targets =>
            objectManager != null ? objectManager.Targets : System.Array.Empty<Helpers.VolumeRenderTarget>();

        private void Awake()
        {
            Instance = this;

            if (dicomData == null) dicomData = FindObjectOfType<LoadDicomData>();
            if (volumePicker == null) volumePicker = FindObjectOfType<Helpers.VolumePicker>();
            if (objectManager == null) objectManager = FindObjectOfType<Helpers.VolumeObjectManager>();

            if (dicomData == null)
                Debug.LogError("[VolumeSession] Brak LoadDicomData w scenie — interfejs nie będzie miał czym sterować.");
        }

        private void OnEnable()
        {
            if (objectManager != null) objectManager.OnTargetsChanged += RaiseTargetsChanged;
            if (dicomData != null)
            {
                dicomData.OnVolumeReady += HandleVolumeReady;
                dicomData.EditHistory.OnChanged += RaiseUndoHistoryChanged;
            }
        }

        private void OnDisable()
        {
            if (objectManager != null) objectManager.OnTargetsChanged -= RaiseTargetsChanged;
            if (dicomData != null)
            {
                dicomData.OnVolumeReady -= HandleVolumeReady;
                dicomData.EditHistory.OnChanged -= RaiseUndoHistoryChanged;
            }
        }

        private void RaiseUndoHistoryChanged() => OnUndoHistoryChanged?.Invoke();

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void RaiseTargetsChanged() => OnTargetsChanged?.Invoke();

        private bool _renderSettingsAdopted;

        private void HandleVolumeReady()
        {
            if (!_renderSettingsAdopted)
            {
                // PIERWSZE wczytanie: to materiał ze sceny ma rację, nie pola tego komponentu.
                // Ustawienia wyglądu były dobierane przez lata pracy nad shaderem i zapisane wprost
                // w materiale; narzucenie im wartości domyślnych sesji po cichu zmieniało obraz
                // (dokładnie tak zgubił się kiedyś próg powierzchni 0.197 na rzecz 0.25).
                AdoptRenderSettingsFromMaterial();
                _renderSettingsAdopted = true;
            }
            else
            {
                // KOLEJNE wczytania: materiał startuje od nowa z wartościami z shadera, więc trzeba
                // dosłać to, co użytkownik ustawił — inaczej przeładowanie serii cofałoby jego pracę.
                PushRenderSettings();
            }

            OnScanChanged?.Invoke(CurrentScanPath);
        }

        /// <summary>
        /// Przejmuje wartości wyglądu z materiału i kolorów naczyń z LoadDicomData, i zapamiętuje je
        /// jako stan „domyślny”, do którego da się wrócić (patrz ResetVesselColors).
        /// </summary>
        private void AdoptRenderSettingsFromMaterial()
        {
            var mat = dicomData != null ? dicomData.InstancedMaterial : null;
            if (mat != null && mat.HasProperty("_SurfaceThreshold"))
                surfaceThreshold = mat.GetFloat("_SurfaceThreshold");

            // Okna CELOWO nie przejmujemy z materiału. Zapisane tam 200/350 nigdy nie działało —
            // renderer je ignorował — więc stanem faktycznym, jaki użytkownik do tej pory widział,
            // jest pełny zakres. Wzięcie tych liczb dosłownie zaczęłoby teraz wygaszać wszystko
            // powyżej 375 HU, czyli kość, i model zmieniłby się sam z siebie przy pierwszym starcie.
            windowCenterHU = DefaultWindowCenterHU;
            windowWidthHU = DefaultWindowWidthHU;
            if (dicomData != null)
            {
                dicomData.SetWindowCenter(windowCenterHU);
                dicomData.SetWindowWidth(windowWidthHU);
            }

            if (dicomData != null)
            {
                // Zapamiętujemy pełne KOLORY, nie same odcienie. Kolory wyjściowe mają własne
                // nasycenie (0.9 dla niższej gęstości), a suwak odcienia zawsze wymusza 1.0 —
                // odtwarzanie stanu wyjściowego z samego odcienia dawało kolor podobny, ale inny,
                // i „przywróć domyślne” nie wracało tam, gdzie użytkownik zaczynał.
                _defaultVesselColorLow = dicomData.vesselColorLow;
                _defaultVesselColorHigh = dicomData.vesselColorHigh;
                _hasDefaultVesselColors = true;

                Color.RGBToHSV(_defaultVesselColorLow, out float lowHue, out _, out _);
                Color.RGBToHSV(_defaultVesselColorHigh, out float highHue, out _, out _);
                vesselHueLow = lowHue;
                vesselHueHigh = highHue;
            }

            OnRenderSettingsChanged?.Invoke();
        }

        private Color _defaultVesselColorLow, _defaultVesselColorHigh;
        private bool _hasDefaultVesselColors;

        /// <summary>
        /// Przywraca barwy naczyń do stanu, w jakim badanie zostało wczytane. Dobieranie odcienia jest
        /// z natury próbowaniem „a jak będzie w tym kolorze” — bez drogi powrotnej każda taka próba
        /// jest jednokierunkowa i trzeba trafiać z pamięci.
        /// </summary>
        public void ResetVesselColors()
        {
            if (!_hasDefaultVesselColors || dicomData == null) return;

            dicomData.SetVesselColors(_defaultVesselColorLow, _defaultVesselColorHigh);

            // Suwaki mają pokazać odcienie przywróconych kolorów, mimo że sam powrót ich nie używał.
            Color.RGBToHSV(_defaultVesselColorLow, out float lowHue, out _, out _);
            Color.RGBToHSV(_defaultVesselColorHigh, out float highHue, out _, out _);
            vesselHueLow = lowHue;
            vesselHueHigh = highHue;

            SetStatus("Przywrócono barwy sprzed zmian.");
            OnRenderSettingsChanged?.Invoke();
        }

        // ------------------------------------------------------------------
        #region Narzędzia

        public Helpers.ToolMode ToolMode
        {
            get => volumePicker != null ? volumePicker.CurrentMode : Helpers.ToolMode.Picker;
            set
            {
                if (volumePicker == null || volumePicker.CurrentMode == value) return;
                volumePicker.CurrentMode = value;
                OnToolModeChanged?.Invoke(value);
            }
        }

        public float BrushRadiusMM
        {
            get => volumePicker != null ? volumePicker.BrushRadiusMM : 0f;
            set
            {
                if (volumePicker == null || Mathf.Approximately(volumePicker.BrushRadiusMM, value)) return;
                volumePicker.BrushRadiusMM = value;
                OnBrushRadiusChanged?.Invoke(value);
            }
        }

        public float CutThresholdHU
        {
            get => volumePicker != null ? volumePicker.CutThresholdHU : 0f;
            set { if (volumePicker != null) volumePicker.CutThresholdHU = value; }
        }

        public float MaxCutDepthMM
        {
            get => volumePicker != null ? volumePicker.MaxCutDepthMM : 0f;
            set { if (volumePicker != null) volumePicker.MaxCutDepthMM = value; }
        }

        #endregion

        // ------------------------------------------------------------------
        #region Renderowanie

        public float WindowCenterHU
        {
            get => windowCenterHU;
            set
            {
                if (Mathf.Approximately(windowCenterHU, value)) return;
                windowCenterHU = value;
                if (dicomData != null) dicomData.SetWindowCenter(value);
                OnRenderSettingsChanged?.Invoke();
            }
        }

        public float WindowWidthHU
        {
            get => windowWidthHU;
            set
            {
                if (Mathf.Approximately(windowWidthHU, value)) return;
                windowWidthHU = value;
                if (dicomData != null) dicomData.SetWindowWidth(value);
                OnRenderSettingsChanged?.Invoke();
            }
        }

        public float SurfaceThreshold
        {
            get => surfaceThreshold;
            set
            {
                if (Mathf.Approximately(surfaceThreshold, value)) return;
                surfaceThreshold = value;
                if (dicomData != null) dicomData.SetSurfaceThreshold(value);
                OnRenderSettingsChanged?.Invoke();
            }
        }

        public float VesselHueLow
        {
            get => vesselHueLow;
            set
            {
                if (Mathf.Approximately(vesselHueLow, value)) return;
                vesselHueLow = value;
                if (dicomData != null) dicomData.SetVesselColorLowHue(value);
                OnRenderSettingsChanged?.Invoke();
            }
        }

        public float VesselHueHigh
        {
            get => vesselHueHigh;
            set
            {
                if (Mathf.Approximately(vesselHueHigh, value)) return;
                vesselHueHigh = value;
                if (dicomData != null) dicomData.SetVesselColorHighHue(value);
                OnRenderSettingsChanged?.Invoke();
            }
        }

        public LoadDicomData.RaymarchQuality RaymarchQuality
        {
            get => dicomData != null ? dicomData.raymarchQuality : LoadDicomData.RaymarchQuality.Auto;
            set
            {
                if (dicomData == null || dicomData.raymarchQuality == value) return;
                dicomData.SetRaymarchQuality(value);
                OnRenderSettingsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Płaszczyzna przekroju — czysto wizualne odcięcie widoku, bez ruszania danych.
        /// Sterowana z obu warstw: suwakiem w panelu i w menu na dłoni, a w goglach dodatkowo
        /// chwytaniem uchwytu. Wszystkie trzy drogi opisują ten sam stan.
        /// </summary>
        public bool ClipPlaneEnabled
        {
            get => dicomData != null && dicomData.ClipPlaneEnabled;
            set
            {
                if (dicomData == null || dicomData.ClipPlaneEnabled == value) return;
                dicomData.ClipPlaneEnabled = value;
                OnRenderSettingsChanged?.Invoke();
            }
        }

        /// <summary>Oś przekroju: 0 = X, 1 = Y, 2 = Z, licząc względem modelu.</summary>
        public int ClipPlaneAxis
        {
            get => dicomData != null ? dicomData.ClipPlaneAxis : 1;
            set
            {
                if (dicomData == null || dicomData.ClipPlaneAxis == value) return;
                dicomData.ClipPlaneAxis = value;
                OnRenderSettingsChanged?.Invoke();
            }
        }

        /// <summary>Położenie płaszczyzny wzdłuż osi, -1..1 względem rozmiaru bryły.</summary>
        public float ClipPlaneOffset
        {
            get => clipPlaneOffset;
            set
            {
                if (Mathf.Approximately(clipPlaneOffset, value)) return;
                clipPlaneOffset = value;
                if (dicomData != null) dicomData.SetCutHeight(value);
                OnRenderSettingsChanged?.Invoke();
            }
        }

        [SerializeField, Range(-1f, 1f)] private float clipPlaneOffset = 0f;

        /// <summary>Nachylenie płaszczyzny względem osi, w stopniach — pozwala ustawić dowolny kąt.</summary>
        public float ClipPlanePitch
        {
            get => dicomData != null ? dicomData.ClipPlanePitch : 0f;
            set
            {
                if (dicomData == null || Mathf.Approximately(dicomData.ClipPlanePitch, value)) return;
                dicomData.ClipPlanePitch = value;
                OnRenderSettingsChanged?.Invoke();
            }
        }

        /// <summary>Obrót płaszczyzny wokół osi, w stopniach.</summary>
        public float ClipPlaneYaw
        {
            get => dicomData != null ? dicomData.ClipPlaneYaw : 0f;
            set
            {
                if (dicomData == null || Mathf.Approximately(dicomData.ClipPlaneYaw, value)) return;
                dicomData.ClipPlaneYaw = value;
                OnRenderSettingsChanged?.Invoke();
            }
        }

        /// <summary>Poziom faktycznie użyty — dla Auto rozwiązany po klasie sprzętu.</summary>
        public LoadDicomData.RaymarchQuality ResolvedRaymarchQuality =>
            dicomData != null ? dicomData.ResolvedRaymarchQuality : LoadDicomData.RaymarchQuality.High;

        public bool EmptySpaceSkipping
        {
            get => dicomData != null && dicomData.enableEmptySkipping;
            set
            {
                if (dicomData == null || dicomData.enableEmptySkipping == value) return;
                dicomData.enableEmptySkipping = value;
                dicomData.RefreshRenderingSettings();
                OnRenderSettingsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Ustawia okno gęstości z gotowej nastawy radiologicznej. Dwie wartości naraz i jedno
        /// powiadomienie — ustawiane osobno przez zwykłe settery dawałyby stan pośredni (nowy środek
        /// przy starej szerokości), który przez chwilę wygląda jak zupełnie inne okno.
        /// </summary>
        #region Presety użytkownika

        /// <summary>
        /// Osobne magazyny dla osobnych grup ustawień: okno i barwy przełącza się w trakcie oglądania,
        /// więc mają własne, szybkie listy. „Zestaw" zapamiętuje komplet — całe stanowisko pracy pod
        /// konkretny typ badania.
        /// </summary>
        public readonly PresetStore WindowPresets = new PresetStore("window");
        public readonly PresetStore VesselPresets = new PresetStore("vessels");
        public readonly PresetStore FullPresets = new PresetStore("full");

        public void CaptureWindowPreset(string name) =>
            WindowPresets.Save(name, windowCenterHU, windowWidthHU);

        /// <summary>
        /// Ustawia okno JEDNYM przebiegiem. Środek i szerokość muszą trafić razem — ustawiane osobno
        /// dają stan pośredni (nowy środek przy starej szerokości), który przez chwilę wygląda jak
        /// zupełnie inne okno.
        /// </summary>
        public void ApplyWindowPreset(Preset preset)
        {
            if (dicomData == null) return;

            windowCenterHU = preset.Get(0, windowCenterHU);
            windowWidthHU = preset.Get(1, windowWidthHU);
            dicomData.SetWindowCenter(windowCenterHU);
            dicomData.SetWindowWidth(windowWidthHU);

            SetStatus($"Okno: {preset.Name} ({windowCenterHU:0} / {windowWidthHU:0} HU)");
            OnRenderSettingsChanged?.Invoke();
        }

        public void CaptureVesselPreset(string name) =>
            VesselPresets.Save(name, vesselHueLow, vesselHueHigh);

        public void ApplyVesselPreset(Preset preset)
        {
            if (dicomData == null) return;

            vesselHueLow = preset.Get(0, vesselHueLow);
            vesselHueHigh = preset.Get(1, vesselHueHigh);
            dicomData.SetVesselColorLowHue(vesselHueLow);
            dicomData.SetVesselColorHighHue(vesselHueHigh);

            SetStatus($"Barwy: {preset.Name}");
            OnRenderSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Zapisuje KOMPLET ustawień. Kolejność wartości jest kontraktem z ApplyFullPreset i musi się
        /// zgadzać w obie strony — dlatego obie metody stoją obok siebie, a odczyt używa wartości
        /// zastępczych, żeby preset zapisany starszą wersją nie wyzerował nowo dodanych pól.
        /// </summary>
        public void CaptureFullPreset(string name) =>
            FullPresets.Save(name,
                windowCenterHU, windowWidthHU, surfaceThreshold,
                vesselHueLow, vesselHueHigh, VisibleMaterialThresholdHU,
                (float)RaymarchQuality, EmptySpaceSkipping ? 1f : 0f,
                MorphThresholdHU, MorphErosionRadius, MorphExpandRadius,
                BrushRadiusMM, CutThresholdHU, MaxCutDepthMM);

        public void ApplyFullPreset(Preset preset)
        {
            if (dicomData == null) return;

            windowCenterHU = preset.Get(0, windowCenterHU);
            windowWidthHU = preset.Get(1, windowWidthHU);
            surfaceThreshold = preset.Get(2, surfaceThreshold);
            vesselHueLow = preset.Get(3, vesselHueLow);
            vesselHueHigh = preset.Get(4, vesselHueHigh);

            dicomData.SetWindowCenter(windowCenterHU);
            dicomData.SetWindowWidth(windowWidthHU);
            dicomData.SetSurfaceThreshold(surfaceThreshold);
            dicomData.SetVesselColorLowHue(vesselHueLow);
            dicomData.SetVesselColorHighHue(vesselHueHigh);

            VisibleMaterialThresholdHU = preset.Get(5, VisibleMaterialThresholdHU);
            RaymarchQuality = (LoadDicomData.RaymarchQuality)Mathf.RoundToInt(preset.Get(6, (float)RaymarchQuality));
            EmptySpaceSkipping = preset.Get(7, EmptySpaceSkipping ? 1f : 0f) > 0.5f;

            MorphThresholdHU = preset.Get(8, MorphThresholdHU);
            MorphErosionRadius = Mathf.RoundToInt(preset.Get(9, MorphErosionRadius));
            MorphExpandRadius = Mathf.RoundToInt(preset.Get(10, MorphExpandRadius));

            BrushRadiusMM = preset.Get(11, BrushRadiusMM);
            CutThresholdHU = preset.Get(12, CutThresholdHU);
            MaxCutDepthMM = preset.Get(13, MaxCutDepthMM);

            SetStatus($"Wczytano zestaw: {preset.Name}. Rozdzielanie struktur wymaga przeliczenia.");
            OnRenderSettingsChanged?.Invoke();
            OnSegmentationSettingsChanged?.Invoke();
        }

        #endregion

        /// <summary>Dosyła komplet ustawień renderowania na materiały — patrz HandleVolumeReady.</summary>
        public void PushRenderSettings()
        {
            if (dicomData == null) return;
            dicomData.SetWindowCenter(windowCenterHU);
            dicomData.SetWindowWidth(windowWidthHU);
            dicomData.SetSurfaceThreshold(surfaceThreshold);
            OnRenderSettingsChanged?.Invoke();
        }

        #endregion

        // ------------------------------------------------------------------
        #region Segmentacja

        public float MorphThresholdHU
        {
            get => dicomData != null ? dicomData.morphThresholdHU : 0f;
            set { if (dicomData != null) { dicomData.morphThresholdHU = value; OnSegmentationSettingsChanged?.Invoke(); } }
        }

        public int MorphErosionRadius
        {
            get => dicomData != null ? dicomData.morphErosionRadius : 0;
            set { if (dicomData != null) { dicomData.morphErosionRadius = value; OnSegmentationSettingsChanged?.Invoke(); } }
        }

        public int MorphExpandRadius
        {
            get => dicomData != null ? dicomData.morphExpandRadius : 0;
            set { if (dicomData != null) { dicomData.morphExpandRadius = value; OnSegmentationSettingsChanged?.Invoke(); } }
        }

        /// <summary>
        /// Najniższa gęstość uznawana za widoczny materiał — steruje progiem barwienia naczyń w shaderze
        /// i tym, w co Picker uzna, że trafił. Ustawienie obrazu, nie segmentacji.
        /// </summary>
        public float VisibleMaterialThresholdHU
        {
            get => dicomData != null ? dicomData.VisibleMaterialThresholdHU : 0f;
            set { if (dicomData != null) { dicomData.VisibleMaterialThresholdHU = value; OnRenderSettingsChanged?.Invoke(); } }
        }


        /// <summary>
        /// Czy poza wybraną wyspą widać też resztę (tkanki miękkie). Wartość jest dosyłana na materiał
        /// co klatkę przez LoadDicomData.UpdateMorphologyMaskID, więc wystarczy zmienić samo pole.
        /// </summary>
        public bool MorphKeepBackground
        {
            get => dicomData != null && dicomData.morphKeepBackground;
            set { if (dicomData != null) { dicomData.morphKeepBackground = value; OnSegmentationSettingsChanged?.Invoke(); } }
        }

        /// <summary>true = ukryj wybraną wyspę zamiast pokazywać tylko ją.</summary>
        public bool MorphNegateMask
        {
            get => dicomData != null && dicomData.morphNegateMask;
            set { if (dicomData != null) { dicomData.morphNegateMask = value; OnSegmentationSettingsChanged?.Invoke(); } }
        }

        /// <summary>Czy Picker ma aktualnie coś wyizolowanego — warunek dla Wydziel/Usuń wyspę.</summary>
        public bool HasPickedIsland => dicomData != null && dicomData.morphPickedVoxel.HasValue;

        #endregion

        // ------------------------------------------------------------------
        #region Akcje

        public UniTask GenerateMaskAsync() =>
            RunExclusiveAsync("Generowanie maski segmentacji…", () => dicomData.GenerateMorphologyMask());

        public UniTask ResetCutsAsync() =>
            RunExclusiveAsync("Cofanie wszystkich cięć…", () => dicomData.ResetCutsAsync());

        public UniTask ExtractPickedIslandAsync() =>
            RunExclusiveAsync("Wydzielanie obiektu…", () => dicomData.ExtractPickedIslandAsObjectAsync());

        public UniTask DeletePickedIslandAsync() =>
            RunExclusiveAsync("Chowanie wskazanej wyspy…", () => dicomData.DeletePickedIslandAsync());

        /// <summary>Czy jest co cofać, i co dokładnie — do podpisu przycisku.</summary>
        public bool CanUndo => dicomData != null && dicomData.EditHistory.CanUndo;
        public string NextUndoLabel => dicomData != null ? dicomData.EditHistory.NextUndoLabel : null;

        /// <summary>Zmieniła się historia cofania (przybył krok, ubył, albo została wyczyszczona).</summary>
        public event Action OnUndoHistoryChanged;

        public UniTask UndoLastEditAsync() =>
            RunExclusiveAsync("Cofanie ostatniej operacji…", async () =>
            {
                string label = await dicomData.UndoLastEditAsync();
                SetStatus(label != null ? $"Cofnięto: {label}." : "Nie ma czego cofnąć.");
            });

        public void ResetModelPosition()
        {
            if (dicomData != null) dicomData.ResetPosition();
        }

        public void SetTargetVisible(Helpers.VolumeRenderTarget target, bool visible)
        {
            if (objectManager != null) objectManager.SetVisible(target, visible);
        }

        public bool IsBinAligned(Helpers.VolumeRenderTarget bin) =>
            objectManager != null && objectManager.IsBinAligned(bin);

        public void SetBinAligned(Helpers.VolumeRenderTarget bin, bool aligned)
        {
            if (objectManager != null) objectManager.SetBinAligned(bin, aligned);
        }

        /// <summary>
        /// Wczytuje serię z podanego folderu. Zwraca true tylko gdy wolumin jest gotowy — ekran
        /// startowy przechodzi do analizy dopiero na tej podstawie, a nie po samym zakończeniu zadania.
        /// </summary>
        public async UniTask<bool> LoadScanAsync(string absolutePath,
                                                 IProgress<LoadDicomData.LoadProgress> progress = null,
                                                 CancellationToken ct = default)
        {
            if (dicomData == null) return false;
            if (IsBusy)
            {
                Debug.LogWarning("[VolumeSession] Trwa inna operacja — wczytywanie pominięte.");
                return false;
            }

            SetBusy(true, "Wczytywanie skanu…");
            try
            {
                bool ok = await dicomData.LoadSeriesAsync(absolutePath, progress, ct);
                OnScanChanged?.Invoke(CurrentScanPath);
                return ok;
            }
            finally
            {
                SetBusy(false, IsVolumeReady ? "Skan wczytany." : "Nie udało się wczytać skanu.");
            }
        }

        /// <summary>Zwalnia bieżącą serię (powrót do ekranu startowego).</summary>
        public void UnloadScan()
        {
            if (dicomData == null) return;
            dicomData.UnloadCurrent();
            OnTargetsChanged?.Invoke();
            OnScanChanged?.Invoke(null);
        }

        #endregion

        // ------------------------------------------------------------------

        /// <summary>
        /// Uruchamia ciężką operację, pilnując że w danej chwili trwa tylko jedna i że obie warstwy UI
        /// wiedzą o blokadzie. Bez tego dwie równoległe segmentacje piszą po tych samych buforach
        /// roboczych VolumeMorphology, a użytkownik nie ma żadnego sygnału, że cokolwiek się liczy.
        /// </summary>
        private async UniTask RunExclusiveAsync(string label, Func<UniTask> operation)
        {
            if (dicomData == null) return;
            if (IsBusy)
            {
                Debug.LogWarning($"[VolumeSession] Pominięto „{label}” — trwa inna operacja ({StatusMessage}).");
                return;
            }

            SetBusy(true, label);
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VolumeSession] Operacja „{label}” zakończyła się błędem: {ex.Message}\n{ex.StackTrace}");
                SetStatus("Operacja zakończona błędem — szczegóły w konsoli.");
            }
            finally
            {
                // Status ustawiony w catch nie może zostać zamazany „gotowe”, więc sprawdzamy, czy
                // wciąż wisi etykieta operacji.
                SetBusy(false, StatusMessage == label ? "Gotowe." : StatusMessage);
            }
        }

        private void SetBusy(bool busy, string status)
        {
            SetStatus(status);
            if (IsBusy == busy) return;
            IsBusy = busy;
            OnBusyChanged?.Invoke(busy);
        }

        private void SetStatus(string status)
        {
            if (StatusMessage == status) return;
            StatusMessage = status;
            OnStatusChanged?.Invoke(status);
        }
    }
}
