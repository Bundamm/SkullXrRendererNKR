using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using MixedReality.Toolkit.UX;
using SkullXrRendererNKR.App;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace SkullXrRendererNKR.UI.XR
{
    /// <summary>
    /// Menu na dłoni w goglach — druga warstwa interfejsu, obok panelu operatora na monitorze.
    /// Zawiera wyłącznie to, co robi się RĘKAMI na modelu: wybór narzędzia, rozmiar pędzla, operacje
    /// na wskazanej wyspie, kosz i widoczność obiektów. Wybór skanu, progi HU i parametry segmentacji
    /// zostają na monitorze — w goglach nie da się sensownie czytać liczb ani przeglądać dysku.
    ///
    /// Kontrolki powstają przez KLONOWANIE elementów, które już są w menu (przycisk i suwak MRTK),
    /// a nie przez budowanie z prymitywów. Gotowe prefaby MRTK mają dopracowaną interakcję dłonią
    /// (dotyk, dźwięk, animacja wciśnięcia, obszar trafienia) i — co ważniejsze — dziedziczą rozmiary
    /// komórki siatki oraz styl ustawione już w scenie, więc nie trzeba ich tu zgadywać.
    ///
    /// Menu ma jedną wąską kolumnę, więc zamiast paska zakładek jest JEDEN przycisk u góry,
    /// przełączający strony po kolei — pasek zakładek zjadłby tyle samo miejsca co trzy kontrolki.
    /// </summary>
    public class HandMenuController : MonoBehaviour
    {
        private enum Page
        {
            Tools,
            Island,
            View,
            Objects
        }

        private static readonly string[] PageNames = { "Narzędzia", "Wyspa", "Widok", "Obiekty" };

        // Kolejność MUSI odpowiadać kolejności przycisków tworzonych w BuildToolsPage.
        private static readonly Helpers.ToolMode[] ToolOrder =
        {
            Helpers.ToolMode.Picker,
            Helpers.ToolMode.Cut,
            Helpers.ToolMode.RemoveIsland,
            Helpers.ToolMode.TunnelCut
        };

        private static readonly string[] ToolNames = { "Wskaż", "Tnij", "Gumka", "Tunel" };

        [Header("Referencje (puste = znajdź automatycznie)")]
        public VolumeSession session;

        [Tooltip("Kontener z układem siatki, w którym stoją elementy menu (w prefabie MRTK: Buttons-GridLayout). Puste = pierwszy układ siatki znaleziony w dzieciach.")]
        public Transform itemsParent;

        [Tooltip("Przycisk-wzorzec do klonowania. Puste = pierwszy PressableButton znaleziony w menu.")]
        public GameObject buttonTemplate;

        [Tooltip("Suwak-wzorzec do klonowania. Puste = pierwszy Slider MRTK znaleziony w menu.")]
        public GameObject sliderTemplate;

        [Header("Zachowanie")]
        [Tooltip("Chowa elementy, które były w menu przed uruchomieniem (posłużyły jako wzorce). Wyłącz, jeśli chcesz zachować własne, ręcznie podpięte kontrolki obok tych generowanych.")]
        public bool hideOriginalItems = true;

        [Tooltip("Zakres suwaka rozmiaru pędzla w milimetrach.")]
        public Vector2 brushRadiusRangeMM = new Vector2(1f, 15f);

        [Tooltip("Zakres suwaka wysokości płaszczyzny cięcia.")]
        public Vector2 cutHeightRange = new Vector2(-1f, 1f);

        private Page _page = Page.Tools;
        private GameObject _pageButton;
        private readonly Dictionary<Page, List<GameObject>> _pageItems = new Dictionary<Page, List<GameObject>>();
        private readonly List<GameObject> _objectRows = new List<GameObject>();

        private PressableButton[] _toolButtons;
        private Slider _brushSlider;
        private GameObject _undoButton;

        private bool _suppressCallbacks;

        // ------------------------------------------------------------------

        private void Awake()
        {
            if (session == null) session = VolumeSession.Instance;
            if (session == null) session = FindObjectOfType<VolumeSession>();
            if (session == null)
            {
                Debug.LogError("[HandMenuController] Brak VolumeSession w scenie — menu na dłoni nie ma czym sterować.");
                enabled = false;
                return;
            }

            if (itemsParent == null)
            {
                var grid = GetComponentInChildren<UnityEngine.UI.GridLayoutGroup>(true);
                if (grid != null) itemsParent = grid.transform;
            }

            if (buttonTemplate == null)
            {
                var button = GetComponentInChildren<PressableButton>(true);
                if (button != null) buttonTemplate = button.gameObject;
            }

            if (sliderTemplate == null)
            {
                var slider = GetComponentInChildren<Slider>(true);
                if (slider != null) sliderTemplate = slider.gameObject;
            }

            if (itemsParent == null || buttonTemplate == null)
            {
                Debug.LogError("[HandMenuController] Nie znalazłem w menu kontenera siatki ani przycisku do sklonowania — " +
                               "wskaż je ręcznie w Inspektorze (Items Parent, Button Template).");
                enabled = false;
                return;
            }

            BuildMenu();
        }

        private void OnEnable()
        {
            if (session == null) return;
            session.OnToolModeChanged += HandleToolModeChanged;
            session.OnBrushRadiusChanged += HandleBrushRadiusChanged;
            session.OnTargetsChanged += RebuildObjectRows;
            session.OnUndoHistoryChanged += RefreshUndoButton;
        }

        private void OnDisable()
        {
            if (session == null) return;
            session.OnToolModeChanged -= HandleToolModeChanged;
            session.OnBrushRadiusChanged -= HandleBrushRadiusChanged;
            session.OnTargetsChanged -= RebuildObjectRows;
            session.OnUndoHistoryChanged -= RefreshUndoButton;
        }

        private void Start()
        {
            HandleToolModeChanged(session.ToolMode);
            HandleBrushRadiusChanged(session.BrushRadiusMM);
            RefreshUndoButton();
            RebuildObjectRows();
            ShowPage(Page.Tools);
        }

        /// <summary>
        /// Podpis mówi, CO zostanie cofnięte. W goglach nie ma paska stanu ani konsoli, więc jest to
        /// jedyne miejsce, z którego użytkownik dowie się, czy jest jeszcze co cofać.
        ///
        /// Bez znaków ozdobnych: font przycisków MRTK (Selawik + zestaw ikon MRTK) nie zawiera strzałek
        /// w rodzaju ↶ i podmienia je na pusty prostokąt, zaśmiecając przy tym konsolę ostrzeżeniem
        /// przy każdym przerysowaniu.
        /// </summary>
        private void RefreshUndoButton()
        {
            if (_undoButton == null) return;
            SetLabel(_undoButton, session.CanUndo ? "Cofnij: " + session.NextUndoLabel : "Nic do cofnięcia");
        }

        // ------------------------------------------------------------------
        #region Budowa

        private void BuildMenu()
        {
            // Wzorce muszą przestać być widoczne ZANIM policzymy je jako elementy menu — inaczej
            // siatka pokazywałaby oryginał obok jego własnego klonu.
            var originals = new List<GameObject>();
            if (hideOriginalItems)
            {
                for (int i = 0; i < itemsParent.childCount; i++)
                    originals.Add(itemsParent.GetChild(i).gameObject);
            }

            _pageButton = CloneButton("Narzędzia ▸", CyclePage);

            BuildToolsPage();
            BuildIslandPage();
            BuildViewPage();

            foreach (var original in originals)
                original.SetActive(false);
        }

        private void BuildToolsPage()
        {
            _toolButtons = new PressableButton[ToolOrder.Length];
            for (int i = 0; i < ToolOrder.Length; i++)
            {
                int captured = i;
                var go = CloneButton(ToolNames[i], () => Apply(() => session.ToolMode = ToolOrder[captured]));
                AddToPage(Page.Tools, go);

                // Tryb jest stanem, nie akcją — przycisk ma zostać wciśnięty, dopóki narzędzie jest
                // aktywne. OneWayToggle, bo wyłączyć trybu się nie da; można tylko wybrać inny.
                var button = go.GetComponent<PressableButton>();
                button.ToggleMode = MixedReality.Toolkit.StatefulInteractable.ToggleType.OneWayToggle;
                _toolButtons[i] = button;
            }

            _brushSlider = CloneSlider("Pędzel", brushRadiusRangeMM.x, brushRadiusRangeMM.y,
                                       session.BrushRadiusMM,
                                       v => Apply(() => session.BrushRadiusMM = v),
                                       out var brushGo);
            if (brushGo != null) AddToPage(Page.Tools, brushGo);

            // Cofanie leży na TEJ SAMEJ stronie co narzędzia, bo tu powstają pomyłki, które trzeba
            // cofnąć — szukanie go na innej stronie kosztowałoby kilka gestów w najgorszym momencie.
            _undoButton = CloneButton("Cofnij", () => session.UndoLastEditAsync().Forget());
            AddToPage(Page.Tools, _undoButton);
        }

        private void BuildIslandPage()
        {
            AddToPage(Page.Island, CloneButton("Odłóż na bok", () => session.ExtractPickedIslandAsync().Forget()));
            AddToPage(Page.Island, CloneButton("Do kosza", () => session.DeletePickedIslandAsync().Forget()));
            AddToPage(Page.Island, CloneButton("Przelicz struktury", () => session.GenerateMaskAsync().Forget()));
        }

        private void BuildViewPage()
        {
            CloneSlider("Przekrój", cutHeightRange.x, cutHeightRange.y, 1f,
                        v => { if (session.dicomData != null) session.dicomData.SetCutHeight(v); },
                        out var cutGo);
            if (cutGo != null) AddToPage(Page.View, cutGo);

            CloneSlider("Powierzchnia", 0.01f, 0.99f, session.SurfaceThreshold,
                        v => Apply(() => session.SurfaceThreshold = v),
                        out var surfaceGo);
            if (surfaceGo != null) AddToPage(Page.View, surfaceGo);

            AddToPage(Page.View, CloneButton("Wyśrodkuj", () => session.ResetModelPosition()));
            AddToPage(Page.View, CloneButton("Cofnij cięcia", () => session.ResetCutsAsync().Forget()));
        }

        /// <summary>
        /// Strona Obiekty nie ma stałej zawartości — wiersze powstają z bieżącej listy celów i
        /// zmieniają się w trakcie pracy (każde wydzielenie i pierwsze cięcie dokłada nowy obiekt),
        /// więc buduje ją dopiero RebuildObjectRows.
        /// </summary>
        private void RebuildObjectRows()
        {
            if (!isActiveAndEnabled || itemsParent == null) return;

            if (_pageItems.TryGetValue(Page.Objects, out var objectPage))
            {
                foreach (var row in _objectRows)
                {
                    if (row == null) continue;
                    objectPage.Remove(row);
                    Destroy(row);
                }
            }
            _objectRows.Clear();

            var targets = session.Targets;
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];

                // Kosz dostaje dodatkowo nałożenie na obiekt źródłowy — to jedyna operacja, która ma
                // sens wyłącznie dla niego (pokazuje, SKĄD dokładnie materiał został wycięty).
                var row = CloneButton(RowLabel(target), null);
                var button = row.GetComponent<PressableButton>();
                button.ToggleMode = MixedReality.Toolkit.StatefulInteractable.ToggleType.Toggle;
                button.ForceSetToggled(target.Visible, false);
                button.OnClicked.AddListener(() =>
                {
                    session.SetTargetVisible(target, button.IsToggled);
                    SetLabel(row, RowLabel(target));
                });

                _objectRows.Add(row);
                AddToPage(Page.Objects, row);

                if (target.IsCutBin)
                {
                    var alignRow = CloneButton(AlignLabel(target), null);
                    var alignButton = alignRow.GetComponent<PressableButton>();
                    alignButton.OnClicked.AddListener(() =>
                    {
                        session.SetBinAligned(target, !session.IsBinAligned(target));
                        SetLabel(alignRow, AlignLabel(target));
                    });

                    _objectRows.Add(alignRow);
                    AddToPage(Page.Objects, alignRow);
                }
            }

            ShowPage(_page);
        }

        private static string RowLabel(Helpers.VolumeRenderTarget target) =>
            (target.Visible ? "◉ " : "○ ") + target.DisplayName;

        private string AlignLabel(Helpers.VolumeRenderTarget bin) =>
            session.IsBinAligned(bin) ? "   ↤ odsuń" : "   ↦ nałóż";

        #endregion

        // ------------------------------------------------------------------
        #region Strony

        private void AddToPage(Page page, GameObject item)
        {
            if (!_pageItems.TryGetValue(page, out var list))
            {
                list = new List<GameObject>();
                _pageItems[page] = list;
            }
            list.Add(item);
        }

        private void CyclePage()
        {
            int next = ((int)_page + 1) % PageNames.Length;
            ShowPage((Page)next);
        }

        private void ShowPage(Page page)
        {
            _page = page;

            foreach (var pair in _pageItems)
            {
                bool visible = pair.Key == page;
                foreach (var item in pair.Value)
                {
                    if (item == null) continue;
                    item.SetActive(visible);
                }
            }

            // Przycisk pokazuje stronę, na której WŁAŚNIE jesteśmy — strzałka mówi, że kliknięcie
            // przeniesie dalej. Nazwa następnej strony byłaby myląca: nie wiadomo, czy to podpis
            // bieżącej zawartości, czy zapowiedź.
            SetLabel(_pageButton, PageNames[(int)page] + " ▸");
        }

        #endregion

        // ------------------------------------------------------------------
        #region Klonowanie elementów MRTK

        private GameObject CloneButton(string label, Action onClick)
        {
            var go = Instantiate(buttonTemplate, itemsParent);
            go.name = "Btn_" + label;
            go.SetActive(true);

            var button = go.GetComponent<PressableButton>();
            if (button != null)
            {
                ClearPersistentListeners(button.OnClicked);
                button.OnClicked.RemoveAllListeners();
                if (onClick != null) button.OnClicked.AddListener(() => onClick());
            }

            SetLabel(go, label);
            return go;
        }

        private Slider CloneSlider(string label, float min, float max, float value,
                                   Action<float> onChanged, out GameObject instance)
        {
            instance = null;
            if (sliderTemplate == null)
            {
                Debug.LogWarning($"[HandMenuController] Brak suwaka-wzorca — pomijam suwak „{label}”. " +
                                 "Wskaż Slider Template w Inspektorze.");
                return null;
            }

            var go = Instantiate(sliderTemplate, itemsParent);
            go.name = "Slider_" + label;
            go.SetActive(true);
            instance = go;

            var slider = go.GetComponent<Slider>();
            if (slider == null) return null;

            ClearPersistentListeners(slider.OnValueUpdated);
            slider.OnValueUpdated.RemoveAllListeners();

            slider.MinValue = min;
            slider.MaxValue = max;
            slider.Value = Mathf.Clamp(value, min, max);
            slider.OnValueUpdated.AddListener(data => onChanged?.Invoke(data.NewValue));

            SetLabel(go, label);
            return slider;
        }

        /// <summary>
        /// Wyłącza wywołania podpięte w Inspektorze. Wzorcem jest element, który STOI w scenie i ma
        /// już swoje podpięcia (np. przycisk wyśrodkowania modelu) — bez tego każdy klon odpalałby
        /// przy okazji akcję oryginału. RemoveAllListeners sam nie wystarcza: usuwa wyłącznie
        /// wywołania dodane z kodu, nie te zapisane w scenie.
        /// </summary>
        private static void ClearPersistentListeners(UnityEventBase unityEvent)
        {
            if (unityEvent == null) return;
            for (int i = 0; i < unityEvent.GetPersistentEventCount(); i++)
                unityEvent.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        private static void SetLabel(GameObject item, string text)
        {
            if (item == null) return;
            var label = item.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = text;
        }

        #endregion

        // ------------------------------------------------------------------
        #region Synchronizacja z drugą warstwą

        private void HandleToolModeChanged(Helpers.ToolMode mode)
        {
            if (_toolButtons == null) return;

            _suppressCallbacks = true;
            int active = Array.IndexOf(ToolOrder, mode);
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                if (_toolButtons[i] == null) continue;
                // fireEvents=false: to jest odzwierciedlenie zmiany, która JUŻ się stała (być może
                // w panelu na monitorze) — odesłanie jej z powrotem zapętliłoby obie warstwy.
                _toolButtons[i].ForceSetToggled(i == active, false);
            }
            _suppressCallbacks = false;
        }

        private void HandleBrushRadiusChanged(float radiusMM)
        {
            if (_brushSlider == null) return;
            _suppressCallbacks = true;
            _brushSlider.Value = Mathf.Clamp(radiusMM, _brushSlider.MinValue, _brushSlider.MaxValue);
            _suppressCallbacks = false;
        }

        private void Apply(Action change)
        {
            if (_suppressCallbacks) return;
            change();
        }

        #endregion
    }
}
