using System.Threading;
using Cysharp.Threading.Tasks;
using SkullXrRendererNKR.App;
using SkullXrRendererNKR.Platform;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SkullXrRendererNKR.UI.Desktop
{
    /// <summary>
    /// Ekran startowy na monitorze: wybór folderu ze skanem, podgląd co w nim jest i postęp
    /// wczytywania. Świadomie jest to warstwa TYLKO na monitorze — wskazanie pliku na dysku wymaga
    /// systemowego okna i czytania drobnego tekstu, czyli dokładnie tego, czego nie da się sensownie
    /// robić w goglach. Menu na dłoni pojawia się dopiero, gdy jest już co oglądać (patrz AppFlow).
    ///
    /// Wystarczy dodać ten komponent na dowolny obiekt w scenie (najlepiej ten sam co AppFlow) —
    /// całe UI powstaje z kodu, patrz DesktopUIFactory.
    /// </summary>
    public class LauncherUI : MonoBehaviour
    {
        [Tooltip("Puste = znajdzie AppFlow w scenie.")]
        public AppFlow appFlow;

        private Canvas _canvas;
        private GameObject _chooserRoot;
        private GameObject _loadingRoot;

        private RectTransform _recentList;
        private RectTransform _seriesList;
        private GameObject _seriesListRoot;
        private TMP_InputField _pathInput;
        private TextMeshProUGUI _infoText;
        private Button _loadButton;
        private Button _browseButton;
        private Button _upButton;

        private TextMeshProUGUI _stageText;
        private Image _progressFill;

        private string _selectedPath;
        // Granica nawigacji wstecz i folder aktualnie oglądany — patrz BrowseAsync i GoUp.
        private string _browseRoot;
        private string _currentFolder;
        private CancellationTokenSource _inspectCts;

        private void Awake()
        {
            if (appFlow == null) appFlow = FindObjectOfType<AppFlow>();
            BuildUI();
        }

        private void OnEnable()
        {
            if (appFlow == null) return;
            appFlow.OnStateChanged += HandleStateChanged;
            appFlow.OnLoadProgress += HandleLoadProgress;
        }

        private void OnDisable()
        {
            if (appFlow == null) return;
            appFlow.OnStateChanged -= HandleStateChanged;
            appFlow.OnLoadProgress -= HandleLoadProgress;
        }

        private void Start()
        {
            RefreshRecentList();
            SetSelectedPath(null, null);
            HandleStateChanged(appFlow != null ? appFlow.State : AppState.Launcher);
        }

        private void OnDestroy()
        {
            _inspectCts?.Cancel();
            _inspectCts?.Dispose();
        }

        // ------------------------------------------------------------------
        #region Budowa UI

        private void BuildUI()
        {
            // Ekran startowy musi być NAD panelem operatora — oba to Screen Space Overlay, więc o
            // kolejności decyduje wyłącznie sortingOrder.
            _canvas = DesktopUIFactory.CreateScreenCanvas("LauncherCanvas", 200, transform);

            _chooserRoot = BuildChooser();
            _loadingRoot = BuildLoadingPanel();
        }

        private GameObject BuildChooser()
        {
            var background = DesktopUIFactory.CreatePanel(_canvas.transform, "LauncherBackground",
                                                          DesktopUIFactory.Palette.Background);
            DesktopUIFactory.Stretch((RectTransform)background.transform);

            var column = DesktopUIFactory.CreateRect(background.transform, "Column");
            column.anchorMin = new Vector2(0.5f, 0.5f);
            column.anchorMax = new Vector2(0.5f, 0.5f);
            column.pivot = new Vector2(0.5f, 0.5f);
            column.sizeDelta = new Vector2(880f, 0f);
            DesktopUIFactory.AddVerticalLayout(column.gameObject, 10f, 24);

            // Wysokość liczona z zawartości, nie stała: lista serii pojawia się i znika zależnie od
            // tego, czy wskazano folder studium, a przy stałej wysokości kolumna raz przepełniałaby
            // się, raz zostawiała pustkę pod spodem. Przy zakotwiczeniu na środku ekranu rosnąca
            // kolumna sama zostaje wyśrodkowana.
            var columnFitter = column.gameObject.AddComponent<ContentSizeFitter>();
            columnFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var title = DesktopUIFactory.CreateText(column, "Wybór skanu", 34f, FontStyles.Bold);
            DesktopUIFactory.SetHeight(title.gameObject, 46f);

            var subtitle = DesktopUIFactory.CreateText(column,
                "Wskaż folder z pojedynczą serią DICOM (jeden folder = jedna seria plastrów).",
                16f, FontStyles.Normal, TextAlignmentOptions.Left, DesktopUIFactory.Palette.TextDim);
            DesktopUIFactory.SetHeight(subtitle.gameObject, 26f);

            var recentLabel = DesktopUIFactory.CreateText(column, "Ostatnio otwierane", 18f, FontStyles.Bold);
            DesktopUIFactory.SetHeight(recentLabel.gameObject, 30f);

            _recentList = DesktopUIFactory.CreateScrollList(column, "RecentList", out _);
            DesktopUIFactory.SetHeight(_recentList.parent.gameObject, 180f);

            // --- Wiersz: systemowe okno + ręczna ścieżka -------------------
            var browseRow = DesktopUIFactory.CreateRect(column, "BrowseRow");
            DesktopUIFactory.AddHorizontalLayout(browseRow.gameObject);
            DesktopUIFactory.SetHeight(browseRow.gameObject, DesktopUIFactory.RowHeight);

            // Powrót do folderu nadrzędnego, ale NIE wyżej niż ten wskazany systemowym oknem
            // (patrz BrowseAsync) — wybór foldera to decyzja, gdzie zaczynamy szukać, a wędrowanie
            // stamtąd w górę zamieniłoby ekran startowy w drugą przeglądarkę plików.
            _upButton = DesktopUIFactory.CreateButton(browseRow, "◄ Wyżej",
                                                      () => GoUpAsync().Forget(),
                                                      DesktopUIFactory.Palette.PanelAlt);
            var upLayout = _upButton.gameObject.GetComponent<LayoutElement>();
            upLayout.preferredWidth = 110f;
            upLayout.flexibleWidth = 0f;

            _browseButton = DesktopUIFactory.CreateButton(browseRow, "Wybierz folder…",
                                                          () => BrowseAsync().Forget(),
                                                          DesktopUIFactory.Palette.Accent);
            _browseButton.gameObject.GetComponent<LayoutElement>().preferredWidth = 220f;
            _browseButton.gameObject.GetComponent<LayoutElement>().flexibleWidth = 0f;

            _pathInput = DesktopUIFactory.CreateInputField(browseRow, "…albo wklej ścieżkę do folderu");
            _pathInput.onEndEdit.AddListener(path => InspectAsync(path).Forget());

            if (!WindowsFolderPicker.IsSupported)
            {
                _browseButton.interactable = false;
                var hint = DesktopUIFactory.CreateText(column,
                    "Systemowe okno wyboru folderu jest dostępne tylko na komputerze — tutaj wpisz ścieżkę ręcznie.",
                    14f, FontStyles.Italic, TextAlignmentOptions.Left, DesktopUIFactory.Palette.TextDim);
                DesktopUIFactory.SetHeight(hint.gameObject, 24f);
            }

            // --- Podgląd wybranej serii ------------------------------------
            var infoPanel = DesktopUIFactory.CreatePanel(column, "InfoPanel", DesktopUIFactory.Palette.Panel);
            DesktopUIFactory.SetHeight(infoPanel.gameObject, 120f);

            _infoText = DesktopUIFactory.CreateText(infoPanel.transform, "", 16f);
            DesktopUIFactory.Stretch((RectTransform)_infoText.transform, 14f);
            _infoText.alignment = TextAlignmentOptions.TopLeft;

            // Lista serii z podfolderów — widoczna tylko wtedy, gdy wskazany folder sam nie jest
            // serią, ale zawiera serie (typowo: folder studium). Ukryta nie zajmuje miejsca w układzie.
            _seriesList = DesktopUIFactory.CreateScrollList(column, "SeriesList", out _);
            _seriesListRoot = _seriesList.parent.gameObject;
            DesktopUIFactory.SetHeight(_seriesListRoot, 150f);
            _seriesListRoot.SetActive(false);

            _loadButton = DesktopUIFactory.CreateButton(column, "Wczytaj skan",
                                                        () => LoadSelectedAsync().Forget(),
                                                        DesktopUIFactory.Palette.Accent, 46f);
            return background.gameObject;
        }

        private GameObject BuildLoadingPanel()
        {
            var background = DesktopUIFactory.CreatePanel(_canvas.transform, "LoadingBackground",
                                                          DesktopUIFactory.Palette.Background);
            DesktopUIFactory.Stretch((RectTransform)background.transform);

            var column = DesktopUIFactory.CreateRect(background.transform, "Column");
            column.anchorMin = new Vector2(0.5f, 0.5f);
            column.anchorMax = new Vector2(0.5f, 0.5f);
            column.pivot = new Vector2(0.5f, 0.5f);
            column.sizeDelta = new Vector2(680f, 200f);
            DesktopUIFactory.AddVerticalLayout(column.gameObject, 14f, 20);

            var title = DesktopUIFactory.CreateText(column, "Wczytywanie skanu", 26f, FontStyles.Bold);
            DesktopUIFactory.SetHeight(title.gameObject, 36f);

            _stageText = DesktopUIFactory.CreateText(column, "", 16f, FontStyles.Normal,
                                                     TextAlignmentOptions.Left, DesktopUIFactory.Palette.TextDim);
            DesktopUIFactory.SetHeight(_stageText.gameObject, 24f);

            _progressFill = DesktopUIFactory.CreateProgressBar(column);

            DesktopUIFactory.CreateButton(column, "Anuluj", () => appFlow?.CancelLoading());

            background.gameObject.SetActive(false);
            return background.gameObject;
        }

        #endregion

        // ------------------------------------------------------------------
        #region Logika

        private void HandleStateChanged(AppState state)
        {
            if (_chooserRoot != null) _chooserRoot.SetActive(state == AppState.Launcher);
            if (_loadingRoot != null) _loadingRoot.SetActive(state == AppState.Loading);

            if (state == AppState.Launcher) RefreshRecentList();
        }

        private void HandleLoadProgress(LoadDicomData.LoadProgress p)
        {
            if (_stageText != null)
            {
                _stageText.text = p.Total > 0
                    ? $"{p.Stage} — {p.Current} / {p.Total}"
                    : p.Stage;
            }

            if (_progressFill != null)
            {
                // Etap bez policzalnego postępu (czytanie nagłówków, budowa tekstury) zostawia pasek
                // tam, gdzie był — cofanie go do zera wyglądałoby jak zawieszenie.
                float f = p.Fraction;
                if (f >= 0f) _progressFill.fillAmount = f;
            }
        }

        private void RefreshRecentList()
        {
            if (_recentList == null) return;

            for (int i = _recentList.childCount - 1; i >= 0; i--)
                Destroy(_recentList.GetChild(i).gameObject);

            var recent = ScanLibrary.GetRecentFolders();
            if (recent.Count == 0)
            {
                var empty = DesktopUIFactory.CreateText(_recentList, "Nic tu jeszcze nie ma — wskaż folder poniżej.",
                                                        15f, FontStyles.Italic, TextAlignmentOptions.Left,
                                                        DesktopUIFactory.Palette.TextDim);
                DesktopUIFactory.SetHeight(empty.gameObject, 28f);
                return;
            }

            foreach (string path in recent)
            {
                string captured = path;
                var button = DesktopUIFactory.CreateButton(_recentList, path,
                                                           () => InspectAsync(captured).Forget(),
                                                           DesktopUIFactory.Palette.PanelAlt, 32f);

                // Długie ścieżki przycinamy od LEWEJ — nazwa serii na końcu jest tym, co odróżnia
                // wpisy od siebie, a wspólny prefiks katalogów i tak nic nie wnosi.
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                label.alignment = TextAlignmentOptions.Left;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.fontSize = 14f;
            }
        }

        private async UniTaskVoid BrowseAsync()
        {
            // Kolejność: to, co właśnie oglądamy → ostatnio otwierane → folder skanów w projekcie.
            string start = _selectedPath
                           ?? ScanLibrary.LastUsedFolder
                           ?? ScanLibrary.DefaultBrowseFolder(appFlow != null && appFlow.session != null
                                                                  ? appFlow.session.dicomData
                                                                  : null);

            string picked = await WindowsFolderPicker.PickFolderAsync("Wskaż folder z serią DICOM", start);
            if (string.IsNullOrEmpty(picked)) return; // anulowane

            // Folder wskazany systemowym oknem staje się granicą nawigacji — powyżej niego przycisk
            // wstecz nie wypuszcza. Wybór foldera to świadoma decyzja użytkownika, gdzie zaczyna
            // szukać; wędrowanie stamtąd w górę zamieniłoby ekran startowy w drugą przeglądarkę plików.
            _browseRoot = picked;
            await InspectAsync(picked);
        }

        /// <summary>
        /// Wraca do folderu nadrzędnego względem oglądanego. Zatrzymuje się na folderze wskazanym
        /// systemowym oknem — patrz komentarz przy przycisku.
        /// </summary>
        private async UniTaskVoid GoUpAsync()
        {
            string parent = ParentWithinRoot(_currentFolder);
            if (parent == null) return;

            await InspectAsync(parent);
        }

        /// <summary>
        /// Folder nadrzędny, o ile mieści się w granicy nawigacji. Zwraca null, gdy jesteśmy już
        /// w korzeniu, gdy korzeń nie został wyznaczony, albo gdy wyżej nie ma nic.
        /// </summary>
        private string ParentWithinRoot(string folder)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(_browseRoot)) return null;
            if (PathsEqual(folder, _browseRoot)) return null;

            var parent = System.IO.Directory.GetParent(folder.TrimEnd('\\', '/'));
            if (parent == null) return null;

            // Poza korzeń nie wychodzimy nawet wtedy, gdy ścieżka nadrzędna istnieje.
            string candidate = parent.FullName;
            return IsWithin(candidate, _browseRoot) ? candidate : null;
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'),
                          System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
                          System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Czy `path` to korzeń albo coś pod nim.</summary>
        private static bool IsWithin(string path, string root)
        {
            if (PathsEqual(path, root)) return true;

            string full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
            string rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
            return full.StartsWith(rootFull + System.IO.Path.DirectorySeparatorChar,
                                   System.StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshUpButton()
        {
            if (_upButton == null) return;
            _upButton.interactable = ParentWithinRoot(_currentFolder) != null;
        }

        private async UniTask InspectAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            path = path.Trim().Trim('"'); // ścieżka skopiowana z Eksploratora bywa w cudzysłowach

            _inspectCts?.Cancel();
            _inspectCts?.Dispose();
            _inspectCts = new CancellationTokenSource();

            _currentFolder = path;
            // Wskazanie folderu z listy ostatnich albo wpisanie ścieżki też wyznacza granicę —
            // inaczej przycisk „wyżej" byłby martwy wszędzie poza ścieżką przez systemowe okno.
            if (string.IsNullOrEmpty(_browseRoot) || !IsWithin(path, _browseRoot)) _browseRoot = path;

            SetSelectedPath(null, "Sprawdzam folder…");
            if (_pathInput != null) _pathInput.SetTextWithoutNotify(path);

            ScanInfo info;
            try
            {
                info = await ScanLibrary.InspectFolderAsync(path, _inspectCts.Token);
            }
            catch (System.OperationCanceledException)
            {
                return; // wskazano w międzyczasie inny folder
            }

            if (info.HasNestedSeries)
            {
                // Folder studium: nie da się go wczytać wprost, ale nie jest to błąd — pokazujemy
                // serie, które w nim leżą, zamiast odsyłać z powrotem do okna wyboru folderu.
                SetSelectedPath(null, $"<b>{info.FolderName}</b>\n{info.Summary}\n" +
                                      $"<color=#9EA5AE>{path}</color>");
                ShowNestedSeries(info.NestedSeries);
                return;
            }

            if (!info.IsValid)
            {
                SetSelectedPath(null, $"<b>{path}</b>\n<color=#D24A42>{info.Summary}</color>");
                return;
            }

            string patient = string.IsNullOrWhiteSpace(info.PatientName) ? "(brak danych pacjenta)" : info.PatientName;
            SetSelectedPath(path, $"<b>{info.FolderName}</b>\n{patient}\n{info.Summary}\n" +
                                  $"<color=#9EA5AE>{path}</color>");
        }

        private void ShowNestedSeries(System.Collections.Generic.List<ScanInfo> series)
        {
            if (_seriesList == null) return;

            for (int i = _seriesList.childCount - 1; i >= 0; i--)
                Destroy(_seriesList.GetChild(i).gameObject);

            foreach (var entry in series)
            {
                string capturedPath = entry.FolderPath;
                string description = string.IsNullOrWhiteSpace(entry.SeriesDescription)
                    ? ""
                    : " — " + entry.SeriesDescription;

                var button = DesktopUIFactory.CreateButton(_seriesList,
                    $"{entry.FolderName}{description}   ({entry.SliceCount} plików)",
                    () => InspectAsync(capturedPath).Forget(),
                    DesktopUIFactory.Palette.PanelAlt, 32f);

                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                label.alignment = TextAlignmentOptions.Left;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.fontSize = 14f;
            }

            _seriesListRoot.SetActive(series.Count > 0);
        }

        private void SetSelectedPath(string path, string info)
        {
            _selectedPath = path;
            if (_infoText != null) _infoText.text = info ?? "Nie wybrano jeszcze żadnego folderu.";
            if (_loadButton != null) _loadButton.interactable = !string.IsNullOrEmpty(path);

            // Lista serii dotyczy KONKRETNEGO folderu studium — przy każdym innym wyborze musi
            // zniknąć, żeby nie sugerować, że należy do nowo wskazanego miejsca.
            if (_seriesListRoot != null) _seriesListRoot.SetActive(false);

            RefreshUpButton();
        }

        private async UniTaskVoid LoadSelectedAsync()
        {
            if (string.IsNullOrEmpty(_selectedPath) || appFlow == null) return;

            if (_progressFill != null) _progressFill.fillAmount = 0f;
            if (_stageText != null) _stageText.text = "Przygotowanie…";

            await appFlow.LoadScanAsync(_selectedPath);
        }

        #endregion
    }
}
