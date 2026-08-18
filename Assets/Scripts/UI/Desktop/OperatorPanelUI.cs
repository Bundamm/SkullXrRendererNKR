using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using SkullXrRendererNKR.App;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SkullXrRendererNKR.UI.Desktop
{
    /// <summary>
    /// Panel operatora — warstwa interfejsu na monitorze. Zawiera WSZYSTKO, co wymaga precyzji,
    /// liczb i czytania: wybór badania, nastawy okna gęstości, rozpoznawanie struktur i pełną listę
    /// obiektów. Menu na dłoni w goglach celowo dubluje z tego tylko rzeczy „fizyczne” (narzędzie,
    /// pędzel, kosz, widoczność) — patrz HandMenuController.
    ///
    /// Obie warstwy działają jednocześnie nad tym samym stanem (VolumeSession), więc panel nie
    /// przechowuje niczego u siebie: każda kontrolka pisze do sesji, a odczytuje się z eventów sesji
    /// przez *WithoutNotify, żeby zmiana zrobiona w goglach przesunęła suwak tutaj i nie wróciła
    /// z powrotem jako kolejna zmiana.
    ///
    /// Wystarczy dodać ten komponent na obiekt w scenie (np. ten sam co AppFlow) — całe UI powstaje
    /// z kodu, patrz DesktopUIFactory.
    /// </summary>
    public partial class OperatorPanelUI : MonoBehaviour
    {
        [Tooltip("Puste = znajdzie w scenie.")]
        public VolumeSession session;
        public AppFlow appFlow;

        [Tooltip("Szerokość panelu w pikselach rozdzielczości odniesienia (1920x1080).")]
        public float panelWidth = 400f;

        private Canvas _canvas;
        private RectTransform _tabBarRoot;
        private readonly List<RectTransform> _tabPages = new List<RectTransform>();
        private DesktopUIFactory.SegmentedControl _tabSelector;

        private TextMeshProUGUI _statusText;
        private Image _busyOverlay;
        private Button _undoButton;
        private TextMeshProUGUI _undoButtonLabel;

        private GameObject _confirmRoot;
        private TextMeshProUGUI _confirmTitle, _confirmBody;
        private Button _confirmAccept;

        private void Awake()
        {
            if (session == null) session = VolumeSession.Instance;
            if (session == null) session = FindObjectOfType<VolumeSession>();
            if (appFlow == null) appFlow = AppFlow.Instance;
            if (appFlow == null) appFlow = FindObjectOfType<AppFlow>();

            if (session == null)
            {
                Debug.LogError("[OperatorPanelUI] Brak VolumeSession w scenie — panel nie ma czym sterować.");
                enabled = false;
                return;
            }

            BuildUI();

            // Panel sam zgłasza się jako warstwa robocza, zamiast wymagać przeciągnięcia referencji
            // w Inspektorze — powstaje dopiero w Awake, więc i tak nie dałoby się go tam wskazać.
            if (appFlow != null && appFlow.operatorPanelRoot == null)
                appFlow.operatorPanelRoot = _canvas.gameObject;
        }

        private void OnEnable()
        {
            if (session == null) return;
            session.OnBusyChanged += HandleBusyChanged;
            session.OnStatusChanged += HandleStatusChanged;
            session.OnTargetsChanged += RefreshObjectList;
            session.OnToolModeChanged += HandleToolModeChanged;
            session.OnBrushRadiusChanged += HandleBrushRadiusChanged;
            session.OnRenderSettingsChanged += RefreshRenderControls;
            session.OnSegmentationSettingsChanged += RefreshSegmentationControls;
            session.OnScanChanged += HandleScanChanged;
            session.OnUndoHistoryChanged += RefreshUndoButton;
        }

        private void OnDisable()
        {
            if (session == null) return;
            session.OnBusyChanged -= HandleBusyChanged;
            session.OnStatusChanged -= HandleStatusChanged;
            session.OnTargetsChanged -= RefreshObjectList;
            session.OnToolModeChanged -= HandleToolModeChanged;
            session.OnBrushRadiusChanged -= HandleBrushRadiusChanged;
            session.OnRenderSettingsChanged -= RefreshRenderControls;
            session.OnSegmentationSettingsChanged -= RefreshSegmentationControls;
            session.OnScanChanged -= HandleScanChanged;
            session.OnUndoHistoryChanged -= RefreshUndoButton;

            // Menedżer remotingu żyje niezależnie od panelu — bez odpięcia jego zdarzenie sięgałoby
            // po zniszczone już elementy interfejsu.
            if (_remoting != null) _remoting.OnConnectionStateChanged -= RefreshRemotingSection;

            // To samo dotyczy magazynów presetów, które żyją w sesji.
            foreach (var unsubscribe in _presetUnsubscribers) unsubscribe();
            _presetUnsubscribers.Clear();
        }

        private void Start()
        {
            RefreshRenderControls();
            RefreshToolControls();
            RefreshSegmentationControls();
            RefreshObjectList();
            HandleScanChanged(session.CurrentScanPath);
            HandleBusyChanged(session.IsBusy);
            RefreshUndoButton();
        }

        private void Update()
        {
            // Ctrl+Z obok przycisku: odruch jest na tyle powszechny, że brak skrótu sam w sobie
            // wygląda na usterkę. Blokujemy w trakcie ciężkiej operacji, tak samo jak resztę panelu.
            var keyboard = Keyboard.current;
            if (keyboard == null || session == null || session.IsBusy) return;

            bool ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            if (ctrl && keyboard.zKey.wasPressedThisFrame && session.CanUndo)
                session.UndoLastEditAsync().Forget();
        }

        private void RefreshUndoButton()
        {
            if (_undoButton == null) return;

            bool canUndo = session.CanUndo;
            _undoButton.interactable = canUndo;
            // Podpis mówi, CO zostanie cofnięte — „Cofnij” bez tego jest obietnicą bez pokrycia,
            // zwłaszcza że część operacji (usuwanie sprzętu skanera) świadomie nie trafia do historii.
            // Bez znaków ozdobnych: fonty użyte w tym projekcie nie mają strzałek w rodzaju ↶ i
            // podmieniają je na pusty prostokąt, ostrzegając o tym przy każdym przerysowaniu.
            _undoButtonLabel.text = canUndo ? $"Cofnij: {session.NextUndoLabel}" : "Nie ma czego cofnąć";
        }

        // ------------------------------------------------------------------
        #region Szkielet

        private void BuildUI()
        {
            // sortingOrder niższy niż ekran startowy (200) — launcher ma go zasłaniać, nie odwrotnie.
            _canvas = DesktopUIFactory.CreateScreenCanvas("OperatorPanelCanvas", 100, transform);

            var panel = DesktopUIFactory.CreatePanel(_canvas.transform, "OperatorPanel",
                                                     DesktopUIFactory.Palette.Background);
            var panelRect = (RectTransform)panel.transform;
            // Zadokowany do prawej krawędzi na pełną wysokość: model zostaje widoczny po lewej, a
            // panel nie zasłania środka sceny, w który celuje się narzędziami.
            panelRect.anchorMin = new Vector2(1f, 0f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 0.5f);
            panelRect.sizeDelta = new Vector2(panelWidth, 0f);
            panelRect.anchoredPosition = Vector2.zero;

            var title = DesktopUIFactory.CreateText(panel.transform, "Panel operatora", 20f, FontStyles.Bold);
            var titleRect = (RectTransform)title.transform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.offsetMin = new Vector2(16f, -46f);
            titleRect.offsetMax = new Vector2(-16f, -12f);

            // --- pasek zakładek ---
            _tabBarRoot = DesktopUIFactory.CreateRect(panel.transform, "TabBar");
            _tabBarRoot.anchorMin = new Vector2(0f, 1f);
            _tabBarRoot.anchorMax = new Vector2(1f, 1f);
            _tabBarRoot.pivot = new Vector2(0.5f, 1f);
            _tabBarRoot.offsetMin = new Vector2(12f, -92f);
            _tabBarRoot.offsetMax = new Vector2(-12f, -50f);

            // --- obszar treści (przewijany) ---
            var contentHost = DesktopUIFactory.CreateRect(panel.transform, "ContentHost");
            contentHost.anchorMin = new Vector2(0f, 0f);
            contentHost.anchorMax = new Vector2(1f, 1f);
            contentHost.offsetMin = new Vector2(8f, 44f);
            contentHost.offsetMax = new Vector2(-8f, -96f);

            var pageContent = DesktopUIFactory.CreateScrollList(contentHost, "Pages", out _);
            DesktopUIFactory.Stretch((RectTransform)pageContent.parent);

            // --- pasek stanu ---
            var statusBar = DesktopUIFactory.CreatePanel(panel.transform, "StatusBar",
                                                         DesktopUIFactory.Palette.Panel);
            var statusRect = (RectTransform)statusBar.transform;
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0f);
            statusRect.pivot = new Vector2(0.5f, 0f);
            statusRect.sizeDelta = new Vector2(0f, 36f);
            statusRect.anchoredPosition = Vector2.zero;

            _statusText = DesktopUIFactory.CreateText(statusBar.transform, "", 13f, FontStyles.Normal,
                                                      TextAlignmentOptions.Left,
                                                      DesktopUIFactory.Palette.TextDim);
            var statusTextRect = (RectTransform)_statusText.transform;
            statusTextRect.anchorMin = new Vector2(0f, 0f);
            statusTextRect.anchorMax = new Vector2(0.55f, 1f);
            statusTextRect.offsetMin = new Vector2(10f, 0f);
            statusTextRect.offsetMax = new Vector2(0f, 0f);

            // Cofanie stoi w pasku stanu, a nie w którejś zakładce — jest potrzebne dokładnie wtedy,
            // gdy coś poszło nie tak, więc nie może wymagać wcześniejszego odgadnięcia, gdzie leży.
            _undoButton = DesktopUIFactory.CreateButton(statusBar.transform, "Cofnij",
                                                        () => session.UndoLastEditAsync().Forget(),
                                                        DesktopUIFactory.Palette.PanelAlt, 26f);
            var undoRect = (RectTransform)_undoButton.transform;
            undoRect.anchorMin = new Vector2(0.55f, 0.5f);
            undoRect.anchorMax = new Vector2(1f, 0.5f);
            undoRect.pivot = new Vector2(1f, 0.5f);
            undoRect.offsetMin = new Vector2(0f, -13f);
            undoRect.offsetMax = new Vector2(-8f, 13f);
            _undoButtonLabel = _undoButton.GetComponentInChildren<TextMeshProUGUI>();
            _undoButtonLabel.fontSize = 12f;

            // --- strony ---
            var tabNames = new[] { "Badanie", "Obraz", "Struktury", "Narzędzia", "Obiekty" };
            _tabPages.Add(BuildScanPage(pageContent));
            _tabPages.Add(BuildRenderPage(pageContent));
            _tabPages.Add(BuildSegmentationPage(pageContent));
            _tabPages.Add(BuildToolsPage(pageContent));
            _tabPages.Add(BuildObjectsPage(pageContent));

            _tabSelector = DesktopUIFactory.CreateSegmented(_tabBarRoot, null, tabNames, 0, ShowTab);
            DesktopUIFactory.Stretch((RectTransform)_tabSelector.Buttons[0].transform.parent);
            foreach (var tab in _tabSelector.Buttons)
                tab.GetComponentInChildren<TextMeshProUGUI>().fontSize = 11f;

            // --- zasłona blokady ---
            // Przezroczysta warstwa nad CAŁYM panelem zamiast wyłączania każdego przycisku z osobna:
            // kontrolek jest kilkadziesiąt i dochodzą kolejne, a każda pominięta w takiej liście
            // byłaby cichą dziurą pozwalającą wystrzelić drugą ciężką operację w trakcie pierwszej.
            _busyOverlay = DesktopUIFactory.CreatePanel(panel.transform, "BusyOverlay",
                                                        new Color(0.05f, 0.05f, 0.06f, 0.55f));
            var overlayRect = DesktopUIFactory.Stretch((RectTransform)_busyOverlay.transform);
            // Pasek stanu zostaje odsłonięty — to jedyne miejsce, które mówi, na co właściwie czekamy.
            overlayRect.offsetMin = new Vector2(0f, 36f);
            _busyOverlay.gameObject.SetActive(false);

            BuildConfirmDialog();

            ShowTab(0);
        }

        private void ShowTab(int index)
        {
            for (int i = 0; i < _tabPages.Count; i++)
                _tabPages[i].gameObject.SetActive(i == index);
        }

        /// <summary>
        /// Pusta strona z pionowym układem — kontener na kontrolki jednej zakładki. Bez własnego
        /// ContentSizeFittera: wysokość raportuje rodzicowi sam układ pionowy (patrz AddVerticalLayout),
        /// a dwa mechanizmy liczące to samo potrafią się rozjechać o klatkę.
        /// </summary>
        private RectTransform CreatePage(Transform parent, string name)
        {
            var page = DesktopUIFactory.CreateRect(parent, "Page_" + name);
            DesktopUIFactory.AddVerticalLayout(page.gameObject, 6f, 6);
            return page;
        }

        #endregion

        // ------------------------------------------------------------------
        #region Potwierdzenia

        /// <summary>
        /// Okno potwierdzenia dla operacji, których skutków nie widać od razu na modelu — jedno na
        /// cały panel, z podmienianą treścią. Powstaje raz i jest chowane; tworzenie go na żądanie
        /// oznaczałoby budowanie hierarchii UI w chwili, gdy użytkownik już czeka na odpowiedź.
        /// </summary>
        private void BuildConfirmDialog()
        {
            var shade = DesktopUIFactory.CreatePanel(_canvas.transform, "ConfirmShade",
                                                     new Color(0.03f, 0.03f, 0.04f, 0.7f));
            DesktopUIFactory.Stretch((RectTransform)shade.transform);
            _confirmRoot = shade.gameObject;

            var box = DesktopUIFactory.CreatePanel(shade.transform, "ConfirmBox", DesktopUIFactory.Palette.Panel);
            var boxRect = (RectTransform)box.transform;
            boxRect.anchorMin = new Vector2(0.5f, 0.5f);
            boxRect.anchorMax = new Vector2(0.5f, 0.5f);
            boxRect.pivot = new Vector2(0.5f, 0.5f);
            boxRect.sizeDelta = new Vector2(560f, 0f);
            DesktopUIFactory.AddVerticalLayout(box.gameObject, 12f, 24);
            var fitter = box.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _confirmTitle = DesktopUIFactory.CreateText(box.transform, "", 22f, FontStyles.Bold);
            DesktopUIFactory.SetHeight(_confirmTitle.gameObject, 32f);

            _confirmBody = DesktopUIFactory.CreateText(box.transform, "", 15f, FontStyles.Normal,
                                                       TextAlignmentOptions.TopLeft,
                                                       DesktopUIFactory.Palette.TextDim);
            DesktopUIFactory.SetHeight(_confirmBody.gameObject, 52f);

            var buttons = DesktopUIFactory.CreateRect(box.transform, "Buttons");
            DesktopUIFactory.AddHorizontalLayout(buttons.gameObject, 10f);
            DesktopUIFactory.SetHeight(buttons.gameObject, 40f);

            DesktopUIFactory.CreateButton(buttons, "Anuluj", () => _confirmRoot.SetActive(false));
            _confirmAccept = DesktopUIFactory.CreateButton(buttons, "Potwierdź", null,
                                                           DesktopUIFactory.Palette.AccentDanger);

            _confirmRoot.SetActive(false);
        }

        private void ConfirmThen(string title, string body, Action action)
        {
            if (_confirmRoot == null) { action?.Invoke(); return; }

            _confirmTitle.text = title;
            _confirmBody.text = body;

            _confirmAccept.onClick.RemoveAllListeners();
            _confirmAccept.onClick.AddListener(() =>
            {
                _confirmRoot.SetActive(false);
                action?.Invoke();
            });

            _confirmRoot.SetActive(true);
        }

        #endregion

        // ------------------------------------------------------------------
        #region Reakcje na zmiany stanu

        private void HandleBusyChanged(bool busy)
        {
            if (_busyOverlay != null) _busyOverlay.gameObject.SetActive(busy);
        }

        private void HandleStatusChanged(string status)
        {
            if (_statusText != null) _statusText.text = status;
        }

        #endregion
    }
}
