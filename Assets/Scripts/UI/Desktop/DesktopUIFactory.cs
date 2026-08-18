using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SkullXrRendererNKR.UI.Desktop
{
    /// <summary>
    /// Buduje elementy interfejsu na monitorze z kodu, zamiast wymagać ręcznego złożenia dziesiątek
    /// obiektów w Edytorze. Świadoma decyzja, inna niż przy menu w goglach (tam korzystamy z gotowych
    /// prefabów MRTK, bo mają dopracowaną interakcję dłonią): warstwa na monitorze to zwykłe suwaki i
    /// przyciski, których jest DUŻO i które będą się jeszcze zmieniać — utrzymywanie ich jako ręcznie
    /// poklikanej hierarchii kosztowałoby więcej niż jest warte, a każda zmiana układu wymagałaby
    /// przechodzenia po scenie zamiast edycji jednego pliku.
    ///
    /// Wszystkie kolory i rozmiary są w jednym miejscu (Palette), żeby wygląd dało się zmienić globalnie.
    /// </summary>
    public static class DesktopUIFactory
    {
        public static class Palette
        {
            public static readonly Color Background   = new Color(0.09f, 0.10f, 0.12f, 0.96f);
            public static readonly Color Panel        = new Color(0.14f, 0.15f, 0.18f, 1f);
            public static readonly Color PanelAlt     = new Color(0.18f, 0.19f, 0.23f, 1f);
            public static readonly Color Accent       = new Color(0.24f, 0.52f, 0.85f, 1f);
            public static readonly Color AccentDanger = new Color(0.78f, 0.29f, 0.26f, 1f);
            public static readonly Color Text         = new Color(0.92f, 0.93f, 0.95f, 1f);
            public static readonly Color TextDim      = new Color(0.62f, 0.65f, 0.70f, 1f);
        }

        public const float RowHeight = 34f;

        // ------------------------------------------------------------------

        /// <summary>Pełnoekranowy Canvas w przestrzeni ekranu (warstwa na monitorze, nie w goglach).</summary>
        public static Canvas CreateScreenCanvas(string name, int sortOrder, Transform parent = null)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            if (parent != null) go.transform.SetParent(parent, false);

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // 0.5 = kompromis między szerokością a wysokością: panel operatora jest zakotwiczony do
            // boku ekranu, więc nie może skalować się wyłącznie względem szerokości.
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        public static RectTransform CreateRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static Image CreatePanel(Transform parent, string name, Color color)
        {
            var rect = CreateRect(parent, name);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        /// <summary>Rozciąga element na całego rodzica (z opcjonalnym marginesem).</summary>
        public static RectTransform Stretch(RectTransform rect, float margin = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(margin, margin);
            rect.offsetMax = new Vector2(-margin, -margin);
            return rect;
        }

        public static VerticalLayoutGroup AddVerticalLayout(GameObject go, float spacing = 8f, int padding = 12)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childControlWidth = true;
            // childControlHeight = true jest tu KONIECZNE, nie kosmetyczne. Przy false rodzic bierze
            // dosłowną wysokość prostokąta dziecka — a gdy dziecko wylicza ją sobie samo (własny
            // układ albo ContentSizeFitter), rodzic czyta ją, zanim dziecko zdąży ją policzyć.
            // Efektem są elementy zwinięte do zera i wielka pusta przestrzeń tam, gdzie miały być.
            // Przy true rodzic PYTA dziecko o preferowaną wysokość, co działa niezależnie od tego,
            // w jakiej kolejności układy się przeliczają.
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return layout;
        }

        public static HorizontalLayoutGroup AddHorizontalLayout(GameObject go, float spacing = 8f, int padding = 0)
        {
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return layout;
        }

        /// <summary>
        /// Panel, którego wysokość wynika z zawartości, a nie jest z góry ustalona. Do treści, której
        /// długości nie da się przewidzieć — podsumowanie segmentacji potrafi mieć jedną linię albo
        /// kilkanaście, zależnie od tego, ile wysp znalazła maska, a przy stałej wysokości nadmiar
        /// wylewał się na kontrolki pod spodem.
        /// </summary>
        public static Image CreateAutoHeightPanel(Transform parent, string name, Color color, int padding = 10)
        {
            var image = CreatePanel(parent, name, color);

            // Sam układ pionowy wystarczy: jako element układu raportuje rodzicowi sumę wysokości
            // swoich dzieci, więc panel rośnie z treścią bez dokładania ContentSizeFittera (który
            // liczyłby to samo drugi raz i potrafi się z rodzicem rozjechać o jedną klatkę).
            AddVerticalLayout(image.gameObject, 0f, padding);
            return image;
        }

        public static LayoutElement SetHeight(GameObject go, float height)
        {
            var element = go.GetComponent<LayoutElement>();
            if (element == null) element = go.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            return element;
        }

        // ------------------------------------------------------------------

        public static TextMeshProUGUI CreateText(Transform parent, string content, float size = 16f,
                                                 FontStyles style = FontStyles.Normal,
                                                 TextAlignmentOptions align = TextAlignmentOptions.Left,
                                                 Color? color = null)
        {
            var rect = CreateRect(parent, "Text");
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = align;
            text.color = color ?? Palette.Text;
            text.richText = true;
            return text;
        }

        /// <summary>
        /// Akapit objaśnienia — CELOWO bez wymuszonej wysokości. Opisy mają różną długość i zmieniają
        /// się przy tłumaczeniu czy przeredagowaniu; wpisana na sztywno wysokość albo ucina ostatni
        /// wiersz, albo zostawia pustą dziurę. TextMeshPro sam raportuje układowi, ile miejsca
        /// potrzebuje przy danej szerokości.
        /// </summary>
        public static TextMeshProUGUI CreateParagraph(Transform parent, string content, float size = 12f)
        {
            var text = CreateText(parent, content, size, FontStyles.Italic,
                                  TextAlignmentOptions.TopLeft, Palette.TextDim);
            text.enableWordWrapping = true;
            return text;
        }

        public static Button CreateButton(Transform parent, string label, UnityAction onClick,
                                          Color? color = null, float height = RowHeight)
        {
            var image = CreatePanel(parent, "Button_" + label, color ?? Palette.PanelAlt);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            // Widoczna różnica stanu bez podmiany sprite'ów — sam kolor, bo cały panel jest płaski.
            // colorMultiplier = 2 i stany wokół 0.5 pozwalają ROZJAŚNIĆ przycisk pod kursorem: gotowy
            // kolor po pomnożeniu i tak jest obcinany do 1, więc wartości powyżej 1 nic by nie dały.
            var colors = button.colors;
            colors.colorMultiplier = 2f;
            colors.normalColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            colors.highlightedColor = new Color(0.62f, 0.62f, 0.62f, 1f);
            colors.pressedColor = new Color(0.42f, 0.42f, 0.42f, 1f);
            colors.selectedColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            colors.disabledColor = new Color(0.32f, 0.32f, 0.32f, 0.6f);
            button.colors = colors;

            if (onClick != null) button.onClick.AddListener(onClick);

            var text = CreateText(image.transform, label, 15f, FontStyles.Normal, TextAlignmentOptions.Center);
            Stretch((RectTransform)text.transform, 6f);

            SetHeight(image.gameObject, height);
            return button;
        }

        public static TMP_InputField CreateInputField(Transform parent, string placeholder, float height = RowHeight)
        {
            var image = CreatePanel(parent, "Input", Palette.PanelAlt);
            var input = image.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = image;

            // TMP_InputField wymaga maski obszaru tekstu — bez niej tekst wychodzi poza ramkę pola.
            var viewport = CreateRect(image.transform, "TextArea");
            Stretch(viewport, 8f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var text = CreateText(viewport, "", 15f);
            Stretch((RectTransform)text.transform);

            var hint = CreateText(viewport, placeholder, 15f, FontStyles.Italic,
                                  TextAlignmentOptions.Left, Palette.TextDim);
            Stretch((RectTransform)hint.transform);

            input.textViewport = viewport;
            input.textComponent = text;
            input.placeholder = hint;
            input.lineType = TMP_InputField.LineType.SingleLine;

            SetHeight(image.gameObject, height);
            return input;
        }

        // ------------------------------------------------------------------
        #region Kontrolki panelu operatora

        /// <summary>
        /// Suwak z podpisem i odczytem bieżącej wartości. Zwracany obiekt pozwala zaktualizować
        /// pozycję suwaka BEZ wywołania zdarzenia zmiany — to jest kluczowe przy dwóch warstwach
        /// interfejsu naraz: gdy wartość zmieni menu na dłoni, panel na monitorze ma tylko podążyć,
        /// a nie odesłać tej samej zmiany z powrotem (i tak w kółko).
        /// </summary>
        public class SliderControl
        {
            public Slider Slider;
            public TextMeshProUGUI ValueLabel;
            public string Format = "0.##";
            public string Unit = "";

            /// <summary>
            /// Opcjonalna dekoracja odczytu wartości — wołana również przy SetValueWithoutNotify,
            /// czyli wtedy, gdy wartość zmieniła druga warstwa interfejsu. Zdarzenie suwaka wtedy
            /// nie leci, więc bez tego suwak barwy pokazywałby kolor sprzed synchronizacji.
            /// </summary>
            public System.Action<float, TextMeshProUGUI> Decorator;

            public void SetValueWithoutNotify(float value)
            {
                if (Slider != null) Slider.SetValueWithoutNotify(value);
                RefreshLabel(value);
            }

            public void RefreshLabel(float value)
            {
                if (ValueLabel == null) return;
                ValueLabel.text = value.ToString(Format) + Unit;
                Decorator?.Invoke(value, ValueLabel);
            }
        }

        public static SliderControl CreateSlider(Transform parent, string label, float min, float max,
                                                 float value, UnityAction<float> onChanged,
                                                 string format = "0.##", string unit = "",
                                                 bool wholeNumbers = false)
        {
            var row = CreateRect(parent, "Slider_" + label);
            SetHeight(row.gameObject, 46f);

            var caption = CreateText(row, label, 14f, FontStyles.Normal, TextAlignmentOptions.TopLeft,
                                     Palette.TextDim);
            var captionRect = (RectTransform)caption.transform;
            captionRect.anchorMin = new Vector2(0f, 1f);
            captionRect.anchorMax = new Vector2(0.7f, 1f);
            captionRect.pivot = new Vector2(0f, 1f);
            captionRect.offsetMin = new Vector2(0f, -18f);
            captionRect.offsetMax = Vector2.zero;

            var valueLabel = CreateText(row, "", 14f, FontStyles.Bold, TextAlignmentOptions.TopRight);
            var valueRect = (RectTransform)valueLabel.transform;
            valueRect.anchorMin = new Vector2(0.7f, 1f);
            valueRect.anchorMax = new Vector2(1f, 1f);
            valueRect.pivot = new Vector2(1f, 1f);
            valueRect.offsetMin = new Vector2(0f, -18f);
            valueRect.offsetMax = Vector2.zero;

            // --- właściwy suwak (hierarchia wymagana przez UnityEngine.UI.Slider) ---
            var sliderRect = CreateRect(row, "Slider");
            sliderRect.anchorMin = new Vector2(0f, 0f);
            sliderRect.anchorMax = new Vector2(1f, 0f);
            sliderRect.pivot = new Vector2(0.5f, 0f);
            sliderRect.offsetMin = new Vector2(0f, 2f);
            sliderRect.offsetMax = new Vector2(0f, 18f);

            var slider = sliderRect.gameObject.AddComponent<Slider>();

            var background = CreatePanel(sliderRect, "Background", Palette.PanelAlt);
            var backgroundRect = (RectTransform)background.transform;
            backgroundRect.anchorMin = new Vector2(0f, 0.28f);
            backgroundRect.anchorMax = new Vector2(1f, 0.72f);
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            var fillArea = CreateRect(sliderRect, "Fill Area");
            fillArea.anchorMin = new Vector2(0f, 0.28f);
            fillArea.anchorMax = new Vector2(1f, 0.72f);
            fillArea.offsetMin = Vector2.zero;
            fillArea.offsetMax = Vector2.zero;
            var fill = CreatePanel(fillArea, "Fill", Palette.Accent);
            var fillRect = (RectTransform)fill.transform;
            fillRect.sizeDelta = Vector2.zero;

            var handleArea = CreateRect(sliderRect, "Handle Slide Area");
            Stretch(handleArea);
            var handle = CreatePanel(handleArea, "Handle", Palette.Text);
            var handleRect = (RectTransform)handle.transform;
            handleRect.sizeDelta = new Vector2(12f, 0f);

            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = wholeNumbers;
            slider.minValue = min;
            slider.maxValue = max;
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));

            var control = new SliderControl { Slider = slider, ValueLabel = valueLabel, Format = format, Unit = unit };
            control.RefreshLabel(slider.value);

            slider.onValueChanged.AddListener(v =>
            {
                control.RefreshLabel(v);
                onChanged?.Invoke(v);
            });

            return control;
        }

        /// <summary>
        /// Suwak wyboru barwy: tłem jest pełne widmo odcieni, a odczyt wartości jest pisany wybranym
        /// kolorem. Sama liczba 0..1 nic nie mówi o tym, jaki to kolor — trzeba było przesuwać suwak
        /// i patrzeć na model, żeby cokolwiek trafić. Tutaj widać docelową barwę zanim się puści suwak.
        ///
        /// Nasycenie i jasność są ustalone (patrz LoadDicomData.SetVesselColorLowHue/HighHue), więc
        /// pełny wybierak koloru byłby wyborem po wymiarach, których i tak nie da się zmienić.
        /// </summary>
        public static SliderControl CreateHueSlider(Transform parent, string label, float value,
                                                    UnityAction<float> onChanged, float saturation = 1f)
        {
            var control = CreateSlider(parent, label, 0f, 1f, value, onChanged, "0.00");

            var background = control.Slider.transform.Find("Background")?.GetComponent<Image>();
            if (background != null)
            {
                background.sprite = HueGradient();
                background.color = Color.white;
                background.type = Image.Type.Simple;
            }

            // Wypełnienie zasłoniłoby lewą część widma jednolitym kolorem akcentu — a to właśnie
            // widmo jest tu informacją. Pozycję pokazuje sam uchwyt.
            if (control.Slider.fillRect != null)
            {
                var fill = control.Slider.fillRect.GetComponent<Image>();
                if (fill != null) fill.color = new Color(1f, 1f, 1f, 0f);
            }

            control.Decorator = (hue, label2) => label2.color = Color.HSVToRGB(Mathf.Clamp01(hue), saturation, 1f);
            control.RefreshLabel(control.Slider.value);
            return control;
        }

        private static Sprite _hueGradient;

        /// <summary>Pasek pełnego widma odcieni — tworzony raz i współdzielony przez wszystkie suwaki barw.</summary>
        private static Sprite HueGradient()
        {
            if (_hueGradient != null) return _hueGradient;

            const int width = 256;
            var texture = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "HueGradient"
            };

            var pixels = new Color[width];
            for (int i = 0; i < width; i++)
                pixels[i] = Color.HSVToRGB(i / (float)(width - 1), 1f, 1f);
            texture.SetPixels(pixels);
            texture.Apply();

            _hueGradient = Sprite.Create(texture, new Rect(0f, 0f, width, 1f), new Vector2(0.5f, 0.5f));
            return _hueGradient;
        }

        public static Toggle CreateToggle(Transform parent, string label, bool value, UnityAction<bool> onChanged)
        {
            var row = CreatePanel(parent, "Toggle_" + label, new Color(0f, 0f, 0f, 0f));
            SetHeight(row.gameObject, 28f);

            var toggle = row.gameObject.AddComponent<Toggle>();

            var box = CreatePanel(row.transform, "Box", Palette.PanelAlt);
            var boxRect = (RectTransform)box.transform;
            boxRect.anchorMin = new Vector2(0f, 0.5f);
            boxRect.anchorMax = new Vector2(0f, 0.5f);
            boxRect.pivot = new Vector2(0f, 0.5f);
            boxRect.sizeDelta = new Vector2(20f, 20f);

            var check = CreatePanel(box.transform, "Check", Palette.Accent);
            Stretch((RectTransform)check.transform, 4f);

            var caption = CreateText(row.transform, label, 14f);
            var captionRect = (RectTransform)caption.transform;
            captionRect.anchorMin = new Vector2(0f, 0f);
            captionRect.anchorMax = new Vector2(1f, 1f);
            captionRect.offsetMin = new Vector2(28f, 0f);
            captionRect.offsetMax = Vector2.zero;
            caption.alignment = TextAlignmentOptions.Left;

            toggle.targetGraphic = box;
            toggle.graphic = check;
            toggle.SetIsOnWithoutNotify(value);
            if (onChanged != null) toggle.onValueChanged.AddListener(onChanged);

            return toggle;
        }

        /// <summary>
        /// Wybór jednej z kilku opcji jako rząd przycisków — świadomie zamiast rozwijanej listy.
        /// Opcji jest tu zawsze kilka (jakość renderowania, tryb narzędzia), a widoczny na stałe stan
        /// jest wart miejsca: przy dwóch warstwach interfejsu operator musi jednym spojrzeniem
        /// widzieć, co jest wybrane, bez rozwijania czegokolwiek.
        /// </summary>
        public class SegmentedControl
        {
            public Button[] Buttons;
            public int Index { get; private set; }

            public void SetIndexWithoutNotify(int index)
            {
                Index = index;
                for (int i = 0; i < Buttons.Length; i++)
                {
                    var image = Buttons[i].targetGraphic as Image;
                    if (image != null) image.color = i == index ? Palette.Accent : Palette.PanelAlt;
                }
            }
        }

        public static SegmentedControl CreateSegmented(Transform parent, string label, string[] options,
                                                       int index, UnityAction<int> onChanged)
        {
            if (!string.IsNullOrEmpty(label))
            {
                var caption = CreateText(parent, label, 14f, FontStyles.Normal,
                                         TextAlignmentOptions.Left, Palette.TextDim);
                SetHeight(caption.gameObject, 20f);
            }

            var row = CreateRect(parent, "Segmented");
            AddHorizontalLayout(row.gameObject, 4f);
            SetHeight(row.gameObject, RowHeight);

            var control = new SegmentedControl { Buttons = new Button[options.Length] };
            for (int i = 0; i < options.Length; i++)
            {
                int captured = i;
                control.Buttons[i] = CreateButton(row, options[i], () =>
                {
                    control.SetIndexWithoutNotify(captured);
                    onChanged?.Invoke(captured);
                });
                control.Buttons[i].GetComponentInChildren<TextMeshProUGUI>().fontSize = 13f;
            }

            control.SetIndexWithoutNotify(index);
            return control;
        }

        public static TextMeshProUGUI CreateSectionHeader(Transform parent, string title)
        {
            var header = CreateText(parent, title, 17f, FontStyles.Bold);
            SetHeight(header.gameObject, 28f);
            return header;
        }

        /// <summary>Cienka pozioma linia — rozdziela grupy ustawień bez dokładania podpisów.</summary>
        public static Image CreateSeparator(Transform parent)
        {
            var line = CreatePanel(parent, "Separator", new Color(1f, 1f, 1f, 0.08f));
            SetHeight(line.gameObject, 1f);
            return line;
        }

        #endregion

        // ------------------------------------------------------------------

        /// <summary>
        /// Poziomy pasek postępu. Zwraca obraz wypełnienia — postęp ustawia się przez jego
        /// fillAmount (0..1).
        /// </summary>
        public static Image CreateProgressBar(Transform parent, float height = 14f)
        {
            var track = CreatePanel(parent, "ProgressTrack", Palette.PanelAlt);
            SetHeight(track.gameObject, height);

            var fill = CreatePanel(track.transform, "ProgressFill", Palette.Accent);
            Stretch((RectTransform)fill.transform, 2f);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            return fill;
        }

        /// <summary>Pionowa lista przewijana — zwraca kontener, do którego dodaje się wiersze.</summary>
        public static RectTransform CreateScrollList(Transform parent, string name, out ScrollRect scrollRect)
        {
            var viewport = CreatePanel(parent, name, new Color(0f, 0f, 0f, 0.25f));
            scrollRect = viewport.gameObject.AddComponent<ScrollRect>();
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = CreateRect(viewport.transform, "Content");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, 0f);

            AddVerticalLayout(content.gameObject, 4f, 6);
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = content;
            scrollRect.viewport = (RectTransform)viewport.transform;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            return content;
        }
    }
}
