using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SkullXrRendererNKR.UI.Desktop
{
    /// <summary>
    /// Wyzwalacz dymku — dodawany przez DesktopUIFactory.AddTooltip. Sam nic nie rysuje, tylko
    /// zgłasza wspólnemu dymkowi, nad czym jest kursor.
    /// </summary>
    public class DesktopTooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [TextArea] public string Text;

        public void OnPointerEnter(PointerEventData eventData) => DesktopTooltip.Show(Text);
        public void OnPointerExit(PointerEventData eventData) => DesktopTooltip.Hide(Text);

        // Przełączenie zakładki chowa element pod kursorem bez zdarzenia wyjścia — dymek zostałby
        // wtedy zawieszony nad pustym miejscem.
        private void OnDisable() => DesktopTooltip.Hide(Text);
    }

    /// <summary>
    /// Jeden wspólny dymek z objaśnieniem dla całego interfejsu na monitorze. Zastępuje długie opisy,
    /// które dotąd stały na stałe pod kontrolkami: objaśnienie jest pod ręką, ale nie zajmuje miejsca.
    ///
    /// Własny Canvas na samym wierzchu i BEZ GraphicRaycaster — dymek nigdy nie przechwytuje kliknięć
    /// ani nie zasłania kontrolki, nad którą wisi, przed promieniem myszy.
    /// </summary>
    public class DesktopTooltip : MonoBehaviour
    {
        private const float ShowDelay = 0.35f;
        private const float MaxWidth = 340f;
        private const float Padding = 8f;
        private static readonly Vector2 CursorOffset = new Vector2(16f, -20f);

        private static DesktopTooltip _instance;

        private RectTransform _canvasRect;
        private RectTransform _box;
        private TextMeshProUGUI _text;
        private string _current;
        private float _showAt;

        private static DesktopTooltip Instance
        {
            get
            {
                if (_instance == null) Create();
                return _instance;
            }
        }

        private static void Create()
        {
            var canvas = DesktopUIFactory.CreateScreenCanvas("TooltipCanvas", 1000);
            Destroy(canvas.GetComponent<GraphicRaycaster>());

            _instance = canvas.gameObject.AddComponent<DesktopTooltip>();
            _instance._canvasRect = (RectTransform)canvas.transform;

            var box = DesktopUIFactory.CreatePanel(canvas.transform, "Tooltip", new Color(0.04f, 0.04f, 0.05f, 0.96f));
            box.raycastTarget = false;
            _instance._box = (RectTransform)box.transform;
            _instance._box.anchorMin = new Vector2(0.5f, 0.5f);
            _instance._box.anchorMax = new Vector2(0.5f, 0.5f);

            _instance._text = DesktopUIFactory.CreateText(box.transform, "", 13f, FontStyles.Normal,
                                                          TextAlignmentOptions.TopLeft);
            _instance._text.enableWordWrapping = true;
            _instance._text.raycastTarget = false;
            DesktopUIFactory.Stretch((RectTransform)_instance._text.transform, Padding);

            box.gameObject.SetActive(false);
        }

        public static void Show(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var tip = Instance;
            tip.SetContent(text);
            tip._showAt = Time.unscaledTime + ShowDelay;
        }

        /// <summary>Jak Show, ale bez opóźnienia — dla przycisku „?”.</summary>
        public static void ShowNow(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var tip = Instance;
            tip.SetContent(text);
            tip._showAt = 0f;
        }

        /// <summary>Chowa dymek, ale tylko jeśli wciąż pokazuje TEN tekst — sąsiedni element mógł go już przejąć.</summary>
        public static void Hide(string text)
        {
            if (_instance == null || _instance._current != text) return;
            _instance._current = null;
            if (_instance._box != null) _instance._box.gameObject.SetActive(false);
        }

        private void SetContent(string text)
        {
            _current = text;
            _text.text = text;

            // Szerokość z treści, ale najwyżej MaxWidth — krótkie podpowiedzi nie dostają szerokiej ramki.
            float width = Mathf.Min(_text.GetPreferredValues(text, MaxWidth, 0f).x, MaxWidth);
            float height = _text.GetPreferredValues(text, width, 0f).y;
            _box.sizeDelta = new Vector2(width + 2f * Padding, height + 2f * Padding);
            _box.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_current == null || Time.unscaledTime < _showAt || Mouse.current == null) return;
            if (!_box.gameObject.activeSelf) _box.gameObject.SetActive(true);

            Vector2 screen = Mouse.current.position.ReadValue();
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local))
                return;

            // Przy krawędzi ekranu dymek przechodzi na drugą stronę kursora, zamiast z niego wychodzić.
            Vector2 half = _canvasRect.rect.size * 0.5f;
            Vector2 size = _box.sizeDelta;
            bool flipX = local.x + CursorOffset.x + size.x > half.x;
            bool flipY = local.y + CursorOffset.y - size.y < -half.y;
            _box.pivot = new Vector2(flipX ? 1f : 0f, flipY ? 0f : 1f);
            _box.anchoredPosition = local + new Vector2(flipX ? -CursorOffset.x : CursorOffset.x,
                                                        flipY ? -CursorOffset.y : CursorOffset.y);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
