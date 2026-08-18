using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;

namespace Helpers
{
    public enum ToolMode
    {
        Picker,
        Cut,
        RemoveIsland,
        TunnelCut, // Wycina "na wylot" wzdłuż promienia patrzenia w miejscu dotknięcia — jedno kliknięcie = od razu trwałe cięcie.
        Inspect // Diagnostyka: klik loguje surowe HU/etykietę/stan cięcia w miejscu kliknięcia, NIC nie zmienia.
    }

    /// <summary>
    /// Router wejścia (mysz + XR) dla narzędzi Cut/Pick/RemoveIsland/TunnelCut/Inspect — działa na
    /// DOWOLNYM zarejestrowanym obiekcie wolumetrycznym (główny wolumen ORAZ każdy wydzielony kawałek,
    /// patrz VolumeObjectManager), niezależnie i BEZ osobnego przełącznika: który obiekt jest celem
    /// narzędzia decyduje wyłącznie to, w który collider trafia promień/mysz w danej klatce
    /// (TryResolveTarget). Cięcie/malowanie zawsze mutuje TE SAME współdzielone dane (pieceOwnerMask,
    /// _volumeHu) niezależnie od tego, który obiekt był celem — jedyne co się zmienia per-obiekt to
    /// transform użyty do rozwiązania promienia na woksel (patrz VolumeRenderTarget/
    /// VolumeSpaceTransform.SubLocalToOriginalLocal).
    /// </summary>
    public class VolumePicker : MonoBehaviour
    {
        [Tooltip("Podepnij główny skrypt ładujący dane, lub zostaw puste (znajdzie go automatycznie)")]
        public LoadDicomData dicomDataRef;
        private LoadDicomData _dicomData;

        [Tooltip("Podepnij VolumeObjectManager, lub zostaw puste (znajdzie go automatycznie)")]
        public VolumeObjectManager volumeObjectManagerRef;
        private VolumeObjectManager _objectManager;

        private Camera _mainCamera;

        // Rozstrzyganie w który zarejestrowany obiekt trafił dany collider (raycasty myszy/fizyki).
        private readonly Dictionary<Collider, VolumeRenderTarget> _colliderToTarget = new Dictionary<Collider, VolumeRenderTarget>();
        private readonly RaycastHit[] _raycastBuffer = new RaycastHit[16];

        // Ciągłe malowanie (trzymanie/przeciąganie pędzla, XR) — JEDNA aktywna "ręka"/interactor maluje
        // NA JEDNYM obiekcie naraz, ustalanym w momencie chwycenia BrushProxy tego obiektu.
        private UnityEngine.XR.Interaction.Toolkit.Interactors.IXRSelectInteractor _activeInteractor;
        private VolumeRenderTarget _activeBrushTarget;

        private bool _isRegenerating = false;

        [Header("Tools Settings")]
        public ToolMode CurrentMode = ToolMode.Picker;

        [Tooltip("Rozmiar pędzla (w mm) dla narzędzia Cut oraz promień otworu dla TunnelCut")]
        public float BrushRadiusMM = 5f;

        [Tooltip("Próg HU (osobny od 'Morph Threshold HU' w LoadDicomData!) decydujący co Cut/TunnelCut w ogóle mogą trafić promieniem. 'Morph Threshold HU' dobrze ustawiony wysoko (np. 300) daje CZYSTĄ segmentację kości do Pick/RemoveIsland — ale wtedy Cut nie mógłby dotknąć niżej gęstej tkanki/maski ze skanera (bo ta nigdy nie dostaje etykiety). Ten próg jest low domyślnie, żeby dało się wyciąć WSZYSTKO co widać w renderze, niezależnie od progu segmentacji.")]
        public float CutThresholdHU = -100f;

        [Tooltip("Maksymalna głębokość (mm) na jaką Cut może 'wwiercić się' w jednym miejscu, licząc od oryginalnej (nienaruszonej) powierzchni. Zabezpiecza przed przypadkowym przebiciem czaszki na wylot przy trzymaniu pędzla w jednym punkcie (np. żeby nie uszkodzić ważnej struktury, jak tętnica, tuż pod kością). TunnelCut (osobne narzędzie) celowo NIE ma tego limitu — jego zadaniem jest przecinać na wylot.")]
        public float MaxCutDepthMM = 12f;

        [Tooltip("Opóźnienie (ms) bezczynności pędzla zanim odświeży się pełna segmentacja (Pick/RemoveIsland). Samo wycinanie jest już natychmiastowe dzięki GPU, więc ta wartość NIE wpływa na płynność cięcia — tylko na to, kiedy w tle przeliczą się ID wysp.")]
        public int BrushUpdateDelayMs = 350;

        [Tooltip("Minimalny odstęp (ms) między kolejnymi tunelami przy przytrzymaniu TunnelCut. Tunel to cięższa operacja niż lokalny pędzel (skanuje cały bounding box na wylot), więc — inaczej niż Cut — NIE odpala się co klatkę, tylko w tym rytmie.")]
        public int TunnelCutIntervalMs = 120;

        private float _lastTunnelCutTime = -999f;

        private float _lastBrushTime = 0f;
        private bool _needsMorphologyUpdate = false;

        // Czy trwa pociągnięcie pędzlem zapisywane jako JEDEN krok historii cofania — patrz ApplyBrushAt.
        private bool _strokeOpen = false;

        // Cache dla wyników Raycast UI, aby nie tworzyć nowej listy co klatkę (zapobiega śmieceniu pamięci)
        private System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult> _uiRaycastResults = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();

        void Start()
        {
            _mainCamera = Camera.main;

            // Inteligentne szukanie referencji (żeby nie duplikować LoadDicomData)
            _dicomData = dicomDataRef;
            if (_dicomData == null) _dicomData = GetComponent<LoadDicomData>();
            if (_dicomData == null) _dicomData = FindObjectOfType<LoadDicomData>();

            if (_dicomData == null)
            {
                Debug.LogError("[VolumePicker] Nie znaleziono skryptu LoadDicomData w scenie!");
                return;
            }

            _objectManager = volumeObjectManagerRef;
            if (_objectManager == null) _objectManager = GetComponent<VolumeObjectManager>();
            if (_objectManager == null) _objectManager = FindObjectOfType<VolumeObjectManager>();

            if (_objectManager == null)
            {
                Debug.LogError("[VolumePicker] Nie znaleziono skryptu VolumeObjectManager w scenie!");
                return;
            }

            _objectManager.OnTargetRemoved += OnTargetRemoved;
        }

        /// <summary>
        /// Reset Cuts kasuje wszystkie kosze i wydzielone fragmenty — trzymana tu mapa collider→obiekt
        /// musi stracić te wpisy RAZEM z nimi. Bez tego zostawałyby klucze wskazujące na zniszczone
        /// komponenty Unity, a przy ponownym użyciu tych samych identyfikatorów przez nowe obiekty
        /// łatwo o pomyłkę co do tego, w co użytkownik właśnie celuje.
        /// </summary>
        private void OnTargetRemoved(VolumeRenderTarget target)
        {
            if (target?.Collider != null) _colliderToTarget.Remove(target.Collider);
            if (_activeBrushTarget == target) { _activeBrushTarget = null; _activeInteractor = null; }
        }

        private void OnDestroy()
        {
            // Jedyna subskrypcja na długożyjącym, współdzielonym evencie — musi być zdjęta jawnie.
            // Listenery na BrushProxy/ObjectManipulator per-target są dodawane z domknięciami (closures)
            // i giną razem z tymi obiektami, więc ich odsubskrybowanie nie jest potrzebne.
            if (_objectManager != null) _objectManager.OnTargetRemoved -= OnTargetRemoved;
        }

        /// <summary>
        /// Leniwie tworzy BrushProxy+XRSimpleInteractable i podpina listener ObjectManipulatora dla
        /// obiektu, który jeszcze ich nie ma — wywoływane co klatkę z Update() dla WSZYSTKICH
        /// zarejestrowanych targetów (idempotentne, patrz strażnik target.BrushProxy != null). Dzięki
        /// temu nowo wydzielony kawałek (VolumeObjectManager.SpawnPieceObject) staje się w pełni
        /// interaktywny (chwytalny + cięty) już w najbliższej klatce, bez potrzeby jawnej subskrypcji
        /// na zdarzenie "dodano obiekt".
        /// </summary>
        private void EnsureTargetInitialized(VolumeRenderTarget target)
        {
            if (target.BrushProxy != null) return;

            if (target.Collider != null) _colliderToTarget[target.Collider] = target;

            var proxy = new GameObject("BrushProxy");
            proxy.transform.SetParent(target.ProxyTransform, false);
            proxy.transform.localPosition = Vector3.zero;
            proxy.transform.localRotation = Quaternion.identity;
            proxy.transform.localScale = Vector3.one;

            var box = proxy.AddComponent<BoxCollider>();
            box.size = target.Collider != null ? target.Collider.size : Vector3.one;

            var interactable = proxy.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
            interactable.selectEntered.AddListener(args => OnSelectEnteredProxy(args, target));
            interactable.selectExited.AddListener(args => OnSelectExitedProxy(args, target));

            // Domyślnie wyłączone — pętla w Update() ustawi zgodnie z aktualnym trybem/widocznością.
            proxy.SetActive(false);

            target.BrushProxy = proxy;

            if (target.Manipulator != null)
                target.Manipulator.selectEntered.AddListener(args => OnSelectEnteredManipulator(args, target));

            Debug.Log($"[VolumePicker] Zainicjalizowano obiekt '{target.DisplayName}' (właściciel {target.OwnerId}).");
        }

        void Update()
        {
            if (_objectManager == null) return;
            var targets = _objectManager.Targets;
            if (targets.Count == 0) return;

            for (int i = 0; i < targets.Count; i++)
                EnsureTargetInitialized(targets[i]);

            // Przełączanie stanów (ObjectManipulator+BoundsControl vs BrushProxy) — PER OBIEKT, i
            // niewidoczne obiekty (VolumeObjectManager.SetVisible) są zawsze wyłączone z obu, żeby
            // schowany obiekt nie dał się złapać/wyceliować promieniem.
            bool isPicker = (CurrentMode == ToolMode.Picker);
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                // SuppressOwnHandle: Kosz "nałożony" na czaszkę (VolumeObjectManager.
                // SetCutBinAlignedToSkull) jest sparentowany pod nią i porusza/obraca się WYŁĄCZNIE
                // przez jej własny uchwyt — własny Manipulator/BoundsControl Kosza musi zostać
                // wyłączony, inaczej dwa nakładające się na siebie uchwyty myliłyby użytkownika.
                bool manipEnabled = isPicker && target.Visible && !target.SuppressOwnHandle;
                if (target.Manipulator != null && target.Manipulator.enabled != manipEnabled)
                    target.Manipulator.enabled = manipEnabled;
                if (target.BoundsControl != null && target.BoundsControl.enabled != manipEnabled)
                    target.BoundsControl.enabled = manipEnabled;
                // KONIECZNE osobno: BoundsControl.enabled=false NIE chowa wizualnych rączek — żyją
                // we własnym dziecku z własnym Update() i colliderami, a BoundsControl nie ma
                // OnDisable, który by je ruszał (patrz VolumeObjectManager.SpawnPieceObject). Bez
                // tego rączki schowanego albo nałożonego na czaszkę Kosza dalej wisiały w powietrzu
                // i przechwytywały chwyt, uniemożliwiając obracanie właściwego obiektu.
                if (target.BoundsVisuals != null && target.BoundsVisuals.activeSelf != manipEnabled)
                    target.BoundsVisuals.SetActive(manipEnabled);

                bool brushEnabled = !isPicker && target.Visible;
                if (target.BrushProxy != null && target.BrushProxy.activeSelf != brushEnabled)
                    target.BrushProxy.SetActive(brushEnabled);
            }

            // Ciągłe malowanie (trzymanie i przeciąganie) dotyczy trybów pędzla: Cut (wycinanie)
            // i RemoveIsland (trzymanie = ciągłe "przywracanie"/gumka) — te odpalają się co klatkę.
            // TunnelCut też wspiera przytrzymanie, ale throttlowane (TunnelCutIntervalMs), bo pojedynczy
            // tunel to znacznie cięższa operacja (skanuje bounding box na wylot) niż lokalny pędzel.
            if (_activeInteractor != null && _activeBrushTarget != null)
            {
                if (IsContinuousBrushMode(CurrentMode))
                {
                    ProcessXRBrushContinuous();
                }
                else if (CurrentMode == ToolMode.TunnelCut &&
                         Time.time - _lastTunnelCutTime >= Mathf.Max(TunnelCutIntervalMs, 0) / 1000f)
                {
                    ProcessXRTunnelContinuous();
                }
            }

            UpdateAimingCursor();

            HandleMouseInput();

            // UWAGA KOLEJNOŚCI: ten check MUSI być PO ProcessXRBrushContinuous()/HandleMouseInput().
            // _lastBrushTime jest odświeżany przy każdej modyfikacji wokseli powyżej — sprawdzając
            // "czy minęło BrushUpdateDelayMs" dopiero PO ewentualnym malowaniu w tej klatce,
            // ciągłe trzymanie pędzla nigdy nie wygląda na "bezczynność" i nie odpala sztormu
            // pełnych przeliczeń maski (dawny bug: przy delay=0 odpalało się to co klatkę).
            //
            // DODATKOWA STRAŻ (!IsBrushCurrentlyHeld()): przy POWOLNYM, precyzyjnym malowaniu
            // (np. dokładne obrysowywanie płata) naturalnie zdarzają się pauzy dłuższe niż
            // BrushUpdateDelayMs bez ŻADNEJ nowej edycji woksela, mimo że przycisk/kontroler
            // WCIĄŻ jest wciśnięty — sam upływ czasu bez tej straży i tak odpalał pełne
            // przeliczenie W TRAKCIE cięcia. Regeneracja ma czekać na faktyczne PUSZCZENIE
            // (obsłużone już osobno przez leftClickUp/rightClickUp i OnSelectExitedProxy).
            // Puszczenie pędzla kończy krok historii — ten sam moment, w którym „skończyło się
            // malowanie”, tylko bez czekania na opóźnienie segmentacji: krok ma być gotowy do
            // cofnięcia od razu, a nie dopiero po przeliczeniu maski.
            if (_strokeOpen && !IsBrushCurrentlyHeld() && _dicomData != null)
            {
                _dicomData.EditHistory.Commit();
                _strokeOpen = false;
            }

            if (_needsMorphologyUpdate && !_isRegenerating && !IsBrushCurrentlyHeld() &&
                Time.time - _lastBrushTime > Mathf.Max(BrushUpdateDelayMs, 0f) / 1000f)
            {
                _needsMorphologyUpdate = false;
                _isRegenerating = true;
                RegenerateMorphologyAsync().Forget();
            }
        }

        // ------------------------------------------------------------------
        #region Celowanie: kursor i stabilizacja

        [Header("Celowanie (XR)")]
        [Tooltip("Ile ostatnich klatek uśredniać przy wyznaczaniu punktu celowania. Promień z dłoni na HoloLens 2 drży o ok. 1-2 cm na metr; uśrednienie wycina to drżenie kosztem minimalnego opóźnienia. 1 = bez stabilizacji.")]
        [Range(1, 20)] public int AimSmoothingFrames = 8;

        [Tooltip("Pokazuj pierścień zasięgu narzędzia w miejscu celowania.")]
        public bool ShowAimingCursor = true;

        private AimingCursor _cursor;
        private readonly Vector3[] _aimSamples = new Vector3[20];
        private int _aimSampleCount;
        private int _aimSampleHead;

        /// <summary>
        /// Uśredniony punkt celowania. Bufor kołowy zamiast filtru wykładniczego, bo przy stałym
        /// oknie łatwiej przewidzieć opóźnienie: N klatek to N/60 sekundy i tyle.
        /// </summary>
        private Vector3 PushAimSample(Vector3 point)
        {
            int window = Mathf.Clamp(AimSmoothingFrames, 1, _aimSamples.Length);

            _aimSamples[_aimSampleHead] = point;
            _aimSampleHead = (_aimSampleHead + 1) % window;
            if (_aimSampleCount < window) _aimSampleCount++;

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < _aimSampleCount; i++) sum += _aimSamples[i];
            return sum / _aimSampleCount;
        }

        private void ResetAimSamples()
        {
            _aimSampleCount = 0;
            _aimSampleHead = 0;
        }

        /// <summary>
        /// Zasięg narzędzia przeliczony z milimetrów pacjenta na jednostki świata. Bez tego pierścień
        /// nie odpowiadałby temu, co faktycznie zostanie wycięte — a to jest cały jego sens.
        /// </summary>
        private float BrushWorldRadius(VolumeRenderTarget target)
        {
            if (_dicomData == null || target?.ProxyTransform == null) return 0.01f;

            float volumeWidthMM = Mathf.Max(_dicomData.Width * _dicomData.PixelSpacingX, 0.0001f);
            float worldPerMM = target.ProxyTransform.lossyScale.x / volumeWidthMM;
            return Mathf.Max(BrushRadiusMM * worldPerMM, 0.0005f);
        }

        /// <summary>
        /// Promień celowania — z interactora trzymającego pędzel, a gdy nic nie jest wciśnięte,
        /// z tego, który aktualnie wskazuje którykolwiek obiekt. Bez tej drugiej ścieżki kursor
        /// pojawiałby się dopiero po naciśnięciu, czyli wtedy, gdy jest już za późno na korektę.
        ///
        /// Na komputerze spada na promień z kamery przez pozycję myszy — panel operatora używa tej
        /// samej ścieżki celowania co gogle, więc podgląd działa w obu warstwach.
        /// </summary>
        private bool TryGetAimRay(out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;

            if (_activeInteractor is UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor active &&
                active.TryGetCurrent3DRaycastHit(out RaycastHit _))
            {
                origin = active.transform.position;
                direction = active.transform.forward;
                return true;
            }

            // Żaden obiekt nie jest chwycony — szukamy interactora, który cokolwiek wskazuje.
            if (_objectManager != null)
            {
                var targets = _objectManager.Targets;
                for (int i = 0; i < targets.Count; i++)
                {
                    var proxy = targets[i].BrushProxy;
                    if (proxy == null || !proxy.activeInHierarchy) continue;

                    var interactable = proxy.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
                    if (interactable == null || interactable.interactorsHovering.Count == 0) continue;

                    var hovering = interactable.interactorsHovering[0];
                    if (hovering?.transform == null) continue;

                    origin = hovering.transform.position;
                    direction = hovering.transform.forward;
                    return true;
                }
            }

            if (_mainCamera != null && UnityEngine.InputSystem.Mouse.current != null)
            {
                Vector2 mouse = UnityEngine.InputSystem.Mouse.current.position.ReadValue();
                Ray mouseRay = _mainCamera.ScreenPointToRay(mouse);
                origin = mouseRay.origin;
                direction = mouseRay.direction;
                return true;
            }

            return false;
        }

        private void UpdateAimingCursor()
        {
            if (!ShowAimingCursor || _dicomData == null)
            {
                _cursor?.Hide();
                return;
            }

            if (_cursor == null) _cursor = AimingCursor.Create(transform);

            // Celowanie bierzemy z interactora, który AKTUALNIE wskazuje — również gdy nic nie jest
            // wciśnięte. To jest istota podglądu: użytkownik ma widzieć cel, zanim naciśnie.
            if (!TryGetAimRay(out Vector3 origin, out Vector3 direction))
            {
                _cursor.Hide();
                ResetAimSamples();
                return;
            }

            if (!TryResolveTarget(new Ray(origin, direction), out RaycastHit hit, out VolumeRenderTarget target))
            {
                _cursor.Hide();
                ResetAimSamples();
                return;
            }

            Vector3 smoothed = PushAimSample(hit.point);

            Color color = CurrentMode switch
            {
                ToolMode.Cut => AimingCursor.ColorCut,
                ToolMode.TunnelCut => AimingCursor.ColorCut,
                ToolMode.RemoveIsland => AimingCursor.ColorErase,
                ToolMode.Picker => AimingCursor.ColorPick,
                _ => AimingCursor.ColorInactive
            };

            _cursor.Show(smoothed, hit.normal, BrushWorldRadius(target), color);
        }

        #endregion

        private bool IsBrushCurrentlyHeld()
        {
            if (_activeInteractor != null) return true;
            if (Mouse.current != null && (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed)) return true;
            return false;
        }

        /// <summary>
        /// Czy trafiony woksel to PRAWDZIWA kość, a nie akcesorium przypadkiem podpięte pod etykietę kostną?
        /// Dwa niezależne mechanizmy potrafią dać temu samemu wokselowi etykietę kostną (label > 0) mimo że
        /// fizycznie to nie kość:
        /// (1) SZUM: przy nisko ustawionym Morph Threshold HU segmentacja generuje czasem tysiące
        ///     jednowoksowych "wysp" (patrz ostrzeżenie w RemapLabelsAsync) — filtrujemy po rozmiarze wyspy.
        /// (2) DOMALOWANE OBRZEŻE (Morph Expand Radius): sąsiedztwo prawdziwej kości "pożycza" jej etykietę
        ///     na 1+ wokseli w głąb tła, więc powierzchnia akcesorium stykającego/leżącego blisko kości może
        ///     dostać etykietę kostną, mimo że SAMA w tym miejscu ma gęstość dużo niższą niż próg — filtrujemy
        ///     po surowym HU klikniętego woksela (nie tym, co "pożyczył").
        /// Bez obu filtrów Picker potrafił trafić w taki fałszywy fragment na powierzchni maseczki i błędnie
        /// potraktować ją jak kość, zamiast policzyć topologiczną izolację akcesorium pod spodem.
        /// </summary>
        private bool IsLegitBoneLabel(byte label, int index) =>
            label > 0 &&
            _dicomData.VolumeHu[index] >= _dicomData.morphThresholdHU &&
            _dicomData.GetMaskLabelSize(label) >= _dicomData.MinLegitBoneIslandVoxels;

        private void OnSelectEnteredManipulator(SelectEnterEventArgs args, VolumeRenderTarget target)
        {
            if (CurrentMode != ToolMode.Picker) return;
            if (!target.Visible) return;

            Vector3 origin = args.interactorObject.transform.position;
            Vector3 hitPoint = origin;

            if (args.interactorObject is UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor rayInteractor && rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
            {
                hitPoint = hit.point;
            }

            var voxelHit = PerformRaymarchCore(target, origin, hitPoint);
            if (voxelHit.HasValue)
            {
                int index = VolumeSpaceTransform.GetFlatIndex(voxelHit.Value, _dicomData.Width, _dicomData.Height);
                // Przed pierwszym "Wygeneruj Maskę" (albo tuż po Reset/regeneracji w locie) maskLabels
                // bywa jeszcze nieutworzone (NativeArray o Length==0) — bez tej straży indeksowanie
                // rzucało IndexOutOfRangeException zamiast po prostu potraktować woksel jak "bez etykiety
                // kostnej" i przejść ścieżką topologiczną (PickAccessoryIslandAt) niżej.
                byte label = _dicomData.maskLabels.IsCreated ? _dicomData.maskLabels[index] : (byte)0;

                if (IsLegitBoneLabel(label, index))
                {
                    Debug.Log($"[VolumePicker] Picker znalazł wyspę: {label} (Woksel: {voxelHit.Value}, obiekt: {target.DisplayName})");
                    _dicomData.morphMaskToKeep = label;
                    _dicomData.morphPickedVoxel = voxelHit.Value;
                    _dicomData.morphPickedVoxelOwnerId = target.OwnerId;
                    _dicomData.morphNegateMask = false;
                }
                else
                {
                    // Patrz komentarz w HandleMouseInput (branch Picker) — realny, widoczny materiał bez
                    // etykiety kostnej: liczymy topologiczną izolację akcesorium zamiast ignorować trafienie.
                    Debug.Log($"[VolumePicker] Picker (XR) trafił w obiekt bez etykiety kostnej (Woksel: {voxelHit.Value}, obiekt: {target.DisplayName}) — izoluję akcesorium.");
                    _dicomData.PickAccessoryIslandAt(voxelHit.Value, target.OwnerId);
                }
            }
        }

        private void OnSelectEnteredProxy(SelectEnterEventArgs args, VolumeRenderTarget target)
        {
            _activeInteractor = args.interactorObject;
            _activeBrushTarget = target;
            // Pierwszy tunel (w trybie TunnelCut) odpali się automatycznie w najbliższej klatce
            // Update() przez ProcessXRTunnelContinuous — _lastTunnelCutTime startuje na tyle "starte",
            // że throttle od razu przepuszcza pierwszy strzał.
        }

        // Tryby, w których trzymanie/przeciąganie ciągle maluje (Cut = wycina, RemoveIsland = "gumka" —
        // ciągłe przywracanie ciętego obszaru). Picker i TunnelCut reagują tylko na pojedyncze kliknięcie.
        private static bool IsContinuousBrushMode(ToolMode mode) => mode == ToolMode.Cut || mode == ToolMode.RemoveIsland;

        private void OnSelectExitedProxy(SelectExitEventArgs args, VolumeRenderTarget target)
        {
            if (_activeBrushTarget == target)
            {
                _activeInteractor = null;
                _activeBrushTarget = null;
            }

            if (_needsMorphologyUpdate && !_isRegenerating)
            {
                _needsMorphologyUpdate = false;
                _isRegenerating = true;
                RegenerateMorphologyAsync().Forget();
            }
        }

        private void ProcessXRBrushContinuous()
        {
            Vector3 origin = _activeInteractor.transform.position;
            Vector3 hitPoint = origin;

            if (_activeInteractor is UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor rayInteractor && rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
            {
                hitPoint = hit.point;
            }

            ApplyBrushAt(_activeBrushTarget, origin, hitPoint, CurrentMode);
        }

        private void ProcessXRTunnelContinuous()
        {
            Vector3 origin = _activeInteractor.transform.position;
            Vector3 hitPoint = origin;

            if (_activeInteractor is UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor rayInteractor && rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
            {
                hitPoint = hit.point;
            }

            ApplyTunnelCut(_activeBrushTarget, origin, hitPoint);
        }

        private async Cysharp.Threading.Tasks.UniTaskVoid RegenerateMorphologyAsync()
        {
            try
            {
                // Pędzel/tunel piszą wprost do _OwnerTex na GPU, z pominięciem SyncOwnerMaskToGPU, więc
                // mapy zajętości per obiekt zdążyły się zdezaktualizować w trakcie pociągnięcia. Dla
                // obiektu CIĘTEGO to tylko strata wydajności (mapa jest nadmiarowa), ale dla KOSZA
                // byłoby gorzej: materiał właśnie do niego wpadł w bloki, które mapa uważa za puste,
                // więc bez odświeżenia zostałby przeskoczony i zniknąłby z obrazu. Odświeżamy tu, na
                // tym samym wyciszeniu (debounce) co segmentacja, żeby nie robić tego co klatkę.
                _dicomData.RebuildAllOwnerOccupancy();
                await _dicomData.GenerateMorphologyMask();
            }
            finally
            {
                // UWAGA: świadomie NIE wołamy tu już System.GC.Collect().
                // Wymuszony pełny GC (blokujący na Mono/IL2CPP) po KAŻDYM przeliczeniu maski
                // dokładał dziesiątki-setki ms zacięcia przy każdym cięciu — na HoloLens 2
                // ten koszt jest jeszcze wyższy niż na PC. GC ma sam zdecydować kiedy sprzątać.
                _isRegenerating = false;
            }
        }

        /// <summary>
        /// Rozstrzyga w KTÓRY zarejestrowany (i widoczny) obiekt trafia dany promień — NAJBLIŻSZE
        /// trafienie spośród wszystkich colliderów należących do znanych VolumeRenderTarget, nie tylko
        /// pierwsze cokolwiek trafione przez fizykę. To jest CAŁY mechanizm "przełączania się między
        /// obiektami" — nie ma osobnego trybu/przełącznika, cel narzędzia wynika wyłącznie z tego,
        /// gdzie w danej klatce celuje user.
        /// </summary>
        private bool TryResolveTarget(Ray ray, out RaycastHit hit, out VolumeRenderTarget target)
        {
            hit = default;
            target = null;
            int n = Physics.RaycastNonAlloc(ray, _raycastBuffer, 1000f);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = _raycastBuffer[i];
                if (h.collider == null) continue;
                if (!_colliderToTarget.TryGetValue(h.collider, out var t)) continue;
                if (!t.Visible) continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    hit = h;
                    target = t;
                    found = true;
                }
            }
            return found;
        }

        private void HandleMouseInput()
        {
            if (IsPointerOverUI()) return;
            if (Mouse.current == null) return;
            if (_mainCamera == null) return;

            bool leftClickDown = Mouse.current.leftButton.wasPressedThisFrame;
            bool leftClick = Mouse.current.leftButton.isPressed;
            bool rightClick = Mouse.current.rightButton.isPressed;
            bool leftClickUp = Mouse.current.leftButton.wasReleasedThisFrame;
            bool rightClickUp = Mouse.current.rightButton.wasReleasedThisFrame;

            if (leftClickDown && CurrentMode == ToolMode.Inspect)
            {
                Vector2 mousePos = Mouse.current.position.ReadValue();
                Ray inspectRay = _mainCamera.ScreenPointToRay(mousePos);
                if (TryResolveTarget(inspectRay, out RaycastHit inspectHit, out VolumeRenderTarget target))
                {
                    InspectVoxelAtCursor(target, inspectRay.origin, inspectHit.point);
                }
                else
                {
                    Debug.LogWarning("[VolumePicker][Inspect] Promień NIE trafił w żaden zarejestrowany (i widoczny) obiekt wolumetryczny.");
                }
            }
            else if (CurrentMode == ToolMode.TunnelCut)
            {
                // Wspiera zarówno pojedynczy klik, jak i przytrzymanie+przeciąganie — throttlowane
                // przez TunnelCutIntervalMs (tunel to znacznie cięższa operacja niż lokalny pędzel Cut,
                // więc celowo NIE odpala się co klatkę tak jak Cut).
                if (leftClick && Time.time - _lastTunnelCutTime >= Mathf.Max(TunnelCutIntervalMs, 0) / 1000f)
                {
                    Ray tunnelRay = _mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
                    if (TryResolveTarget(tunnelRay, out RaycastHit tunnelHit, out VolumeRenderTarget target))
                    {
                        ApplyTunnelCut(target, tunnelRay.origin, tunnelHit.point);
                    }
                }
            }
            else if (leftClickDown && CurrentMode == ToolMode.Picker)
            {
                Ray ray = _mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
                if (TryResolveTarget(ray, out RaycastHit hit, out VolumeRenderTarget target))
                {
                    var voxelHit = PerformRaymarchCore(target, ray.origin, hit.point);
                    if (voxelHit.HasValue)
                    {
                        int index = VolumeSpaceTransform.GetFlatIndex(voxelHit.Value, _dicomData.Width, _dicomData.Height);
                        // Patrz komentarz w OnSelectEnteredManipulator — maskLabels może jeszcze nie istnieć.
                        byte label = _dicomData.maskLabels.IsCreated ? _dicomData.maskLabels[index] : (byte)0;
                        if (IsLegitBoneLabel(label, index))
                        {
                            Debug.Log($"[VolumePicker] Myszka wyizolowała wyspę: {label} (Woksel: {voxelHit.Value}, obiekt: {target.DisplayName})");
                            _dicomData.morphMaskToKeep = label;
                            _dicomData.morphPickedVoxel = voxelHit.Value;
                            _dicomData.morphPickedVoxelOwnerId = target.OwnerId;
                            _dicomData.morphNegateMask = false;
                        }
                        else
                        {
                            // Trafiony materiał jest realny i widoczny (patrz PerformRaymarchCore), ale bez
                            // PRAWDZIWEJ etykiety kostnej (label==0, albo label>0 lecz to za mała drobina
                            // szumu CT — patrz IsLegitBoneLabel) — typowo akcesorium (maseczka, korek, łóżko
                            // skanera) o gęstości poniżej Morph Threshold HU. Liczymy CZYSTO TOPOLOGICZNY
                            // komponent (morphErosionRadius odcina erozją cienkie mostki skóry PRZED
                            // CCL), żeby wyizolować je jako JEDNĄ spójną całość, odrębną od stykającej się
                            // skóry (patrz PickAccessoryIslandAt).
                            Debug.Log($"[VolumePicker] Myszka trafiła w obiekt bez etykiety kostnej (Woksel: {voxelHit.Value}, obiekt: {target.DisplayName}) — izoluję akcesorium.");
                            _dicomData.PickAccessoryIslandAt(voxelHit.Value, target.OwnerId);
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[VolumePicker] Picker: promień z kliknięcia NIE trafił w żaden woksel (raymarch nie znalazł dopasowania w bryle wolumenu).");
                    }
                }
            }
            else if (leftClickDown && CurrentMode == ToolMode.RemoveIsland)
            {
                // RemoveIsland celowo NIE używa maskLabels (liczonych przy WYSOKIM progu segmentacji
                // kości) — obiekt o zmiennej wewnętrznej gęstości (np. gąbczasty materiał z gęstszym
                // rdzeniem) byłby tam widoczny tylko częściowo, więc usuwanie działałoby "warstwami".
                // Zamiast tego: znajdź dowolny realny woksel pod kursorem (Auto Strip Threshold HU tu
                // służy WYŁĄCZNIE do znalezienia SEEDA pod kursorem — łapie dosłownie wszystko poza
                // powietrzem, żeby trafić w cokolwiek widocznego), a LoadDicomData.RemoveConnectedObjectAt
                // policzy łączność OD NOWA (czysto topologicznie, bez pasma gęstości wokół seeda —
                // morphErosionRadius odcina erozją cienkie mostki skóry PRZED liczeniem łączności,
                // więc kontakt przez samą skórę się nie liczy) i usunie CAŁY fizycznie odłączony obiekt
                // naraz — albo odmówi, jeśli seed leży w głównej strukturze.
                Ray ray = _mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
                if (TryResolveTarget(ray, out RaycastHit hit, out VolumeRenderTarget target))
                {
                    var scan = ScanRayFull(target, ray.origin, hit.point);
                    // WAŻNE: pomijamy woksele, które już NIE należą do celowanego obiektu (schowane do
                    // Kosza albo wydzielone gdzie indziej) — inaczej promień może "trafić" w coś, co
                    // technicznie ma wysoką gęstość w surowych danych, ale jest już niewidoczne na TYM
                    // obiekcie, więc FindComponentContainingSeedAsync słusznie nie przypisze mu żadnej
                    // wyspy i operacja nic nie schowa (dokładnie ten bug, który już raz naprawiono dla
                    // dawnego userCutsMask).
                    var seedVoxel = scan?.FirstVoxelAboveAndUncut(_dicomData.VisibleMaterialThresholdHU, v =>
                    {
                        int idx = VolumeSpaceTransform.GetFlatIndex(v, _dicomData.Width, _dicomData.Height);
                        return _dicomData.pieceOwnerMask.IsCreated && _dicomData.pieceOwnerMask[idx] != target.OwnerId;
                    });
                    if (seedVoxel.HasValue)
                    {
                        Debug.Log($"[VolumePicker] Magiczna gumka: usuwam cały odłączony obiekt od woksela {seedVoxel.Value} (obiekt: {target.DisplayName}).");
                        _dicomData.RemoveConnectedObjectAt(seedVoxel.Value, target.OwnerId);
                    }
                    else
                    {
                        Debug.LogWarning("[VolumePicker] Magiczna gumka: promień nie trafił w żaden materiał powyżej Auto Strip Threshold HU.");
                    }
                }
            }
            else if ((leftClick || rightClick) && CurrentMode == ToolMode.Cut)
            {
                // Ciągłe malowanie myszką: tylko Cut (tak było już wcześniej — RemoveIsland
                // działa myszką jako pojedynczy klik "usuń całą wyspę", nie pędzel; TunnelCut
                // ma własny, throttlowany branch obsłużony wyżej, wspierający też przytrzymanie).
                Ray ray = _mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
                if (TryResolveTarget(ray, out RaycastHit hit, out VolumeRenderTarget target))
                {
                    ApplyBrushAt(target, ray.origin, hit.point, CurrentMode);
                }
            }
            else if (leftClickUp || rightClickUp)
            {
                if (CurrentMode == ToolMode.Cut && _needsMorphologyUpdate && !_isRegenerating)
                {
                    _needsMorphologyUpdate = false;
                    _isRegenerating = true;
                    RegenerateMorphologyAsync().Forget();
                }
            }
        }

        private bool IsPointerOverUI()
        {
            if (UnityEngine.EventSystems.EventSystem.current == null || Mouse.current == null) return false;

            UnityEngine.EventSystems.PointerEventData eventData = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
            {
                position = Mouse.current.position.ReadValue()
            };

            _uiRaycastResults.Clear();
            UnityEngine.EventSystems.EventSystem.current.RaycastAll(eventData, _uiRaycastResults);

            for (int i = 0; i < _uiRaycastResults.Count; i++)
            {
                if (_uiRaycastResults[i].gameObject.layer == LayerMask.NameToLayer("UI"))
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyBrushAt(VolumeRenderTarget target, Vector3 rayOrigin, Vector3 rayHitPoint, ToolMode mode)
        {
            if (_dicomData == null || !_dicomData.pieceOwnerMask.IsCreated || target == null) return;

            var hit = PerformRaymarchCore(target, rayOrigin, rayHitPoint);
            if (hit.HasValue)
            {
                Vector3Int center = hit.Value;

                // Przeliczamy fizyczny promień (mm) na odpowiednią liczbę wokseli w każdej z osi
                int rx = Mathf.CeilToInt(BrushRadiusMM / _dicomData.PixelSpacingX);
                int ry = Mathf.CeilToInt(BrushRadiusMM / _dicomData.PixelSpacingY);
                int rz = Mathf.CeilToInt(BrushRadiusMM / _dicomData.SliceThickness);

                // Zapobieganie błędom przy zerowym spacingu / dzieleniu
                if (rx <= 0) rx = 1;
                if (ry <= 0) ry = 1;
                if (rz <= 0) rz = 1;

                int minX = Mathf.Max(0, center.x - rx);
                int maxX = Mathf.Min(_dicomData.Width - 1, center.x + rx);
                int minY = Mathf.Max(0, center.y - ry);
                int maxY = Mathf.Min(_dicomData.Height - 1, center.y + ry);
                int minZ = Mathf.Max(0, center.z - rz);
                int maxZ = Mathf.Min(_dicomData.Depth - 1, center.z + rz);

                // Cut = chowa do kosza CIĘTEGO obiektu; RemoveIsland (gumka, ciągłe malowanie) =
                // przywraca z tego kosza z powrotem do niego. Obie operacje dotyczą wyłącznie pary
                // (obiekt, jego kosz), więc nie ruszają ani czaszki, ani innych kawałków, które
                // akurat stoją w tym samym miejscu.
                bool erase = (mode != ToolMode.Cut);

                byte sourceOwner = target.OwnerId;
                // Cięcie tworzy kosz, jeśli to pierwsze cięcie z tego obiektu; gumka go NIE tworzy —
                // skoro nic jeszcze nie wycięto, nie ma czego przywracać, a pusty kosz tylko
                // zaśmieciłby scenę. binOwner==0 znaczy "nie ma dokąd/skąd" → nie robimy nic, zamiast
                // przypisać materiał byle komu.
                byte binOwner = erase
                    ? _dicomData.GetExistingCutBinOwner(sourceOwner)
                    : _dicomData.ResolveCutBinOwner(sourceOwner);
                if (binOwner == 0) return;

                // Cofanie działa na CAŁE pociągnięcie, nie na pojedynczą klatkę malowania — inaczej
                // jedno przeciągnięcie pędzlem rozpadłoby się na kilkadziesiąt osobnych kroków
                // i „cofnij” cofałoby po kawałku ruchu, którego użytkownik nigdy tak nie widział.
                // Krok domyka Update, gdy pędzel przestaje być trzymany.
                if (!_strokeOpen)
                {
                    _dicomData.EditHistory.Begin(erase ? "Przywrócenie pędzlem" : "Cięcie pędzlem");
                    _strokeOpen = true;
                }

                // NATYCHMIASTOWA wizualizacja na GPU — tylko bounding box pędzla, nie cały wolumen.
                // To jest to, co użytkownik widzi w tej samej klatce; CPU-owy pieceOwnerMask poniżej
                // pozostaje źródłem prawdy dla (wolnej, tła) segmentacji Pick/RemoveIsland. Malujemy
                // zawsze do WSPÓLNYCH danych niezależnie od tego, który obiekt był celem — cięcie
                // fragmentu i cięcie głównego wolumenu to fizycznie ta sama operacja na tych samych
                // wokselach oryginalnego skanu.
                _dicomData.PaintOwnerBrush(minX, minY, minZ, maxX, maxY, maxZ, center, rx, ry, rz, erase,
                                            sourceOwner, binOwner);

                bool modified = false;

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            int dx = x - center.x;
                            int dy = y - center.y;
                            int dz = z - center.z;

                            float nx = (float)dx / rx;
                            float ny = (float)dy / ry;
                            float nz = (float)dz / rz;

                            // Równanie elipsoidy (kula w jednostkach fizycznych)
                            if (nx * nx + ny * ny + nz * nz <= 1f)
                            {
                                int index = VolumeSpaceTransform.GetFlatIndex(new Vector3Int(x, y, z), _dicomData.Width, _dicomData.Height);

                                // Dokładnie ta sama para warunków co w CSPaintOwnerBrush (GPU) —
                                // maska CPU i tekstura GPU muszą zgadzać się co do KAŻDEGO woksela.
                                if (erase)
                                {
                                    if (_dicomData.pieceOwnerMask[index] == binOwner)
                                    {
                                        _dicomData.EditHistory.Record(index, binOwner);
                                        _dicomData.pieceOwnerMask[index] = sourceOwner;
                                        modified = true;
                                    }
                                }
                                else if (_dicomData.pieceOwnerMask[index] == sourceOwner)
                                {
                                    _dicomData.EditHistory.Record(index, sourceOwner);
                                    _dicomData.pieceOwnerMask[index] = binOwner;
                                    modified = true;
                                }
                            }
                        }
                    }
                }

                if (modified)
                {
                    _lastBrushTime = Time.time;
                    _needsMorphologyUpdate = true;
                }
            }
        }

        /// <summary>
        /// TunnelCut — wycina prostą "dziurę na wylot" wzdłuż promienia patrzenia/kontrolera,
        /// od wejścia do wyjścia z bryły CELOWANEGO obiektu (nie tylko lokalny pęcherzyk jak Cut).
        /// Jedno wywołanie = natychmiastowy, trwały efekt (bez trzymania/przeciągania).
        /// </summary>
        private void ApplyTunnelCut(VolumeRenderTarget target, Vector3 rayOrigin, Vector3 rayHitPoint)
        {
            if (_dicomData == null || !_dicomData.pieceOwnerMask.IsCreated || target == null) return;
            if (!ComputeTunnelVoxelSegment(target, rayOrigin, rayHitPoint, out Vector3 voxelStart, out Vector3 voxelEnd))
            {
                Debug.LogWarning("[VolumePicker][TunnelCut] Promień NIE przecina bryły celowanego obiektu (RayBoxIntersect zwrócił false) — brak jakiegokolwiek efektu.");
                return;
            }

            // Znacznik czasu throttlingu — sprawdzany zarówno przez mysz (HandleMouseInput),
            // jak i XR (ProcessXRTunnelContinuous), żeby przytrzymanie nie odpalało tunelu co klatkę.
            _lastTunnelCutTime = Time.time;

            // Promień otworu w wokselach — przybliżenie izotropowe na bazie X-spacing
            // (narzędzie do "zaglądania do środka", nie precyzyjne narzędzie chirurgiczne).
            float radiusVoxels = Mathf.Max(BrushRadiusMM / Mathf.Max(_dicomData.PixelSpacingX, 0.0001f), 1f);

            Debug.Log($"[VolumePicker][TunnelCut] Tunel: start={voxelStart} end={voxelEnd} promień={radiusVoxels:F1} wokseli (BrushRadiusMM={BrushRadiusMM}, wolumin={_dicomData.Width}x{_dicomData.Height}x{_dicomData.Depth})");

            byte sourceOwner = target.OwnerId;
            byte binOwner = _dicomData.ResolveCutBinOwner(sourceOwner);
            if (binOwner == 0) return;

            // Natychmiastowa wizualizacja na GPU — jeden dispatch po bounding boxie tunelu (nie po
            // całym wolumenie), więc koszt skaluje się z długością/promieniem tunelu.
            _dicomData.TunnelOwnerGPU(voxelStart, voxelEnd, radiusVoxels, sourceOwner, binOwner);

            TunnelCutCpuAsync(voxelStart, voxelEnd, radiusVoxels, sourceOwner, binOwner).Forget();
        }

        /// <summary>
        /// Liczy odcinek (w przestrzeni wokseli ORYGINALNEGO wolumenu) od wejścia do wyjścia promienia
        /// z bryły CELOWANEGO obiektu — ta sama matematyka co PerformRaymarchCore (RayBoxIntersect +
        /// SubLocalToOriginalLocal + LocalToUVW), tylko zamiast szukać PIERWSZEGO trafienia, zwraca
        /// CAŁY odcinek od tMin do tMax.
        /// </summary>
        private bool ComputeTunnelVoxelSegment(VolumeRenderTarget target, Vector3 origin, Vector3 hitPoint, out Vector3 voxelStart, out Vector3 voxelEnd)
        {
            voxelStart = voxelEnd = Vector3.zero;

            Vector3 dir = (hitPoint - origin).normalized;
            if (dir.sqrMagnitude < 0.001f && _mainCamera != null) dir = _mainCamera.transform.forward;

            Transform t = target.ProxyTransform;
            Vector3 localOrigin = t.InverseTransformPoint(origin);
            // InverseTransformVector, NIE InverseTransformDirection — patrz komentarz przy
            // PerformRaymarchCore: to drugie z założenia IGNORUJE skalę, więc przy niejednorodnie
            // przeskalowanym obiekcie kierunek promienia trafiał do innej przestrzeni niż jego początek.
            Vector3 localDir = t.InverseTransformVector(dir).normalized;

            if (!RayBoxIntersect(localOrigin, localDir, out float tMin, out float tMax)) return false;
            tMin = Mathf.Max(tMin, 0f);
            if (tMax <= tMin) return false;

            Vector3 rotOffset = Vector3.zero;
            if (target.Material != null && target.Material.HasProperty("_RotationOffset"))
                rotOffset = target.Material.GetVector("_RotationOffset");

            Vector3 localEntry = localOrigin + localDir * tMin;
            Vector3 localExit  = localOrigin + localDir * tMax;

            Vector3 origEntry = VolumeSpaceTransform.SubLocalToOriginalLocal(localEntry, target.SubLocalCenter, target.SubLocalSize);
            Vector3 origExit  = VolumeSpaceTransform.SubLocalToOriginalLocal(localExit,  target.SubLocalCenter, target.SubLocalSize);

            Vector3 uvwEntry = VolumeSpaceTransform.LocalToUVW(origEntry, rotOffset);
            Vector3 uvwExit  = VolumeSpaceTransform.LocalToUVW(origExit,  rotOffset);

            voxelStart = new Vector3(uvwEntry.x * _dicomData.Width, uvwEntry.y * _dicomData.Height, uvwEntry.z * _dicomData.Depth);
            voxelEnd   = new Vector3(uvwExit.x  * _dicomData.Width, uvwExit.y  * _dicomData.Height, uvwExit.z  * _dicomData.Depth);
            return true;
        }

        private async Cysharp.Threading.Tasks.UniTaskVoid TunnelCutCpuAsync(Vector3 voxelStart, Vector3 voxelEnd, float radiusVoxels,
                                                                            byte sourceOwner, byte binOwner)
        {
            int width = _dicomData.Width, height = _dicomData.Height, depth = _dicomData.Depth;
            var owners = _dicomData.pieceOwnerMask;

            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(voxelStart.x, voxelEnd.x) - radiusVoxels), 0, width  - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(voxelStart.x, voxelEnd.x) + radiusVoxels), 0, width  - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(voxelStart.y, voxelEnd.y) - radiusVoxels), 0, height - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(voxelStart.y, voxelEnd.y) + radiusVoxels), 0, height - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(voxelStart.z, voxelEnd.z) - radiusVoxels), 0, depth  - 1);
            int maxZ = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(voxelStart.z, voxelEnd.z) + radiusVoxels), 0, depth  - 1);

            // Bounding box tunelu może być spory (biegnie przez cały wolumen) — ZAWSZE wątek tła,
            // żeby nie zamrozić klatki, dokładnie jak przy pędzlu i magicznej gumce.
            int modifiedCount = 0;
            // Tunel to jedno kliknięcie = jeden krok, więc granice kroku są tu jednoznaczne (inaczej
            // niż przy ciągłym pociągnięciu pędzlem, gdzie krok domyka dopiero puszczenie przycisku).
            _dicomData.EditHistory.Begin("Przewiercenie tunelu");

            await Cysharp.Threading.Tasks.UniTask.RunOnThreadPool(() =>
            {
                Vector3 seg = voxelEnd - voxelStart;
                float segLenSq = Mathf.Max(seg.sqrMagnitude, 1e-5f);
                float radiusSq = radiusVoxels * radiusVoxels;

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            Vector3 p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                            float t = Mathf.Clamp01(Vector3.Dot(p - voxelStart, seg) / segLenSq);
                            Vector3 closest = voxelStart + seg * t;
                            if ((p - closest).sqrMagnitude <= radiusSq)
                            {
                                int idx = z * width * height + y * width + x;
                                // Strażnik własności identyczny jak w CSTunnelOwner (GPU): tunel
                                // przez jeden obiekt nie może zabierać wokseli należących do innego.
                                if (owners[idx] == sourceOwner)
                                {
                                    _dicomData.EditHistory.Record(idx, sourceOwner);
                                    owners[idx] = binOwner;
                                    modifiedCount++;
                                }
                            }
                        }
                    }
                }
            });

            _dicomData.EditHistory.Commit();

            Debug.Log($"[VolumePicker][TunnelCut] Bounding box: X[{minX}-{maxX}] Y[{minY}-{maxY}] Z[{minZ}-{maxZ}]. Schowano do Kosza {modifiedCount} nowych wokseli.");

            _lastBrushTime = Time.time;
            _needsMorphologyUpdate = true;
        }

        private Vector3Int? PerformRaymarchCore(VolumeRenderTarget target, Vector3 origin, Vector3 hitPoint)
        {
            Vector3 dir = (hitPoint - origin).normalized;
            if (dir.sqrMagnitude < 0.001f && _mainCamera != null) dir = _mainCamera.transform.forward;

            Transform t = target.ProxyTransform;
            Vector3 localOrigin = t.InverseTransformPoint(origin);
            // InverseTransformVector, NIE InverseTransformDirection. Unity dokumentuje to drugie jako
            // "unaffected by scale" — obraca wektor, ale IGNORUJE skalę obiektu. Tymczasem
            // InverseTransformPoint powyżej skalę UWZGLĘDNIA, więc początek promienia i jego kierunek
            // lądowały w dwóch RÓŻNYCH przestrzeniach, gdy tylko obiekt miał skalę niejednorodną.
            //
            // Dla głównej czaszki skala jest prawie jednorodna, więc błąd był ledwie zauważalny, ale
            // KAŻDY wydzielony kawałek ma skalę proporcjonalną do swojego pod-obszaru (np. 0.3 x 0.5 x 0.2),
            // czyli mocno niejednorodną — tam promień biegł wewnątrz bryły w wyraźnie złym kierunku.
            // Błąd narasta z odległością przebytą w bryle, dlatego celowanie przy krawędzi (najdłuższa
            // droga przez pudło) trafiało "kompletnie po innej stronie" obiektu.
            //
            // Po normalizacji parametr t jest w jednostkach lokalnych, czyli dokładnie tych, których
            // oczekuje RayBoxIntersect dla bryły -0.5..0.5 — normalizacja zmienia parametryzację, nie
            // przebieg geometryczny promienia.
            Vector3 localDir = t.InverseTransformVector(dir).normalized;

            if (RayBoxIntersect(localOrigin, localDir, out float tMin, out float tMax))
            {
                tMin = Mathf.Max(tMin, 0f);

                Vector3 rotOffset = Vector3.zero;
                Vector4 clipPlane = new Vector4(0, 1, 0, 0);

                Material mat = target.Material;
                if (mat != null)
                {
                    if (mat.HasProperty("_RotationOffset")) rotOffset = mat.GetVector("_RotationOffset");
                    if (mat.HasProperty("_ClipPlane")) clipPlane = mat.GetVector("_ClipPlane");
                }

                int maxDim = Mathf.Max(_dicomData.Width, Mathf.Max(_dicomData.Height, _dicomData.Depth));
                float stepSize = 0.5f / maxDim; // Zawsze minimum 2 próbki na najmniejszy woksel

                // UWAGA: świadomie NIE podwajamy już kroku w trybie Cut. To był JEDEN promień na
                // klatkę (nie per-piksel jak w shaderze) — koszt podwojenia próbek jest znikomy, a
                // przy płaskim kącie patrzenia na zakrzywioną powierzchnię (np. czoło) zgrubiony krok
                // potrafił "przeskoczyć" cienką powierzchnię i znaleźć pierwsze trafienie kawałek
                // dalej wzdłuż krzywizny — czyli WIDOCZNIE przesunięte cięcie względem miejsca
                // celowania, mimo że sam promień (origin/dir) był poprawny.

                int maxSteps = Mathf.CeilToInt((tMax - tMin) / stepSize);
                float t2 = tMin;

                // Limit głębokości wiercenia (tylko Cut) — patrz komentarz przy MaxCutDepthMM.
                // Przybliżone przeliczenie mm -> jednostki t (grube, bo t nie biegnie wzdłuż jednej
                // konkretnej osi o znanym spacingu — wystarczające dla zabezpieczenia, nie do precyzyjnych pomiarów).
                float voxelMM = (_dicomData.PixelSpacingX + _dicomData.PixelSpacingY + _dicomData.SliceThickness) / 3f;
                float maxDepthT = MaxCutDepthMM / Mathf.Max(voxelMM * maxDim, 0.0001f);
                float? tOriginalSurface = null;

                for (int i = 0; i < maxSteps; i++)
                {
                    Vector3 samplePosLocal = localOrigin + localDir * t2;
                    Vector3 samplePosWorld = t.TransformPoint(samplePosLocal);

                    float planeDist = Vector3.Dot(new Vector3(clipPlane.x, clipPlane.y, clipPlane.z), samplePosWorld) + clipPlane.w;

                    if (planeDist <= 0) // Punkt przechodzi test Clip Plane'a
                    {
                        // Mapujemy lokalną pozycję NA CELOWANYM OBIEKCIE z powrotem na odpowiadającą
                        // pozycję w lokalnej przestrzeni ORYGINALNEGO VolumeCube (identyczność dla
                        // głównego wolumenu) — dopiero POTEM aplikujemy istniejącą rotację/UVW.
                        Vector3 origLocalPos = VolumeSpaceTransform.SubLocalToOriginalLocal(samplePosLocal, target.SubLocalCenter, target.SubLocalSize);
                        Vector3 uvw = VolumeSpaceTransform.LocalToUVW(origLocalPos, rotOffset);

                        if (uvw.x >= 0 && uvw.x <= 1 && uvw.y >= 0 && uvw.y <= 1 && uvw.z >= 0 && uvw.z <= 1)
                        {
                            Vector3Int voxel = VolumeSpaceTransform.UvwToVoxelIndex(uvw, _dicomData.Width, _dicomData.Height, _dicomData.Depth);
                            int index = VolumeSpaceTransform.GetFlatIndex(voxel, _dicomData.Width, _dicomData.Height);

                            if (CurrentMode == ToolMode.Cut)
                            {
                                // Malowanie musi trafiać w to, co użytkownik FAKTYCZNIE widzi:
                                // - CutThresholdHU (NIE morphThresholdHU!) — świadomie osobny, niski próg,
                                //   żeby dało się wyciąć każdy widoczny w renderze materiał (np. maskę/pasek
                                //   ze skanera o niskiej gęstości), niezależnie od tego jak wysoko jest
                                //   ustawiony próg segmentacji (potrzebny do CZYSTYCH wysp dla Pick/RemoveIsland),
                                // - ten sam filtr maski co shader (IsVoxelVisibleUnderMask) — inaczej promień
                                //   zatrzymuje się na niewidocznej strukturze ukrytej przez izolację maski.
                                bool passesBasic = _dicomData.VolumeHu[index] >= CutThresholdHU &&
                                                    IsVoxelVisibleUnderMask(index, target.OwnerId);

                                if (passesBasic)
                                {
                                    // Zapamiętujemy PIERWSZE trafienie (wycięte czy nie) jako "oryginalną"
                                    // powierzchnię — od niej liczymy dozwoloną głębokość wiercenia.
                                    if (!tOriginalSurface.HasValue) tOriginalSurface = t2;

                                    // "Już wycięty" = nie należy już do CELOWANEGO obiektu (schowany do Kosza
                                    // albo wydzielony gdzie indziej) — patrz pieceOwnerMask.
                                    bool alreadyCut = _dicomData.pieceOwnerMask[index] != target.OwnerId;
                                    if (!alreadyCut)
                                    {
                                        // Pomijamy już wycięte woksele (BEZ TEGO promień zawsze zatrzymywał się
                                        // na tej samej, pierwotnej powierzchni — "wwiercenie się" głębiej przez
                                        // wcześniej wyciętą dziurę było niemożliwe, dało się ciąć tylko po wierzchu),
                                        // ALE tylko do MaxCutDepthMM od oryginalnej powierzchni — inaczej trzymanie
                                        // pędzla w jednym miejscu potrafi przewiercić czaszkę na wylot.
                                        if (t2 - tOriginalSurface.Value <= maxDepthT)
                                            return voxel;

                                        // Osiągnięto limit głębokości w tym miejscu — koniec wiercenia tutaj.
                                        return null;
                                    }
                                }
                            }
                            else
                            {
                                // Picker: zatrzymujemy się na PIERWSZYM realnie widocznym materiale (ta sama
                                // logika co Cut: CutThresholdHU + IsVoxelVisibleUnderMask + pominięcie już
                                // wyciętych) — NIE tylko na wokselach z etykietą kostną (maskLabels > 0).
                                // Bez tego promień przelatywał na wylot przez akcesoria bez etykiety kostnej
                                // (np. gumową maseczkę czy korek od linii do narkozy — za mało gęste żeby
                                // dostać etykietę przy Morph Threshold HU), nigdy ich "nie widząc". To, czy
                                // trafiony woksel ma etykietę kostną (izolacja od razu gotowa) czy nie (trzeba
                                // policzyć topologiczną izolację akcesorium — LoadDicomData.PickAccessoryIslandAt),
                                // sprawdza wywołujący.
                                if (_dicomData.VolumeHu[index] >= CutThresholdHU &&
                                    _dicomData.pieceOwnerMask[index] == target.OwnerId &&
                                    IsVoxelVisibleUnderMask(index, target.OwnerId))
                                    return voxel;
                            }
                        }
                    }
                    t2 += stepSize;
                }
            }
            return null;
        }

        /// <summary>
        /// Diagnostyka (tryb Inspect): skanuje CAŁĄ linię wzroku i loguje: (1) maksymalne HU napotkane
        /// na całym promieniu (czy TAM w ogóle jest cokolwiek gęstego), (2) pierwsze trafienie dla
        /// KAŻDEGO realnie używanego progu (Cut/Auto-Strip/Morph) osobno — jeden klik, pełny obraz,
        /// zamiast zgadywać który próg sprawdzić. Nic nie zmienia w danych.
        /// </summary>
        private void InspectVoxelAtCursor(VolumeRenderTarget target, Vector3 rayOrigin, Vector3 rayHitPoint)
        {
            var scan = ScanRayFull(target, rayOrigin, rayHitPoint);
            if (scan == null)
            {
                Debug.LogWarning("[VolumePicker][Inspect] Promień NIE przecina bryły celowanego obiektu.");
                return;
            }

            Debug.Log($"[VolumePicker][Inspect] Obiekt: {target.DisplayName}. MAX HU wzdłuż CAŁEGO promienia = {scan.MaxHU} przy wokselu {scan.MaxHuVoxel} " +
                $"(t={scan.MaxHuT:F3} z zakresu [{scan.TMin:F3}-{scan.TMax:F3}]). To pokazuje czy JAKIEKOLWIEK realne ciało leży na tej linii wzroku, niezależnie od progów.");

            LogThresholdHit("Cut Threshold HU", CutThresholdHU, scan);
            LogThresholdHit("Auto Strip Threshold HU", _dicomData.VisibleMaterialThresholdHU, scan);
            LogThresholdHit("Morph Threshold HU", _dicomData.morphThresholdHU, scan);
        }

        private void LogThresholdHit(string label, float threshold, RayScanResult scan)
        {
            Vector3Int? hit = scan.FirstVoxelAbove(threshold);
            if (!hit.HasValue)
            {
                Debug.Log($"[VolumePicker][Inspect] {label}={threshold}: NIC na tej linii wzroku nie przekracza tego progu.");
                return;
            }
            int index = VolumeSpaceTransform.GetFlatIndex(hit.Value, _dicomData.Width, _dicomData.Height);
            short hu = _dicomData.VolumeHu[index];
            byte islandLabel = _dicomData.maskLabels.IsCreated ? _dicomData.maskLabels[index] : (byte)0;
            // Właściciel zamiast starego binarnego "wycięty" — teraz może ich być wielu (Kosz, konkretny
            // wydzielony kawałek...), patrz VolumeObjectManager.GetOrCreateCutBinFor.
            byte owner = _dicomData.pieceOwnerMask.IsCreated ? _dicomData.pieceOwnerMask[index] : (byte)0;
            Debug.Log($"[VolumePicker][Inspect] {label}={threshold}: pierwsze trafienie woksel {hit.Value}, HU={hu}, etykieta wyspy={islandLabel}, właściciel={owner}");
        }

        private class RayScanResult
        {
            public float TMin, TMax;
            public short MaxHU = short.MinValue;
            public Vector3Int MaxHuVoxel;
            public float MaxHuT;
            public List<(float t, Vector3Int voxel, short hu)> Samples = new List<(float, Vector3Int, short)>();

            public Vector3Int? FirstVoxelAbove(float threshold)
            {
                foreach (var s in Samples)
                    if (s.hu >= threshold) return s.voxel;
                return null;
            }

            /// <summary>
            /// Jak FirstVoxelAbove, ale dodatkowo pomija woksele uznane przez isCut za już wycięte —
            /// żeby nie trafić "punktem startowym" w coś niewidocznego (userCutsMask), co skutkowałoby
            /// operacją na obiekcie, którego z perspektywy segmentacji już nie ma.
            /// </summary>
            public Vector3Int? FirstVoxelAboveAndUncut(float threshold, System.Func<Vector3Int, bool> isCut)
            {
                foreach (var s in Samples)
                    if (s.hu >= threshold && !isCut(s.voxel)) return s.voxel;
                return null;
            }
        }

        /// <summary>
        /// Skanuje CAŁY promień jednym przebiegiem (ta sama matematyka co PerformRaymarchCore:
        /// RayBoxIntersect + SubLocalToOriginalLocal + LocalToUVW) i zapisuje HU KAŻDEGO napotkanego
        /// woksela — pozwala sprawdzić wiele progów naraz (i znaleźć maksymalne HU na całej linii
        /// wzroku) bez wielokrotnego klikania. Używane WYŁĄCZNIE przez Inspect i RemoveIsland (seed
        /// search) — nic nie zmienia w danych.
        /// </summary>
        private RayScanResult ScanRayFull(VolumeRenderTarget target, Vector3 origin, Vector3 hitPoint)
        {
            Vector3 dir = (hitPoint - origin).normalized;
            if (dir.sqrMagnitude < 0.001f && _mainCamera != null) dir = _mainCamera.transform.forward;

            Transform t = target.ProxyTransform;
            Vector3 localOrigin = t.InverseTransformPoint(origin);
            // InverseTransformVector, NIE InverseTransformDirection — patrz komentarz przy
            // PerformRaymarchCore: to drugie z założenia IGNORUJE skalę, więc przy niejednorodnie
            // przeskalowanym obiekcie kierunek promienia trafiał do innej przestrzeni niż jego początek.
            Vector3 localDir = t.InverseTransformVector(dir).normalized;

            if (!RayBoxIntersect(localOrigin, localDir, out float tMin, out float tMax)) return null;
            tMin = Mathf.Max(tMin, 0f);

            Vector3 rotOffset = Vector3.zero;
            if (target.Material != null && target.Material.HasProperty("_RotationOffset"))
                rotOffset = target.Material.GetVector("_RotationOffset");

            var result = new RayScanResult { TMin = tMin, TMax = tMax };

            int maxDim = Mathf.Max(_dicomData.Width, Mathf.Max(_dicomData.Height, _dicomData.Depth));
            float stepSize = 0.5f / maxDim;
            int maxSteps = Mathf.CeilToInt((tMax - tMin) / stepSize);
            float tt = tMin;

            for (int i = 0; i < maxSteps; i++)
            {
                Vector3 samplePosLocal = localOrigin + localDir * tt;
                Vector3 origLocalPos = VolumeSpaceTransform.SubLocalToOriginalLocal(samplePosLocal, target.SubLocalCenter, target.SubLocalSize);
                Vector3 uvw = VolumeSpaceTransform.LocalToUVW(origLocalPos, rotOffset);

                if (uvw.x >= 0 && uvw.x <= 1 && uvw.y >= 0 && uvw.y <= 1 && uvw.z >= 0 && uvw.z <= 1)
                {
                    Vector3Int voxel = VolumeSpaceTransform.UvwToVoxelIndex(uvw, _dicomData.Width, _dicomData.Height, _dicomData.Depth);
                    int index = VolumeSpaceTransform.GetFlatIndex(voxel, _dicomData.Width, _dicomData.Height);
                    short hu = _dicomData.VolumeHu[index];
                    result.Samples.Add((tt, voxel, hu));
                    if (hu > result.MaxHU) { result.MaxHU = hu; result.MaxHuVoxel = voxel; result.MaxHuT = tt; }
                }
                tt += stepSize;
            }
            return result;
        }

        /// <summary>
        /// Sprawdza czy dany woksel byłby widoczny na ekranie przy aktualnych ustawieniach maski
        /// morfologicznej — DOKŁADNIE ta sama logika co blok "MORPHOLOGY MASK" w shaderze
        /// (RaymarchCT_Simplified.shader / RaymarchCT.shader). Musi być trzymana w synchronizacji
        /// z tamtą logiką ręcznie, bo shader HLSL i ten kod C# to dwie oddzielne implementacje.
        /// Globalne (nie per-target) — mechanizm podglądu Pick/RemoveIsland jest jeden, wspólny dla
        /// całej sceny, niezależnie od tego, na który obiekt aktualnie celujesz.
        /// </summary>
        private bool IsVoxelVisibleUnderMask(int index, byte targetOwnerId)
        {
            // Podgląd izolacji jest aktywny na DOKŁADNIE JEDNYM obiekcie — tym, na którym ostatnio
            // kliknięto Pickerem (LoadDicomData.UpdateMorphologyMaskID rozprowadza te uniformy tylko
            // na jego materiał, a pozostałym je zeruje). Ten warunek to wierne odbicie tamtej reguły
            // po stronie CPU — bez niego promień na obiekcie BEZ aktywnej izolacji był filtrowany
            // cudzym stanem maski: przy Pełnej Izolacji każdy woksel wydzielonego kawałka (maskID=0,
            // bo segmentacja kostna liczy się tylko dla właściciela 0) fałszywie wypadał jako
            // niewidoczny i Cut/Picker wyglądały jakby w ogóle nie działały na oddzielonych strukturach.
            if (targetOwnerId != _dicomData.morphPickedVoxelOwnerId) return true;
            if (_dicomData.morphMaskToKeep <= 0) return true; // maskowanie wyłączone — wszystko widoczne
            if (!_dicomData.maskLabels.IsCreated) return true;

            int maskID = _dicomData.maskLabels[index];
            int targetID = _dicomData.morphMaskToKeep;

            if (_dicomData.morphNegateMask)
            {
                // Tryb ukrywania wybranej maski (+ dodatkowe Extra Hide)
                bool hidden = maskID == targetID ||
                              (maskID > 0 && maskID == _dicomData.morphExtraHide1) ||
                              (maskID > 0 && maskID == _dicomData.morphExtraHide2) ||
                              (maskID > 0 && maskID == _dicomData.morphExtraHide3);
                return !hidden;
            }

            if (_dicomData.morphKeepBackground)
            {
                // Tło (ID 0) zawsze widoczne, inne wyspy poza targetID — ukryte
                return !(maskID > 0 && maskID != targetID);
            }

            // Pełna izolacja — widoczna tylko dokładnie wybrana wyspa
            return maskID == targetID;
        }

        private bool RayBoxIntersect(Vector3 ro, Vector3 rd, out float tMin, out float tMax)
        {
            Vector3 invDir = new Vector3(1f / rd.x, 1f / rd.y, 1f / rd.z);
            Vector3 t0 = Vector3.Scale(new Vector3(-0.5f, -0.5f, -0.5f) - ro, invDir);
            Vector3 t1 = Vector3.Scale(new Vector3(0.5f, 0.5f, 0.5f) - ro, invDir);

            Vector3 tSmall = Vector3.Min(t0, t1);
            Vector3 tBig = Vector3.Max(t0, t1);

            tMin = Mathf.Max(Mathf.Max(tSmall.x, tSmall.y), tSmall.z);
            tMax = Mathf.Min(Mathf.Min(tBig.x, tBig.y), tBig.z);

            return tMax >= Mathf.Max(tMin, 0.0f);
        }


    }
}
