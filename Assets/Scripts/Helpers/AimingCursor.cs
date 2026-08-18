using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Pierścień pokazujący, GDZIE i JAK SZEROKO zadziała narzędzie — rysowany na powierzchni
    /// modelu w miejscu, w które celuje promień z ręki.
    ///
    /// Powód istnienia: na HoloLens 2 promień z dłoni drży o ok. 1-2 cm na metr, a struktury, które
    /// trzeba trafić, mają milimetry. Bez widocznego kursora użytkownik dowiaduje się, co złapał,
    /// dopiero PO kliknięciu — stąd wrażenie, że „ciężko chwycić określoną wyspę”. Z pierścieniem
    /// celowanie przestaje być zgadywanką: widać obszar działania i można skorygować rękę wcześniej.
    ///
    /// Promień pierścienia odpowiada realnemu zasięgowi pędzla, więc rozmiar cięcia przestaje być
    /// liczbą w milimetrach, którą trzeba sobie wyobrazić.
    /// </summary>
    public class AimingCursor : MonoBehaviour
    {
        private const int Segments = 48;

        private LineRenderer _ring;
        private Transform _root;
        private bool _visible;

        /// <summary>Kolory niosące informację: co się stanie po naciśnięciu.</summary>
        public static readonly Color ColorPick = new Color(0.35f, 0.9f, 0.5f, 0.95f);   // wskazywanie
        public static readonly Color ColorCut = new Color(1f, 0.45f, 0.35f, 0.95f);     // cięcie
        public static readonly Color ColorErase = new Color(0.4f, 0.75f, 1f, 0.95f);    // przywracanie
        public static readonly Color ColorInactive = new Color(0.6f, 0.6f, 0.6f, 0.5f); // brak celu

        public static AimingCursor Create(Transform parent)
        {
            var go = new GameObject("AimingCursor");
            go.transform.SetParent(parent, false);

            var cursor = go.AddComponent<AimingCursor>();
            cursor.Build();
            return cursor;
        }

        private void Build()
        {
            _root = transform;

            _ring = gameObject.AddComponent<LineRenderer>();
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null) _ring.material = new Material(shader);

            _ring.useWorldSpace = false;   // punkty w lokalnych — obrót i pozycja idą przez transform
            _ring.loop = true;
            _ring.positionCount = Segments;
            _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ring.receiveShadows = false;
            _ring.alignment = LineAlignment.TransformZ;

            var points = new Vector3[Segments];
            for (int i = 0; i < Segments; i++)
            {
                float a = i / (float)Segments * Mathf.PI * 2f;
                points[i] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            }
            _ring.SetPositions(points);

            SetVisible(false);
        }

        /// <summary>
        /// Ustawia kursor na trafionym punkcie. `normal` to normalna powierzchni — pierścień leży
        /// na niej płasko, więc czytelnie pokazuje nachylenie miejsca, w które celujemy.
        /// `worldRadius` jest już przeliczonym zasięgiem narzędzia w jednostkach świata.
        /// </summary>
        public void Show(Vector3 worldPoint, Vector3 normal, float worldRadius, Color color)
        {
            if (_ring == null) return;

            SetVisible(true);

            _root.position = worldPoint + normal * 0.001f; // minimalne odsunięcie, żeby nie z-fightować
            _root.rotation = Quaternion.LookRotation(normal);
            _root.localScale = Vector3.one * Mathf.Max(worldRadius, 0.0005f);

            _ring.startColor = _ring.endColor = color;
            // Grubość proporcjonalna do promienia: stała wartość znikałaby przy dużym pędzlu
            // i zasłaniała cel przy małym.
            _ring.widthMultiplier = 0.06f;
        }

        public void Hide() => SetVisible(false);

        private void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;
            if (_ring != null) _ring.enabled = visible;
        }
    }
}
