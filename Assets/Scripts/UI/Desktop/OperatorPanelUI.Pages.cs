using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using SkullXrRendererNKR.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SkullXrRendererNKR.UI.Desktop
{
    /// <summary>
    /// Zawartość zakładek panelu operatora.
    ///
    /// Wszystkie kontrolki są widoczne od razu — nie ma podziału na tryb prosty i zaawansowany.
    /// Powód jest merytoryczny, nie estetyczny: parametry rozdzielania struktur trzeba dobierać
    /// ręcznie do konkretnego badania, bo żadna automatyka ich nie zgadnie (stąd usunięte presety
    /// segmentacji, cięcie progiem gęstości i zamiatanie drobin). Skoro ręczne strojenie jest jedyną
    /// działającą drogą, chowanie go za przełącznikiem sugerowało, że to opcja dla zaawansowanych —
    /// a jest to normalny przebieg pracy.
    /// </summary>
    public partial class OperatorPanelUI
    {
        // Kolejność MUSI odpowiadać kolejności etykiet w kontrolce wyboru narzędzia.
        private static readonly Helpers.ToolMode[] ToolOrder =
        {
            Helpers.ToolMode.Picker,
            Helpers.ToolMode.Cut,
            Helpers.ToolMode.RemoveIsland,
            Helpers.ToolMode.TunnelCut,
            Helpers.ToolMode.Inspect
        };

        private static readonly string[] ToolNames = { "Wskaż", "Tnij", "Gumka", "Tunel", "Diagn." };

        private static readonly string[] ToolHints =
        {
            "Kliknij strukturę na modelu, żeby wyodrębnić ją i zobaczyć w izolacji od reszty.",
            "Przeciągnij po modelu, żeby odciąć materiał. Wycięte trafia do kosza, nie znika.",
            "Przywraca materiał wcześniej schowany do kosza — przeciągnij po miejscu, które ma wrócić.",
            "Jedno kliknięcie przewierca model na wylot, wzdłuż kierunku patrzenia.",
            "Diagnostyka: kliknięcie wypisuje w konsoli gęstość i etykietę trafionego punktu. Nic nie zmienia."
        };

        private static readonly LoadDicomData.RaymarchQuality[] QualityOrder =
        {
            LoadDicomData.RaymarchQuality.Auto,
            LoadDicomData.RaymarchQuality.High,
            LoadDicomData.RaymarchQuality.Medium,
            LoadDicomData.RaymarchQuality.Low
        };

        private TextMeshProUGUI _scanInfoText;

        private TextMeshProUGUI _qualityHint;
        private readonly List<Action> _presetUnsubscribers = new List<Action>();
        private DesktopUIFactory.SliderControl _windowCenter, _windowWidth, _surfaceThreshold;
        private DesktopUIFactory.SliderControl _visibleMaterialThreshold, _hueLow, _hueHigh;
        private DesktopUIFactory.SegmentedControl _qualitySelector, _clipAxisSelector;
        private DesktopUIFactory.SliderControl _clipOffset;
        private Toggle _clipEnabledToggle;
        private Toggle _emptySkipToggle;

        private DesktopUIFactory.SliderControl _morphThreshold, _morphErosion, _morphExpand;
        private Toggle _keepBackgroundToggle, _negateMaskToggle;

        private DesktopUIFactory.SegmentedControl _toolSelector;
        private TextMeshProUGUI _toolHint;
        private DesktopUIFactory.SliderControl _brushRadius, _cutThreshold, _maxCutDepth;

        private RectTransform _objectListRoot;

        private RemotingConnectionManager _remoting;
        private TMP_InputField _remotingIpInput;
        private Button _remotingButton;
        private TextMeshProUGUI _remotingButtonLabel, _remotingStatus;

        // Blokada odsyłania zmian: gdy kontrolkę ustawia KOD (bo wartość zmieniła druga warstwa UI),
        // nie wolno tej zmiany zapisać z powrotem do sesji — patrz opis synchronizacji w VolumeSession.
        private bool _suppressCallbacks;

        // ------------------------------------------------------------------
        #region Badanie

        private RectTransform BuildScanPage(Transform parent)
        {
            var page = CreatePage(parent, "Scan");

            DesktopUIFactory.CreateSectionHeader(page, "Wczytane badanie");

            var infoPanel = DesktopUIFactory.CreateAutoHeightPanel(page, "ScanInfo", DesktopUIFactory.Palette.Panel, 12);
            _scanInfoText = DesktopUIFactory.CreateText(infoPanel.transform, "", 14f);
            _scanInfoText.alignment = TextAlignmentOptions.TopLeft;

            DesktopUIFactory.CreateButton(page, "Wczytaj inne badanie…",
                                          () => appFlow?.ReturnToLauncher(),
                                          DesktopUIFactory.Palette.Accent);

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateButton(page, "Przywróć pozycję modelu",
                                          () => session.ResetModelPosition());

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Zestaw ustawień");
            BuildPresetSection(page, session.FullPresets,
                                "Zapamiętuje KOMPLET ustawień obrazu, rozdzielania struktur i narzędzi — " +
                                "całe stanowisko pracy pod dany typ badania.",
                                p => session.ApplyFullPreset(p),
                                n => session.CaptureFullPreset(n));

            DesktopUIFactory.CreateSeparator(page);
            BuildRemotingSection(page);

            return page;
        }

        /// <summary>
        /// Łączenie z goglami. Sekcja jest widoczna ZAWSZE, także w Edytorze: strumieniowanie obrazu
        /// do gogli jest tu podstawowym trybem pracy, a nie dodatkiem, więc jego stan musi być widać
        /// bez szukania. W Edytorze połączenie zwykle zestawia się jeszcze przed startem sceny, z menu
        /// pakietu Mixed Reality — jest o tym wzmianka, ale przycisk działa w obu przypadkach.
        /// </summary>
        private void BuildRemotingSection(Transform page)
        {
            DesktopUIFactory.CreateSectionHeader(page, "Gogle");

            if (_remoting == null) _remoting = FindObjectOfType<RemotingConnectionManager>();

            if (_remoting == null)
            {
                DesktopUIFactory.CreateParagraph(page,
                    "W scenie nie ma komponentu RemotingConnectionManager — dodaj go, żeby móc stąd " +
                    "zestawić połączenie z goglami.");
                return;
            }

            _remotingIpInput = DesktopUIFactory.CreateInputField(page, "adres IP gogli");
            if (!string.IsNullOrWhiteSpace(_remoting.DefaultIP))
                _remotingIpInput.SetTextWithoutNotify(_remoting.DefaultIP);

            _remotingButton = DesktopUIFactory.CreateButton(page, "Połącz z goglami",
                                                            ToggleRemoting,
                                                            DesktopUIFactory.Palette.Accent, 40f);
            _remotingButtonLabel = _remotingButton.GetComponentInChildren<TextMeshProUGUI>();

            _remotingStatus = DesktopUIFactory.CreateParagraph(page, "");

#if UNITY_EDITOR
            DesktopUIFactory.CreateParagraph(page,
                "W Edytorze połączenie można też zestawić przed uruchomieniem sceny, z menu " +
                "<b>Mixed Reality → Remoting → Holographic Remoting for Play Mode</b>.");
#endif

            _remoting.OnConnectionStateChanged += RefreshRemotingSection;
            RefreshRemotingSection();
        }

        private void ToggleRemoting()
        {
            if (_remoting == null) return;

            _remoting.DescribeState(out bool canConnect);
            if (canConnect) _remoting.StartConnection(_remotingIpInput != null ? _remotingIpInput.text : null);
            else _remoting.StopConnection();

            RefreshRemotingSection();
        }

        private void RefreshRemotingSection()
        {
            if (_remoting == null || _remotingStatus == null) return;

            _remotingStatus.text = _remoting.DescribeState(out bool canConnect);
            _remotingButtonLabel.text = canConnect ? "Połącz z goglami" : "Rozłącz";
            if (_remotingIpInput != null) _remotingIpInput.interactable = canConnect;
        }

        private void HandleScanChanged(string path)
        {
            if (_scanInfoText == null) return;

            if (string.IsNullOrEmpty(path))
            {
                _scanInfoText.text = "<i>Nie wczytano żadnego badania.</i>";
                return;
            }

            var d = session.dicomData;
            string dims = d != null && d.IsVolumeReady
                ? $"{d.Width}×{d.Height}×{d.Depth} wokseli\n" +
                  $"{d.PixelSpacingX:0.###} × {d.PixelSpacingY:0.###} mm, warstwa {d.SliceThickness:0.###} mm"
                : "Trwa przygotowanie danych…";

            _scanInfoText.text = $"<b>{System.IO.Path.GetFileName(path)}</b>\n{dims}\n" +
                                 $"<color=#9EA5AE><size=11>{path}</size></color>";
        }

        #endregion

        // ------------------------------------------------------------------
        #region Obraz

        private RectTransform BuildRenderPage(Transform parent)
        {
            var page = CreatePage(parent, "Render");

            DesktopUIFactory.CreateSectionHeader(page, "Okno gęstości");

            BuildPresetSection(page, session.WindowPresets,
                                "Zapisz bieżące okno pod własną nazwą, żeby wracać do niego jednym kliknięciem.",
                                p => session.ApplyWindowPreset(p),
                                n => session.CaptureWindowPreset(n));

            _windowCenter = DesktopUIFactory.CreateSlider(page, "Środek okna", -1000f, 2000f,
                session.WindowCenterHU, v => Apply(() => session.WindowCenterHU = v), "0", " HU");
            // Górna granica sięga poza skalę Hounsfielda, żeby dało się suwakiem dojść do nastawy
            // pełnego zakresu (6000 HU), a nie tylko wybrać ją z gotowych.
            _windowWidth = DesktopUIFactory.CreateSlider(page, "Szerokość okna", 1f, 6000f,
                session.WindowWidthHU, v => Apply(() => session.WindowWidthHU = v), "0", " HU");

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Kolory naczyń");
            // Nasycenia odpowiadają tym, których LoadDicomData faktycznie używa przy zamianie odcienia
            // na kolor (1.0 dla niskiej gęstości, 0.85 dla wysokiej) — inaczej próbka w panelu
            // kłamałaby względem tego, co widać na modelu.
            _hueLow = DesktopUIFactory.CreateHueSlider(page, "Niższa gęstość",
                session.VesselHueLow, v => Apply(() => session.VesselHueLow = v), 1.0f);
            _hueHigh = DesktopUIFactory.CreateHueSlider(page, "Wyższa gęstość",
                session.VesselHueHigh, v => Apply(() => session.VesselHueHigh = v), 0.85f);

            DesktopUIFactory.CreateButton(page, "Przywróć domyślne barwy",
                                          () => session.ResetVesselColors(),
                                          DesktopUIFactory.Palette.PanelAlt, 28f);

            BuildPresetSection(page, session.VesselPresets,
                                "Własne zestawy barw — przydatne, gdy wracasz do tych samych badań.",
                                p => session.ApplyVesselPreset(p),
                                n => session.CaptureVesselPreset(n));

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Płaszczyzna przekroju");
            DesktopUIFactory.CreateParagraph(page,
                "Odcina widok, nie dane — nic nie trafia do kosza i nie trzeba tego cofać. " +
                "W goglach płaszczyznę można też chwycić i obrócić dowolnie.");

            _clipEnabledToggle = DesktopUIFactory.CreateToggle(page, "Włącz przekrój",
                session.ClipPlaneEnabled, v => Apply(() => session.ClipPlaneEnabled = v));

            _clipAxisSelector = DesktopUIFactory.CreateSegmented(page, "Oś cięcia",
                new[] { "Lewo–prawo", "Góra–dół", "Przód–tył" },
                session.ClipPlaneAxis,
                i => Apply(() => session.ClipPlaneAxis = i));

            _clipOffset = DesktopUIFactory.CreateSlider(page, "Położenie", -1f, 1f,
                session.ClipPlaneOffset, v => Apply(() => session.ClipPlaneOffset = v), "0.00");

            DesktopUIFactory.CreateButton(page, "Wyśrodkuj przekrój",
                                          () => Apply(() => session.ClipPlaneOffset = 0f),
                                          DesktopUIFactory.Palette.PanelAlt, 28f);

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Renderowanie");

            _surfaceThreshold = DesktopUIFactory.CreateSlider(page, "Próg trafienia powierzchni",
                0.01f, 0.99f, session.SurfaceThreshold, v => Apply(() => session.SurfaceThreshold = v), "0.00");

            _visibleMaterialThreshold = DesktopUIFactory.CreateSlider(page,
                "Próg widocznego materiału", -500f, 500f, session.VisibleMaterialThresholdHU,
                v => Apply(() => session.VisibleMaterialThresholdHU = v), "0", " HU");

            // Podpisy niosą liczbę próbek na przekrój modelu — samo „Wysoka/Średnia/Niska" nie mówi,
            // czym te poziomy się różnią, a to jest jedyna rzecz, która przekłada się na obraz i FPS.
            _qualitySelector = DesktopUIFactory.CreateSegmented(page, "Gęstość próbkowania",
                new[]
                {
                    "Auto",
                    "Wysoka\n" + LoadDicomData.SamplesPerModelFor(LoadDicomData.RaymarchQuality.High),
                    "Średnia\n" + LoadDicomData.SamplesPerModelFor(LoadDicomData.RaymarchQuality.Medium),
                    "Niska\n" + LoadDicomData.SamplesPerModelFor(LoadDicomData.RaymarchQuality.Low)
                },
                System.Array.IndexOf(QualityOrder, session.RaymarchQuality),
                i => Apply(() => session.RaymarchQuality = QualityOrder[i]));

            _qualityHint = DesktopUIFactory.CreateParagraph(page, "");

            _emptySkipToggle = DesktopUIFactory.CreateToggle(page, "Przeskakiwanie pustki",
                session.EmptySpaceSkipping, v => Apply(() => session.EmptySpaceSkipping = v));

            DesktopUIFactory.CreateParagraph(page,
                "Przeskakiwanie pustki wyłącza się tylko diagnostycznie: jeśli dziury albo paski " +
                "w modelu znikają po wyłączeniu, przyczyna jest w mapie zajętości.");

            return page;
        }

        private void RefreshRenderControls()
        {
            if (_windowCenter == null) return;

            _suppressCallbacks = true;
            _windowCenter.SetValueWithoutNotify(session.WindowCenterHU);
            _windowWidth.SetValueWithoutNotify(session.WindowWidthHU);
            _surfaceThreshold.SetValueWithoutNotify(session.SurfaceThreshold);
            _visibleMaterialThreshold.SetValueWithoutNotify(session.VisibleMaterialThresholdHU);
            _hueLow.SetValueWithoutNotify(session.VesselHueLow);
            _hueHigh.SetValueWithoutNotify(session.VesselHueHigh);
            _qualitySelector?.SetIndexWithoutNotify(System.Array.IndexOf(QualityOrder, session.RaymarchQuality));
            if (_emptySkipToggle != null) _emptySkipToggle.SetIsOnWithoutNotify(session.EmptySpaceSkipping);
            if (_clipEnabledToggle != null) _clipEnabledToggle.SetIsOnWithoutNotify(session.ClipPlaneEnabled);
            _clipAxisSelector?.SetIndexWithoutNotify(session.ClipPlaneAxis);
            _clipOffset?.SetValueWithoutNotify(session.ClipPlaneOffset);
            _suppressCallbacks = false;

            RefreshQualityHint();
        }

        #endregion

        // ------------------------------------------------------------------
        #region Struktury

        private RectTransform BuildSegmentationPage(Transform parent)
        {
            var page = CreatePage(parent, "Segmentation");

            DesktopUIFactory.CreateSectionHeader(page, "Rozdzielanie struktur");
            DesktopUIFactory.CreateParagraph(page,
                "Wartości dobiera się do konkretnego badania — nie ma nastaw poprawnych dla każdego " +
                "skanu. Po każdej zmianie kliknij Przelicz.");

            _morphThreshold = DesktopUIFactory.CreateSlider(page, "Próg rozpoznawania", -200f, 1500f,
                session.MorphThresholdHU, v => Apply(() => session.MorphThresholdHU = v), "0", " HU");
            _morphErosion = DesktopUIFactory.CreateSlider(page, "Promień rozdzielania", 0f, 10f,
                session.MorphErosionRadius, v => Apply(() => session.MorphErosionRadius = (int)v),
                "0", " wok.", wholeNumbers: true);
            _morphExpand = DesktopUIFactory.CreateSlider(page, "Promień domalowania obrzeża", 0f, 10f,
                session.MorphExpandRadius, v => Apply(() => session.MorphExpandRadius = (int)v),
                "0", " wok.", wholeNumbers: true);

            DesktopUIFactory.CreateButton(page, "Przelicz",
                                          () => session.GenerateMaskAsync().Forget(),
                                          DesktopUIFactory.Palette.Accent, 40f);

            var statsPanel = DesktopUIFactory.CreateAutoHeightPanel(page, "Stats", DesktopUIFactory.Palette.Panel);
            var stats = DesktopUIFactory.CreateText(statsPanel.transform, "<i>Struktury jeszcze nie policzone.</i>",
                                                    12f, FontStyles.Normal, TextAlignmentOptions.TopLeft,
                                                    DesktopUIFactory.Palette.TextDim);

            // LoadDicomData wpisuje podsumowanie segmentacji wprost w przypisany TextMeshPro. Dotąd
            // pole trzeba było podpiąć ręcznie w Inspektorze; panel dostarcza je sam, ale nie odbiera
            // już przypisanego — gdyby ktoś celowo wskazał inne miejsce w scenie, ma ono pierwszeństwo.
            if (session.dicomData != null && session.dicomData.morphologyStatsText == null)
                session.dicomData.morphologyStatsText = stats;

            _keepBackgroundToggle = DesktopUIFactory.CreateToggle(page, "Pokaż też tkanki miękkie",
                session.MorphKeepBackground, v => Apply(() => session.MorphKeepBackground = v));
            _negateMaskToggle = DesktopUIFactory.CreateToggle(page, "Odwróć: ukryj wskazaną strukturę",
                session.MorphNegateMask, v => Apply(() => session.MorphNegateMask = v));

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateButton(page, "Cofnij wszystkie cięcia",
                                          () => ConfirmThen("Cofnąć wszystkie cięcia?",
                                                            "Model wróci do stanu sprzed pierwszego cięcia. " +
                                                            "Wydzielone obiekty i kosze zostaną usunięte.",
                                                            () => session.ResetCutsAsync().Forget()),
                                          DesktopUIFactory.Palette.AccentDanger, 42f);

            return page;
        }

        private void RefreshSegmentationControls()
        {
            if (_morphThreshold == null) return;

            _suppressCallbacks = true;
            _morphThreshold.SetValueWithoutNotify(session.MorphThresholdHU);
            _morphErosion.SetValueWithoutNotify(session.MorphErosionRadius);
            _morphExpand.SetValueWithoutNotify(session.MorphExpandRadius);
            if (_keepBackgroundToggle != null) _keepBackgroundToggle.SetIsOnWithoutNotify(session.MorphKeepBackground);
            if (_negateMaskToggle != null) _negateMaskToggle.SetIsOnWithoutNotify(session.MorphNegateMask);
            _suppressCallbacks = false;
        }

        #endregion

        // ------------------------------------------------------------------
        #region Narzędzia

        private RectTransform BuildToolsPage(Transform parent)
        {
            var page = CreatePage(parent, "Tools");

            DesktopUIFactory.CreateSectionHeader(page, "Aktywne narzędzie");
            _toolSelector = DesktopUIFactory.CreateSegmented(page, null, ToolNames,
                System.Array.IndexOf(ToolOrder, session.ToolMode),
                i => Apply(() => session.ToolMode = ToolOrder[i]));

            _toolHint = DesktopUIFactory.CreateParagraph(page, "", 13f);

            DesktopUIFactory.CreateSeparator(page);

            _brushRadius = DesktopUIFactory.CreateSlider(page, "Rozmiar pędzla", 0.5f, 25f,
                session.BrushRadiusMM, v => Apply(() => session.BrushRadiusMM = v), "0.0", " mm");
            _cutThreshold = DesktopUIFactory.CreateSlider(page, "Próg gęstości cięcia",
                -1000f, 1000f, session.CutThresholdHU, v => Apply(() => session.CutThresholdHU = v), "0", " HU");
            _maxCutDepth = DesktopUIFactory.CreateSlider(page, "Maks. głębokość jednego cięcia",
                1f, 50f, session.MaxCutDepthMM, v => Apply(() => session.MaxCutDepthMM = v), "0.0", " mm");

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Wskazana struktura");
            DesktopUIFactory.CreateParagraph(page,
                "Najpierw wskaż strukturę narzędziem Wskaż, potem wybierz, co z nią zrobić. " +
                "Tą samą drogą usuwa się stół i sprzęt skanera.");

            DesktopUIFactory.CreateButton(page, "Odłóż na bok",
                                          () => session.ExtractPickedIslandAsync().Forget(),
                                          new Color(0.30f, 0.75f, 0.55f));
            DesktopUIFactory.CreateButton(page, "Schowaj do kosza",
                                          () => session.DeletePickedIslandAsync().Forget(),
                                          DesktopUIFactory.Palette.AccentDanger);

            return page;
        }

        private void RefreshToolControls()
        {
            if (_toolSelector == null) return;

            _suppressCallbacks = true;
            _toolSelector.SetIndexWithoutNotify(System.Array.IndexOf(ToolOrder, session.ToolMode));
            _brushRadius.SetValueWithoutNotify(session.BrushRadiusMM);
            _cutThreshold.SetValueWithoutNotify(session.CutThresholdHU);
            _maxCutDepth.SetValueWithoutNotify(session.MaxCutDepthMM);
            _suppressCallbacks = false;

            UpdateToolHint(session.ToolMode);
        }

        private void UpdateToolHint(Helpers.ToolMode mode)
        {
            if (_toolHint == null) return;
            int index = System.Array.IndexOf(ToolOrder, mode);
            _toolHint.text = index >= 0 ? ToolHints[index] : "";
        }

        private void HandleToolModeChanged(Helpers.ToolMode mode)
        {
            if (_toolSelector == null) return;
            _suppressCallbacks = true;
            _toolSelector.SetIndexWithoutNotify(System.Array.IndexOf(ToolOrder, mode));
            _suppressCallbacks = false;

            UpdateToolHint(mode);
        }

        private void HandleBrushRadiusChanged(float radiusMM)
        {
            if (_brushRadius == null) return;
            _suppressCallbacks = true;
            _brushRadius.SetValueWithoutNotify(radiusMM);
            _suppressCallbacks = false;
        }

        #endregion

        // ------------------------------------------------------------------
        #region Obiekty

        private RectTransform BuildObjectsPage(Transform parent)
        {
            var page = CreatePage(parent, "Objects");

            DesktopUIFactory.CreateSectionHeader(page, "Obiekty na scenie");
            DesktopUIFactory.CreateParagraph(page,
                "Ukrycie obiektu jest niezależne od tego, w co celują narzędzia — cel wynika wyłącznie " +
                "z tego, w co trafia promień.");

            _objectListRoot = DesktopUIFactory.CreateRect(page, "ObjectList");
            DesktopUIFactory.AddVerticalLayout(_objectListRoot.gameObject, 4f, 0);

            return page;
        }

        private void RefreshObjectList()
        {
            if (_objectListRoot == null) return;

            for (int i = _objectListRoot.childCount - 1; i >= 0; i--)
                Destroy(_objectListRoot.GetChild(i).gameObject);

            var targets = session.Targets;
            if (targets.Count == 0)
            {
                var empty = DesktopUIFactory.CreateText(_objectListRoot, "Brak wczytanego badania.",
                                                        13f, FontStyles.Italic, TextAlignmentOptions.Left,
                                                        DesktopUIFactory.Palette.TextDim);
                DesktopUIFactory.SetHeight(empty.gameObject, 24f);
                return;
            }

            for (int i = 0; i < targets.Count; i++)
                BuildObjectRow(targets[i]);
        }

        private void BuildObjectRow(Helpers.VolumeRenderTarget target)
        {
            var row = DesktopUIFactory.CreatePanel(_objectListRoot, "Row_" + target.DisplayName,
                                                   DesktopUIFactory.Palette.Panel);
            DesktopUIFactory.SetHeight(row.gameObject, target.IsCutBin ? 62f : 34f);

            var visibility = DesktopUIFactory.CreateToggle(row.transform, target.DisplayName, target.Visible,
                                                           v => session.SetTargetVisible(target, v));
            var visibilityRect = (RectTransform)visibility.transform;
            visibilityRect.anchorMin = new Vector2(0f, 1f);
            visibilityRect.anchorMax = new Vector2(1f, 1f);
            visibilityRect.pivot = new Vector2(0.5f, 1f);
            visibilityRect.offsetMin = new Vector2(8f, -32f);
            visibilityRect.offsetMax = new Vector2(-8f, -2f);

            if (!target.IsCutBin) return;

            // Kosz ma jedną dodatkową akcję: nałożenie DOKŁADNIE na obiekt źródłowy, żeby zobaczyć,
            // skąd konkretnie materiał został wycięty. Zwykłe obiekty jej nie potrzebują.
            bool aligned = session.IsBinAligned(target);
            var alignButton = DesktopUIFactory.CreateButton(row.transform,
                aligned ? "Odsuń od źródła" : "Nałóż na źródło", null,
                DesktopUIFactory.Palette.PanelAlt, 24f);

            alignButton.onClick.AddListener(() =>
            {
                session.SetBinAligned(target, !session.IsBinAligned(target));
                // Etykieta opisuje stan, więc musi się przełączyć razem z nim — a lista nie
                // przebudowuje się sama, bo zmiana nie dotyka składu obiektów na scenie.
                var label = alignButton.GetComponentInChildren<TextMeshProUGUI>();
                label.text = session.IsBinAligned(target) ? "Odsuń od źródła" : "Nałóż na źródło";
            });

            var buttonRect = (RectTransform)alignButton.transform;
            buttonRect.anchorMin = new Vector2(0f, 0f);
            buttonRect.anchorMax = new Vector2(1f, 0f);
            buttonRect.pivot = new Vector2(0.5f, 0f);
            buttonRect.offsetMin = new Vector2(30f, 4f);
            buttonRect.offsetMax = new Vector2(-8f, 28f);
        }

        #endregion

        // ------------------------------------------------------------------

        /// <summary>
        /// Zapisuje zmianę do sesji, chyba że kontrolka została właśnie ustawiona programowo (bo
        /// wartość zmieniła druga warstwa interfejsu) — patrz _suppressCallbacks.
        /// </summary>
        private void Apply(System.Action change)
        {
            if (_suppressCallbacks) return;
            change();
        }

        // ------------------------------------------------------------------
        #region Presety użytkownika

        /// <summary>
        /// Sekcja własnych presetów — ta sama w każdym miejscu, w którym da się coś zapisać.
        /// Zaszytych nastaw nie ma świadomie: wartości dobiera się do konkretnego badania, więc
        /// cudze liczby i tak trzeba by poprawiać. Zamiast tego użytkownik zapisuje swoje.
        ///
        /// Lista przebudowuje się z PresetStore.OnChanged — tak samo, jak lista obiektów reaguje
        /// na OnTargetsChanged.
        /// </summary>
        private void BuildPresetSection(Transform page, PresetStore store, string hint,
                                        Action<Preset> apply, Action<string> capture)
        {
            DesktopUIFactory.CreateParagraph(page, hint);

            var list = DesktopUIFactory.CreateRect(page, "Presets");
            DesktopUIFactory.AddVerticalLayout(list.gameObject, 4f, 0);

            var nameRow = DesktopUIFactory.CreateRect(page, "SaveRow");
            DesktopUIFactory.AddHorizontalLayout(nameRow.gameObject, 6f);
            DesktopUIFactory.SetHeight(nameRow.gameObject, DesktopUIFactory.RowHeight);

            var nameInput = DesktopUIFactory.CreateInputField(nameRow, "nazwa presetu");

            var saveButton = DesktopUIFactory.CreateButton(nameRow, "Zapisz", null,
                                                           DesktopUIFactory.Palette.Accent);
            saveButton.gameObject.GetComponent<LayoutElement>().preferredWidth = 90f;
            saveButton.gameObject.GetComponent<LayoutElement>().flexibleWidth = 0f;

            saveButton.onClick.AddListener(() =>
            {
                if (string.IsNullOrWhiteSpace(nameInput.text)) return;
                capture(nameInput.text);
                nameInput.SetTextWithoutNotify("");
            });

            void Rebuild()
            {
                if (list == null) return;

                for (int i = list.childCount - 1; i >= 0; i--)
                    Destroy(list.GetChild(i).gameObject);

                if (store.All.Count == 0)
                {
                    DesktopUIFactory.CreateParagraph(list, "Brak zapisanych presetów.");
                    return;
                }

                foreach (var preset in store.All)
                {
                    var captured = preset;

                    var row = DesktopUIFactory.CreateRect(list, "Preset_" + preset.Name);
                    DesktopUIFactory.AddHorizontalLayout(row.gameObject, 4f);
                    DesktopUIFactory.SetHeight(row.gameObject, 30f);

                    DesktopUIFactory.CreateButton(row, preset.Name,
                                                  () => Apply(() => apply(captured)),
                                                  DesktopUIFactory.Palette.PanelAlt, 30f);

                    // Usuwanie stoi przy swoim presecie, a nie w osobnym trybie edycji — przy kilku
                    // pozycjach osobny tryb kosztowałby więcej kliknięć niż oszczędzał miejsca.
                    var remove = DesktopUIFactory.CreateButton(row, "×",
                                                               () => store.Delete(captured.Name),
                                                               DesktopUIFactory.Palette.AccentDanger, 30f);
                    var removeLayout = remove.gameObject.GetComponent<LayoutElement>();
                    removeLayout.preferredWidth = 34f;
                    removeLayout.flexibleWidth = 0f;
                }
            }

            store.OnChanged += Rebuild;
            // Magazyn żyje w sesji, a panel bywa niszczony — bez odpięcia jego zdarzenie sięgałoby
            // po zniszczone elementy interfejsu.
            _presetUnsubscribers.Add(() => store.OnChanged -= Rebuild);
            Rebuild();
        }

        /// <summary>
        /// Opis dobranego poziomu próbkowania. Dla Auto pokazuje, co faktycznie wybrał sprzęt —
        /// bez tego „Auto" jest jedyną pozycją, przy której nie wiadomo, ile próbek się dostaje.
        /// </summary>
        private void RefreshQualityHint()
        {
            if (_qualityHint == null) return;

            var resolved = session.ResolvedRaymarchQuality;
            int samples = LoadDicomData.SamplesPerModelFor(resolved);

            string prefix = session.RaymarchQuality == LoadDicomData.RaymarchQuality.Auto
                ? $"Auto dobrało poziom {ResolvedName(resolved)} — "
                : "";

            _qualityHint.text = prefix +
                $"{samples} próbek na przekrój modelu. Więcej próbek to ostrzejszy obraz i niższy FPS.";
        }

        private static string ResolvedName(LoadDicomData.RaymarchQuality q) => q switch
        {
            LoadDicomData.RaymarchQuality.Low => "niski",
            LoadDicomData.RaymarchQuality.Medium => "średni",
            _ => "wysoki"
        };

        #endregion
    }
}
