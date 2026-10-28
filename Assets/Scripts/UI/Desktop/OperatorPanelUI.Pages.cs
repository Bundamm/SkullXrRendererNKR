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

        private static readonly string[] ToolNames = { "Pick", "Cut", "Restore", "Tunnel", "Inspect" };

        // Objaśnienia narzędzi — w dymkach nad przyciskami, nie na stałe pod nimi.
        private static readonly string[] ToolHints =
        {
            "Click a structure on the model to isolate it from the rest.",
            "Drag over the model to cut material away. Cut material goes to the bin, it is not deleted.",
            "Drag over a spot to bring back material that was moved to the bin.",
            "One click drills straight through the model along the view direction.",
            "Click the model to read the density (HU) and island ID under the cursor. Changes nothing."
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
        private DesktopUIFactory.SliderControl _windowLow, _windowHigh;
        private TextMeshProUGUI _windowRangeInfo;
        private TextMeshProUGUI _undoHistoryText;
        private DesktopUIFactory.SliderControl _visibleMaterialThreshold, _hueLow, _hueHigh;
        private DesktopUIFactory.SegmentedControl _qualitySelector, _clipAxisSelector;
        private DesktopUIFactory.SliderControl _clipOffset, _clipPitch, _clipYaw;
        private Toggle _clipEnabledToggle;
        private Toggle _emptySkipToggle;

        private DesktopUIFactory.SliderControl _morphThreshold, _morphErosion, _morphExpand;
        private Toggle _keepBackgroundToggle, _negateMaskToggle;

        private DesktopUIFactory.SegmentedControl _toolSelector;
        private GameObject _inspectResultRoot;
        private TextMeshProUGUI _inspectResultText;
        private TextMeshProUGUI _pickedInfoText;
        private Button _extractPickedButton, _deletePickedButton;
        private Vector3Int? _lastPickedVoxel;
        private int _lastIsolatedId = -1;
        private TextMeshProUGUI _isolationText;
        private TMP_InputField _islandIdInput;
        private RectTransform _islandListContent;
        // Najwyższe ID wyspy (254 to etykieta resztkowa dla najmniejszych wysp, patrz VolumeMorphology).
        private const int MaxIslandId = 254;
        // Stan, dla którego zbudowano listę wysp — przebudowa tylko przy jego zmianie, bo
        // RefreshIsolation leci też przy każdym ruchu suwaków segmentacji.
        private int _islandListVersion = -1, _islandListIsolatedId = -1;
        private bool _pickedStateKnown;
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

            DesktopUIFactory.CreateSectionHeader(page, "Loaded scan",
                "Technical details of the loaded DICOM series. Patient data is never shown here.");

            var infoPanel = DesktopUIFactory.CreateAutoHeightPanel(page, "ScanInfo", DesktopUIFactory.Palette.Panel, 12);
            _scanInfoText = DesktopUIFactory.CreateText(infoPanel.transform, "", 14f);
            _scanInfoText.alignment = TextAlignmentOptions.TopLeft;

            DesktopUIFactory.CreateButton(page, "Load another scan…",
                                          () => appFlow?.ReturnToLauncher(),
                                          DesktopUIFactory.Palette.Accent);

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.AddTooltip(DesktopUIFactory.CreateButton(page, "Reset model position",
                                          () => session.ResetModelPosition()),
                "Moves the model back to its starting position, rotation and scale.");

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Workspace presets",
                "Saves ALL view, structure and tool settings under one name, so a whole setup " +
                "for a type of scan can be restored with one click.");
            BuildPresetSection(page, session.FullPresets,
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
            string help = "Streams the view to the headset over Holographic Remoting. Enter the IP address " +
                          "shown in the Holographic Remoting app on the headset.";
#if UNITY_EDITOR
            help += " In the Editor you can also connect before entering Play mode: " +
                    "Mixed Reality > Remoting > Holographic Remoting for Play Mode.";
#endif
            DesktopUIFactory.CreateSectionHeader(page, "Headset", help);

            if (_remoting == null) _remoting = FindObjectOfType<RemotingConnectionManager>();

            if (_remoting == null)
            {
                DesktopUIFactory.CreateParagraph(page, "No RemotingConnectionManager in the scene.");
                return;
            }

            _remotingIpInput = DesktopUIFactory.CreateInputField(page, "headset IP address");
            if (!string.IsNullOrWhiteSpace(_remoting.DefaultIP))
                _remotingIpInput.SetTextWithoutNotify(_remoting.DefaultIP);

            _remotingButton = DesktopUIFactory.CreateButton(page, "Connect to headset",
                                                            ToggleRemoting,
                                                            DesktopUIFactory.Palette.Accent, 40f);
            _remotingButtonLabel = _remotingButton.GetComponentInChildren<TextMeshProUGUI>();

            _remotingStatus = DesktopUIFactory.CreateParagraph(page, "");

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
            _remotingButtonLabel.text = canConnect ? "Connect to headset" : "Disconnect";
            if (_remotingIpInput != null) _remotingIpInput.interactable = canConnect;
        }

        private void HandleScanChanged(string path)
        {
            RefreshWindowRangeInfo();
            if (_scanInfoText == null) return;

            if (string.IsNullOrEmpty(path))
            {
                _scanInfoText.text = "No scan loaded.";
                return;
            }

            var d = session.dicomData;
            string dims = d != null && d.IsVolumeReady
                ? $"{d.Width}×{d.Height}×{d.Depth} voxels ({d.Depth} slices)\n" +
                  $"Voxel: {d.PixelSpacingX:0.###} × {d.PixelSpacingY:0.###} mm, slice {d.SliceThickness:0.###} mm\n" +
                  $"Extent: {d.Width * d.PixelSpacingX:0} × {d.Height * d.PixelSpacingY:0} × {d.Depth * d.SliceThickness:0} mm\n" +
                  $"Density range: {d.HuRangeMin} to {d.HuRangeMax} HU"
                : "Preparing data…";

            _scanInfoText.text = $"<b>{System.IO.Path.GetFileName(path)}</b>\n{DescribeSeries(d)}{dims}\n" +
                                 $"<color=#9EA5AE><size=11>{path}</size></color>";
        }

        /// <summary>
        /// Linia opisu serii z nagłówka DICOM (modalność, opis serii, skaner) — pusta, gdy seria tych
        /// znaczników nie ma. Bez danych pacjenta, patrz LoadDicomData.SeriesModality.
        /// </summary>
        private static string DescribeSeries(LoadDicomData d)
        {
            if (d == null) return "";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(d.SeriesModality)) parts.Add(d.SeriesModality.Trim());
            if (!string.IsNullOrWhiteSpace(d.SeriesDescription)) parts.Add(d.SeriesDescription.Trim());
            if (!string.IsNullOrWhiteSpace(d.ScannerName)) parts.Add(d.ScannerName);
            return parts.Count > 0 ? string.Join(", ", parts) + "\n" : "";
        }

        #endregion

        // ------------------------------------------------------------------
        #region Obraz

        private RectTransform BuildRenderPage(Transform parent)
        {
            var page = CreatePage(parent, "Render");

            DesktopUIFactory.CreateSectionHeader(page, "Density window",
                "Which densities (HU) are shown. Set it either as center + width or as lower + upper " +
                "bound; both pairs always stay in sync. Click a number to type an exact value; -/+ " +
                "change it by one step (Shift: 10 steps). Presets save the current window under a name.");

            BuildPresetSection(page, session.WindowPresets,
                                p => session.ApplyWindowPreset(p),
                                n => session.CaptureWindowPreset(n));

            _windowCenter = DesktopUIFactory.CreateSlider(page, "Window center", -1000f, 2000f,
                session.WindowCenterHU, v => Apply(() => session.WindowCenterHU = v), "0", " HU");
            // Górna granica sięga poza skalę Hounsfielda, żeby dało się suwakiem dojść do nastawy
            // pełnego zakresu (6000 HU), a nie tylko wybrać ją z gotowych.
            _windowWidth = DesktopUIFactory.CreateSlider(page, "Window width", 1f, 6000f,
                session.WindowWidthHU, v => Apply(() => session.WindowWidthHU = v), "0", " HU");

            // To samo okno opisane granicami zamiast środka i szerokości — często prościej myśleć
            // „pokaż od 200 do 1500 HU” niż przeliczać to w głowie. Obie pary są zawsze zgodne.
            _windowLow = DesktopUIFactory.CreateSlider(page, "Lower bound", -2000f, 4000f,
                session.WindowCenterHU - session.WindowWidthHU / 2f, v => Apply(() => SetWindowBounds(v, null)), "0", " HU");
            _windowHigh = DesktopUIFactory.CreateSlider(page, "Upper bound", -1000f, 5000f,
                session.WindowCenterHU + session.WindowWidthHU / 2f, v => Apply(() => SetWindowBounds(null, v)), "0", " HU");

            _windowRangeInfo = DesktopUIFactory.CreateParagraph(page, "");

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Vessel colors",
                "Hue used for lower- and higher-density vessels. Presets save your own color sets.");
            // Nasycenia odpowiadają tym, których LoadDicomData faktycznie używa przy zamianie odcienia
            // na kolor (1.0 dla niskiej gęstości, 0.85 dla wysokiej) — inaczej próbka w panelu
            // kłamałaby względem tego, co widać na modelu.
            _hueLow = DesktopUIFactory.CreateHueSlider(page, "Lower density",
                session.VesselHueLow, v => Apply(() => session.VesselHueLow = v), 1.0f);
            _hueHigh = DesktopUIFactory.CreateHueSlider(page, "Higher density",
                session.VesselHueHigh, v => Apply(() => session.VesselHueHigh = v), 0.85f);

            DesktopUIFactory.CreateButton(page, "Reset colors",
                                          () => session.ResetVesselColors(),
                                          DesktopUIFactory.Palette.PanelAlt, 28f);

            BuildPresetSection(page, session.VesselPresets,
                                p => session.ApplyVesselPreset(p),
                                n => session.CaptureVesselPreset(n));

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Clipping plane",
                "Hides part of the VIEW, not the data: nothing goes to the bin and nothing needs undoing. " +
                "Pick an axis, then move and tilt the plane. In the headset the plane can also be grabbed.");

            _clipEnabledToggle = DesktopUIFactory.CreateToggle(page, "Enable clipping",
                session.ClipPlaneEnabled, v => Apply(() => session.ClipPlaneEnabled = v));

            _clipAxisSelector = DesktopUIFactory.CreateSegmented(page, "Axis",
                new[] { "Left–right", "Up–down", "Front–back" },
                session.ClipPlaneAxis,
                i => Apply(() => session.ClipPlaneAxis = i));

            _clipOffset = DesktopUIFactory.CreateSlider(page, "Position", -1f, 1f,
                session.ClipPlaneOffset, v => Apply(() => session.ClipPlaneOffset = v), "0.00");

            // Oś to punkt wyjścia; te dwa kąty pozwalają ustawić dowolne nachylenie bez chwytania
            // uchwytu — na komputerze to jedyna droga, bo chwytanie działa wyłącznie dłonią w XR.
            _clipPitch = DesktopUIFactory.CreateSlider(page, "Tilt", -90f, 90f,
                session.ClipPlanePitch, v => Apply(() => session.ClipPlanePitch = v), "0", "°");
            _clipYaw = DesktopUIFactory.CreateSlider(page, "Rotation", -180f, 180f,
                session.ClipPlaneYaw, v => Apply(() => session.ClipPlaneYaw = v), "0", "°");

            DesktopUIFactory.CreateButton(page, "Center and straighten",
                                          () => Apply(() =>
                                          {
                                              session.ClipPlaneOffset = 0f;
                                              session.ClipPlanePitch = 0f;
                                              session.ClipPlaneYaw = 0f;
                                          }),
                                          DesktopUIFactory.Palette.PanelAlt, 28f);

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Rendering");

            _surfaceThreshold = DesktopUIFactory.CreateSlider(page, "Surface threshold",
                0.01f, 0.99f, session.SurfaceThreshold, v => Apply(() => session.SurfaceThreshold = v), "0.00")
                .Tip("Opacity at which a ray counts as hitting the surface. Lower = surface appears earlier.");

            _visibleMaterialThreshold = DesktopUIFactory.CreateSlider(page,
                "Visible material", -500f, 500f, session.VisibleMaterialThresholdHU,
                v => Apply(() => session.VisibleMaterialThresholdHU = v), "0", " HU")
                .Tip("Lowest density that is drawn at all (and that the Pick tool can hit).");

            // Podpisy niosą liczbę próbek na przekrój modelu — samo „Wysoka/Średnia/Niska" nie mówi,
            // czym te poziomy się różnią, a to jest jedyna rzecz, która przekłada się na obraz i FPS.
            _qualitySelector = DesktopUIFactory.CreateSegmented(page, "Sampling quality",
                new[]
                {
                    "Auto",
                    "High\n" + LoadDicomData.SamplesPerModelFor(LoadDicomData.RaymarchQuality.High),
                    "Medium\n" + LoadDicomData.SamplesPerModelFor(LoadDicomData.RaymarchQuality.Medium),
                    "Low\n" + LoadDicomData.SamplesPerModelFor(LoadDicomData.RaymarchQuality.Low)
                },
                System.Array.IndexOf(QualityOrder, session.RaymarchQuality),
                i => Apply(() => session.RaymarchQuality = QualityOrder[i]));

            DesktopUIFactory.AddTooltip(_qualitySelector.Buttons[0].transform.parent,
                "Samples per ray across the model. More samples = sharper image, lower FPS. " +
                "Auto picks a level that fits the hardware.");
            _qualityHint = DesktopUIFactory.CreateParagraph(page, "");

            _emptySkipToggle = DesktopUIFactory.AddTooltip(DesktopUIFactory.CreateToggle(page, "Empty space skipping",
                session.EmptySpaceSkipping, v => Apply(() => session.EmptySpaceSkipping = v)),
                "Speeds up rendering by skipping empty blocks. Turn off only for diagnosis: if holes or " +
                "stripes disappear when it is off, the cause is the occupancy map.");

            return page;
        }

        /// <summary>
        /// Ustawia okno z granic. Podaje się jedną granicę, druga zostaje na miejscu; szerokość
        /// nie spada poniżej 1 HU (dolna granica nie może przeskoczyć górnej).
        /// </summary>
        private void SetWindowBounds(float? low, float? high)
        {
            float currentLow = session.WindowCenterHU - session.WindowWidthHU / 2f;
            float currentHigh = session.WindowCenterHU + session.WindowWidthHU / 2f;
            float lo = low ?? currentLow;
            float hi = high ?? currentHigh;
            if (hi - lo < 1f)
            {
                if (low.HasValue) lo = hi - 1f;
                else hi = lo + 1f;
            }
            // Te same granice co suwaki środka i szerokości — sesja ich nie pilnuje, a wartość spoza
            // zakresu tamtych suwaków byłaby na nich pokazana jako skrajna, czyli nieprawdziwie.
            // Odczyt granic po odświeżeniu pokaże to, co faktycznie ustawiono.
            float width = Mathf.Clamp(hi - lo, 1f, 6000f);
            float center = Mathf.Clamp(low.HasValue ? hi - width / 2f : lo + width / 2f, -1000f, 2000f);
            session.WindowWidthHU = width;
            session.WindowCenterHU = center;
        }

        private void RefreshWindowRangeInfo()
        {
            if (_windowRangeInfo == null) return;
            float lo = session.WindowCenterHU - session.WindowWidthHU / 2f;
            float hi = session.WindowCenterHU + session.WindowWidthHU / 2f;
            var d = session.dicomData;
            string series = d != null && d.IsVolumeReady
                ? $"  ·  scan: {d.HuRangeMin} to {d.HuRangeMax} HU"
                : "";
            _windowRangeInfo.text = $"Showing {lo:0} to {hi:0} HU{series}";
        }

        private void RefreshRenderControls()
        {
            if (_windowCenter == null) return;

            _suppressCallbacks = true;
            _windowCenter.SetValueWithoutNotify(session.WindowCenterHU);
            _windowWidth.SetValueWithoutNotify(session.WindowWidthHU);
            _windowLow?.SetValueWithoutNotify(session.WindowCenterHU - session.WindowWidthHU / 2f);
            _windowHigh?.SetValueWithoutNotify(session.WindowCenterHU + session.WindowWidthHU / 2f);
            _surfaceThreshold.SetValueWithoutNotify(session.SurfaceThreshold);
            _visibleMaterialThreshold.SetValueWithoutNotify(session.VisibleMaterialThresholdHU);
            _hueLow.SetValueWithoutNotify(session.VesselHueLow);
            _hueHigh.SetValueWithoutNotify(session.VesselHueHigh);
            _qualitySelector?.SetIndexWithoutNotify(System.Array.IndexOf(QualityOrder, session.RaymarchQuality));
            if (_emptySkipToggle != null) _emptySkipToggle.SetIsOnWithoutNotify(session.EmptySpaceSkipping);
            if (_clipEnabledToggle != null) _clipEnabledToggle.SetIsOnWithoutNotify(session.ClipPlaneEnabled);
            _clipAxisSelector?.SetIndexWithoutNotify(session.ClipPlaneAxis);
            _clipOffset?.SetValueWithoutNotify(session.ClipPlaneOffset);
            _clipPitch?.SetValueWithoutNotify(session.ClipPlanePitch);
            _clipYaw?.SetValueWithoutNotify(session.ClipPlaneYaw);
            _suppressCallbacks = false;

            RefreshQualityHint();
            RefreshWindowRangeInfo();
        }

        #endregion

        // ------------------------------------------------------------------
        #region Struktury

        private RectTransform BuildSegmentationPage(Transform parent)
        {
            var page = CreatePage(parent, "Segmentation");

            DesktopUIFactory.CreateSectionHeader(page, "Structure separation",
                "Splits the scan into separate islands (bones, accessories). The values depend on the " +
                "scan, there are no settings that fit every scan. Click Recalculate after each change.");

            _morphThreshold = DesktopUIFactory.CreateSlider(page, "Bone threshold", -200f, 1500f,
                session.MorphThresholdHU, v => Apply(() => session.MorphThresholdHU = v), "0", " HU")
                .Tip("Density from which voxels count as structure. Bone is usually 300 HU or more.");
            _morphErosion = DesktopUIFactory.CreateSlider(page, "Separation radius", 0f, 10f,
                session.MorphErosionRadius, v => Apply(() => session.MorphErosionRadius = (int)v),
                "0", " vox", wholeNumbers: true)
                .Tip("Cuts thin bridges between structures before they are split. Higher = more islands.");
            _morphExpand = DesktopUIFactory.CreateSlider(page, "Edge expansion", 0f, 10f,
                session.MorphExpandRadius, v => Apply(() => session.MorphExpandRadius = (int)v),
                "0", " vox", wholeNumbers: true)
                .Tip("Grows each island outwards by this many voxels, so its edge is not cut off.");

            DesktopUIFactory.CreateButton(page, "Recalculate",
                                          () => session.GenerateMaskAsync().Forget(),
                                          DesktopUIFactory.Palette.Accent, 40f);

            var statsPanel = DesktopUIFactory.CreateAutoHeightPanel(page, "Stats", DesktopUIFactory.Palette.Panel);
            var stats = DesktopUIFactory.CreateText(statsPanel.transform, "Structures not calculated yet.",
                                                    12f, FontStyles.Normal, TextAlignmentOptions.TopLeft,
                                                    DesktopUIFactory.Palette.TextDim);

            // LoadDicomData wpisuje podsumowanie segmentacji wprost w przypisany TextMeshPro. Dotąd
            // pole trzeba było podpiąć ręcznie w Inspektorze; panel dostarcza je sam, ale nie odbiera
            // już przypisanego — gdyby ktoś celowo wskazał inne miejsce w scenie, ma ono pierwszeństwo.
            if (session.dicomData != null && session.dicomData.morphologyStatsText == null)
                session.dicomData.morphologyStatsText = stats;

            BuildIsolationSection(page);

            _keepBackgroundToggle = DesktopUIFactory.AddTooltip(DesktopUIFactory.CreateToggle(page, "Also show soft tissue",
                session.MorphKeepBackground, v => Apply(() => session.MorphKeepBackground = v)),
                "While a structure is isolated, keep showing tissue that belongs to no island.");
            _negateMaskToggle = DesktopUIFactory.AddTooltip(DesktopUIFactory.CreateToggle(page, "Invert: hide selected structure",
                session.MorphNegateMask, v => Apply(() => session.MorphNegateMask = v)),
                "Instead of showing only the selected structure, hide it and show everything else.");

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateButton(page, "Undo all cuts",
                                          () => ConfirmThen("Undo all cuts?",
                                                            "The model returns to its state before the first cut. " +
                                                            "Separated objects and bins will be removed.",
                                                            () => session.ResetCutsAsync().Forget()),
                                          DesktopUIFactory.Palette.AccentDanger, 42f);

            DesktopUIFactory.CreateSectionHeader(page, "Edit history",
                "Operations that can be undone (Undo in the bottom bar, or Ctrl+Z), newest first. " +
                "Removing scanner equipment is not recorded: only Undo all cuts reverts it.");
            var historyPanel = DesktopUIFactory.CreateAutoHeightPanel(page, "UndoHistory", DesktopUIFactory.Palette.Panel);
            _undoHistoryText = DesktopUIFactory.CreateText(historyPanel.transform, "", 13f,
                                                           FontStyles.Normal, TextAlignmentOptions.TopLeft);

            return page;
        }

        private void RefreshUndoHistoryList()
        {
            if (_undoHistoryText == null) return;
            var labels = session.UndoStepLabels;
            if (labels.Count == 0)
            {
                _undoHistoryText.text = "Nothing to undo.";
                return;
            }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < labels.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(i == 0 ? $"<b>{i + 1}. {labels[i]}</b>  <color=#9EA5AE>(next to undo)</color>"
                                 : $"{i + 1}. {labels[i]}");
            }
            _undoHistoryText.text = sb.ToString();
        }

        /// <summary>
        /// Co aktualnie widać na modelu: całość czy jedna wyspa. Izolację ustawia Picker, ale dotąd
        /// nie było jej ani widać, ani nie dało się jej zdjąć ani zmienić z panelu — po wydzieleniu
        /// obiektu główna struktura potrafiła zostać zawężona do przypadkowej wyspy bez wyjścia.
        ///
        /// Dostępne jest KAŻDE ID, nie tylko kilka największych: wpisanie numeru, krok do poprzedniej/
        /// następnej istniejącej wyspy i przewijana lista wszystkich wysp z objętością.
        /// </summary>
        private void BuildIsolationSection(Transform page)
        {
            DesktopUIFactory.CreateSectionHeader(page, "Visible structure",
                "Show the whole volume, or only one island. Islands are numbered by size: ID 1 is the " +
                "largest. Type an ID, step with < >, or click one in the list. The shown island also " +
                "becomes the selected structure, so it can be set aside or moved to the bin at once.");

            var panel = DesktopUIFactory.CreateAutoHeightPanel(page, "Isolation", DesktopUIFactory.Palette.Panel);
            _isolationText = DesktopUIFactory.CreateText(panel.transform, "", 13f, FontStyles.Normal,
                                                         TextAlignmentOptions.TopLeft);

            DesktopUIFactory.CreateButton(page, "Show all", () => session.ShowWholeVolume(),
                                          DesktopUIFactory.Palette.Accent, 34f);

            // --- wybór po numerze: < [ID] > Pokaż ---
            var row = DesktopUIFactory.CreateRect(page, "IslandIdRow");
            DesktopUIFactory.AddHorizontalLayout(row.gameObject, 4f);
            DesktopUIFactory.SetHeight(row.gameObject, DesktopUIFactory.RowHeight);

            var prev = DesktopUIFactory.CreateButton(row, "<", () => StepIsland(-1));
            SetFixedWidth(prev.gameObject, 34f);
            DesktopUIFactory.AddTooltip(prev.gameObject, "Previous existing island");

            _islandIdInput = DesktopUIFactory.CreateInputField(row, "ID");
            _islandIdInput.contentType = TMP_InputField.ContentType.IntegerNumber;
            // onEndEdit zamiast onSubmit: Enter, Tab i kliknięcie obok też zatwierdzają; Escape — nie.
            _islandIdInput.onEndEdit.AddListener(_ =>
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) return;
                ShowTypedIsland();
            });

            var next = DesktopUIFactory.CreateButton(row, ">", () => StepIsland(1));
            SetFixedWidth(next.gameObject, 34f);
            DesktopUIFactory.AddTooltip(next.gameObject, "Next existing island");

            var show = DesktopUIFactory.CreateButton(row, "Show", ShowTypedIsland, DesktopUIFactory.Palette.Accent);
            SetFixedWidth(show.gameObject, 70f);

            // --- lista wszystkich wysp ---
            var listHost = DesktopUIFactory.CreateRect(page, "IslandListHost");
            DesktopUIFactory.SetHeight(listHost.gameObject, 190f);
            _islandListContent = DesktopUIFactory.CreateScrollList(listHost, "IslandList", out _, nested: true);
            DesktopUIFactory.Stretch((RectTransform)_islandListContent.parent);
        }

        private static void SetFixedWidth(GameObject go, float width)
        {
            var layout = go.GetComponent<LayoutElement>();
            if (layout == null) layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.minWidth = width;
            layout.flexibleWidth = 0f;
        }

        private void ShowTypedIsland()
        {
            if (_islandIdInput == null) return;
            // Ten sam numer co obecnie pokazany — nic do zrobienia (onEndEdit leci też przy samym
            // kliknięciu obok pola, a ponowne szukanie wyspy blokowałoby panel na chwilę bez powodu).
            if (int.TryParse(_islandIdInput.text, out int id) && id > 0 && id != session.IsolatedIslandId)
                session.IsolateIslandAsync(id).Forget();
        }

        /// <summary>
        /// Najbliższa ISTNIEJĄCA wyspa w danym kierunku — numeracja ma luki (wyspy zniknięte po
        /// cięciu), a krok po pustych numerach kończyłby się komunikatem „nie ma takiej wyspy”.
        /// </summary>
        private void StepIsland(int direction)
        {
            int from = session.IsolatedIslandId;
            if (from <= 0 || session.IsAccessoryIsolated)
                from = int.TryParse(_islandIdInput != null ? _islandIdInput.text : null, out int typed) ? typed : 0;

            for (int id = from + direction; id >= 1 && id <= MaxIslandId; id += direction)
            {
                if (session.IslandVolumeCm3(id) <= 0f) continue;
                session.IsolateIslandAsync(id).Forget();
                return;
            }
        }

        /// <summary>Lista wysp budowana od nowa po każdej segmentacji albo zmianie wybranej wyspy.</summary>
        private void RebuildIslandList()
        {
            if (_islandListContent == null) return;
            if (_islandListVersion == session.MaskLabelsVersion &&
                _islandListIsolatedId == session.IsolatedIslandId) return;
            _islandListVersion = session.MaskLabelsVersion;
            _islandListIsolatedId = session.IsolatedIslandId;

            for (int i = _islandListContent.childCount - 1; i >= 0; i--)
                Destroy(_islandListContent.GetChild(i).gameObject);

            int shown = 0;
            for (int id = 1; id <= MaxIslandId; id++)
            {
                float volume = session.IslandVolumeCm3(id);
                if (volume <= 0f) continue;

                int captured = id;
                string label = id == MaxIslandId
                    ? $"ID {id}   {volume:0.##} cm³   <color=#9EA5AE>(smallest islands, merged)</color>"
                    : $"ID {id}   {volume:0.##} cm³";
                var button = DesktopUIFactory.CreateButton(_islandListContent, label,
                                                           () => session.IsolateIslandAsync(captured).Forget(),
                                                           id == session.IsolatedIslandId ? DesktopUIFactory.Palette.Accent
                                                                                          : DesktopUIFactory.Palette.PanelAlt, 26f);
                var text = button.GetComponentInChildren<TextMeshProUGUI>();
                text.fontSize = 13f;
                text.alignment = TextAlignmentOptions.Left;
                shown++;
            }

            if (shown == 0)
                DesktopUIFactory.CreateParagraph(_islandListContent, "Not calculated yet. Click Recalculate.");
        }

        private void RefreshIsolation()
        {
            if (_isolationText == null) return;
            _lastIsolatedId = session.IsolatedIslandId;

            int id = session.IsolatedIslandId;
            string mode = session.MorphNegateMask ? "Hidden" : "Showing only";
            if (id <= 0)
                _isolationText.text = "<b>Showing all</b> (no island isolated)";
            else if (session.IsAccessoryIsolated)
                _isolationText.text = $"<b>{mode}: picked accessory</b> (outside bone segmentation)";
            else
            {
                float volume = session.IslandVolumeCm3(id);
                _isolationText.text = volume > 0f
                    ? $"<b>{mode}: ID {id}</b> ({volume:0.0} cm³)"
                    : $"<b>{mode}: ID {id}</b>";
            }

            // Pole ID podąża za wyborem (także z Pickera), chyba że użytkownik właśnie w nim pisze.
            if (_islandIdInput != null && !_islandIdInput.isFocused)
                _islandIdInput.SetTextWithoutNotify(id > 0 && !session.IsAccessoryIsolated ? id.ToString() : "");

            RebuildIslandList();
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

            RefreshIsolation();
        }

        #endregion

        // ------------------------------------------------------------------
        #region Narzędzia

        private RectTransform BuildToolsPage(Transform parent)
        {
            var page = CreatePage(parent, "Tools");

            DesktopUIFactory.CreateSectionHeader(page, "Active tool",
                "Hover a tool for a short description. The same tools are on the hand menu in the headset.");
            _toolSelector = DesktopUIFactory.CreateSegmented(page, null, ToolNames,
                System.Array.IndexOf(ToolOrder, session.ToolMode),
                i => Apply(() => session.ToolMode = ToolOrder[i]));
            for (int i = 0; i < _toolSelector.Buttons.Length; i++)
                DesktopUIFactory.AddTooltip(_toolSelector.Buttons[i], ToolHints[i]);

            // Wynik narzędzia Diagn. — widoczny tylko przy tym narzędziu, patrz UpdateToolHint.
            var inspectPanel = DesktopUIFactory.CreateAutoHeightPanel(page, "InspectResult", DesktopUIFactory.Palette.Panel);
            _inspectResultRoot = inspectPanel.gameObject;
            _inspectResultText = DesktopUIFactory.CreateText(inspectPanel.transform,
                                                             "Click the model to read the density there.",
                                                             12f, FontStyles.Normal, TextAlignmentOptions.TopLeft,
                                                             DesktopUIFactory.Palette.TextDim);

            DesktopUIFactory.CreateSeparator(page);

            _brushRadius = DesktopUIFactory.CreateSlider(page, "Brush size", 0.5f, 25f,
                session.BrushRadiusMM, v => Apply(() => session.BrushRadiusMM = v), "0.0", " mm")
                .Tip("Radius of the Cut / Restore brush and of the Tunnel hole.");
            _cutThreshold = DesktopUIFactory.CreateSlider(page, "Cut density threshold",
                -1000f, 1000f, session.CutThresholdHU, v => Apply(() => session.CutThresholdHU = v), "0", " HU")
                .Tip("Cut and Tunnel only affect voxels at or above this density.");
            _maxCutDepth = DesktopUIFactory.CreateSlider(page, "Max cut depth",
                1f, 50f, session.MaxCutDepthMM, v => Apply(() => session.MaxCutDepthMM = v), "0.0", " mm")
                .Tip("How deep one Cut stroke can go from the original surface. Protects structures " +
                     "underneath. Tunnel has no depth limit.");

            DesktopUIFactory.CreateSeparator(page);

            DesktopUIFactory.CreateSectionHeader(page, "Selected structure",
                "Select a structure with the Pick tool (or by ID on the Structures tab), then choose " +
                "what to do with it. Scanner table and equipment are removed the same way.");

            var pickedPanel = DesktopUIFactory.CreateAutoHeightPanel(page, "PickedInfo", DesktopUIFactory.Palette.Panel);
            _pickedInfoText = DesktopUIFactory.CreateText(pickedPanel.transform, "", 13f,
                                                          FontStyles.Normal, TextAlignmentOptions.TopLeft);

            _extractPickedButton = DesktopUIFactory.CreateButton(page, "Set aside",
                                          () => session.ExtractPickedIslandAsync().Forget(),
                                          new Color(0.30f, 0.75f, 0.55f));
            _deletePickedButton = DesktopUIFactory.CreateButton(page, "Move to bin",
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
            if (_inspectResultRoot != null) _inspectResultRoot.SetActive(mode == Helpers.ToolMode.Inspect);
        }

        private void HandleInspectReport(string report)
        {
            if (_inspectResultText == null) return;
            _inspectResultText.color = DesktopUIFactory.Palette.Text;
            _inspectResultText.text = report;
        }

        /// <summary>
        /// Wskazanie Pickerem nie ma własnego eventu (ustawia je LoadDicomData w środku operacji),
        /// więc odpytujemy je co klatkę — to porównanie dwóch wartości, przebudowa tekstu tylko przy zmianie.
        /// </summary>
        private void PollPickedIsland()
        {
            if (_pickedInfoText == null) return;
            var voxel = session.PickedVoxel;
            // ID też jest sprawdzane osobno: Picker potrafi zmienić izolację bez zmiany woksela
            // (przeliczenie przenumerowuje wyspy), a wtedy opis „co widać” byłby nieaktualny.
            if (_pickedStateKnown && voxel == _lastPickedVoxel && session.IsolatedIslandId == _lastIsolatedId) return;
            RefreshPickedIsland();
        }

        private void RefreshPickedIsland()
        {
            if (_pickedInfoText == null) return;
            _lastPickedVoxel = session.PickedVoxel;
            _pickedStateKnown = true;
            RefreshIsolation();

            bool picked = session.HasPickedIsland;
            // Przyciski bez wskazania i tak nic nie robią (LoadDicomData tylko loguje ostrzeżenie),
            // więc wyszarzenie mówi wprost to, co dotąd było widać jedynie w konsoli.
            _extractPickedButton.interactable = picked;
            _deletePickedButton.interactable = picked;

            if (!picked)
            {
                _pickedInfoText.text = "Nothing selected.";
                return;
            }

            string owner = session.PickedIslandOwner?.DisplayName ?? "object";
            float volume = session.PickedIslandVolumeCm3;
            _pickedInfoText.text = volume > 0f
                ? $"Selected on: <b>{owner}</b>\nVolume ~{volume:0.0} cm³ <color=#9EA5AE>(as of last Recalculate)</color>"
                : $"Selected on: <b>{owner}</b>";
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

            DesktopUIFactory.CreateSectionHeader(page, "Objects in the scene",
                "Untick to hide an object. Tools always act on whatever the ray hits, so a hidden " +
                "object cannot be cut by accident. A bin can be overlaid on its source to see where " +
                "material was cut from.");

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
                var empty = DesktopUIFactory.CreateText(_objectListRoot, "No scan loaded.",
                                                        13f, FontStyles.Normal, TextAlignmentOptions.Left,
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
                aligned ? "Move away from source" : "Overlay on source", null,
                DesktopUIFactory.Palette.PanelAlt, 24f);

            alignButton.onClick.AddListener(() =>
            {
                session.SetBinAligned(target, !session.IsBinAligned(target));
                // Etykieta opisuje stan, więc musi się przełączyć razem z nim — a lista nie
                // przebudowuje się sama, bo zmiana nie dotyka składu obiektów na scenie.
                var label = alignButton.GetComponentInChildren<TextMeshProUGUI>();
                label.text = session.IsBinAligned(target) ? "Move away from source" : "Overlay on source";
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
        private void BuildPresetSection(Transform page, PresetStore store,
                                        Action<Preset> apply, Action<string> capture)
        {
            var list = DesktopUIFactory.CreateRect(page, "Presets");
            DesktopUIFactory.AddVerticalLayout(list.gameObject, 4f, 0);

            var nameRow = DesktopUIFactory.CreateRect(page, "SaveRow");
            DesktopUIFactory.AddHorizontalLayout(nameRow.gameObject, 6f);
            DesktopUIFactory.SetHeight(nameRow.gameObject, DesktopUIFactory.RowHeight);

            var nameInput = DesktopUIFactory.CreateInputField(nameRow, "preset name");

            var saveButton = DesktopUIFactory.CreateButton(nameRow, "Save", null,
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
                    DesktopUIFactory.CreateParagraph(list, "No saved presets.");
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
                ? $"Auto chose {ResolvedName(resolved)}: "
                : "";

            // Krótki odczyt; reszta objaśnienia jest w dymku nad wyborem jakości.
            _qualityHint.text = prefix + $"{samples} samples across the model";
        }

        private static string ResolvedName(LoadDicomData.RaymarchQuality q) => q switch
        {
            LoadDicomData.RaymarchQuality.Low => "low",
            LoadDicomData.RaymarchQuality.Medium => "medium",
            _ => "high"
        };

        #endregion
    }
}
