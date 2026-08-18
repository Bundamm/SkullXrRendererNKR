using System;
using System.Collections.Generic;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Klasa danych (NIE MonoBehaviour) opisująca jeden renderowalny obiekt wolumetryczny — albo
    /// główny wolumen (OwnerId=0), albo jeden wydzielony kawałek (OwnerId=N, patrz
    /// LoadDicomData.ExtractPickedIslandAsObject). VolumePicker konsumuje tę listę do raycastingu i
    /// cięcia, VolumeObjectListUI do budowania ręcznego menu widoczności.
    /// </summary>
    public class VolumeRenderTarget
    {
        public Transform ProxyTransform;
        public MeshRenderer Renderer;
        public BoxCollider Collider;
        public Material Material;
        public MixedReality.Toolkit.SpatialManipulation.ObjectManipulator Manipulator;
        public MixedReality.Toolkit.SpatialManipulation.BoundsControl BoundsControl;
        // Wizualizacja rączek wygenerowana przez BoundsControl (osobne dziecko, patrz
        // BoundsControl.CreateBoundsVisuals). Trzymana osobno, bo wyłączenie SAMEGO komponentu
        // BoundsControl jej NIE chowa — trzeba ją dezaktywować jawnie (patrz VolumePicker.Update).
        public GameObject BoundsVisuals;
        // Tworzony/przypisywany przez VolumePicker (jeden BrushProxy+XRSimpleInteractable per target,
        // patrz VolumePicker.RegisterTarget) — trzymany tu, żeby VolumeObjectManager mógł go
        // uwzględnić przy chowaniu obiektu, bez zależności w drugą stronę na konkretnym typie z VolumePicker.
        public GameObject BrushProxy;
        public byte OwnerId;
        public string DisplayName;
        public bool Visible = true;
        // Gdy true, VolumePicker.Update() trzyma Manipulator+BoundsControl TEGO targetu wyłączone,
        // niezależnie od trybu/Visible — używane przez Kosz w trybie "nałożony na czaszkę"
        // (SetCutBinAlignedToSkull), żeby nie miał WŁASNEGO, nakładającego się na czaszkę uchwytu —
        // porusza/obraca się wtedy WYŁĄCZNIE przez uchwyt czaszki (jest jej dzieckiem w hierarchii).
        public bool SuppressOwnHandle;
        // Czy ten obiekt jest koszem (celem cięć) — i czyim. Kosz nie jest osobną kategorią w masce
        // własności: to zwykły obiekt z własnym OwnerId, więc da się go pickować, ciąć i wydzielać
        // z niego kawałki dokładnie tak jak z czaszki. Patrz VolumeObjectManager.GetOrCreateCutBinFor.
        public bool IsCutBin;
        public byte BinSourceOwnerId;
        // Zgrubna mapa zajętości TEGO obiektu (1 = blok zawiera jego widoczny materiał). Per obiekt,
        // nie globalnie: mapa gęstościowa nie odróżniłaby rzadkiej zawartości kosza od gęstej czaszki
        // zajmującej te same bloki — patrz LoadDicomData.RebuildOwnerOccupancy.
        public RenderTexture Occupancy;
        // Miejsce "spoczynkowe" obok źródła — zapamiętane, żeby dało się wrócić po nałożeniu kosza
        // na jego obiekt źródłowy (SetCutBinAlignedToSkull).
        public Vector3 RestPosition;
        public Quaternion RestRotation;
        public Vector3 RestScale;
        // Stały, policzony raz przy wydzieleniu obiektu podzbiór lokalnej przestrzeni -0.5..0.5
        // ORYGINALNEGO VolumeCube — patrz komentarz przy _SubLocalCenter/_SubLocalSize w RaymarchCT.shader.
        public Vector3 SubLocalCenter;
        public Vector3 SubLocalSize;
    }

    /// <summary>
    /// Centralny, jedyny rejestr wszystkich obiektów wolumetrycznych na scenie (główny wolumen + każdy
    /// wydzielony kawałek). VolumePicker czyta stąd Targets do raycastingu/cięcia (celowanie decyduje,
    /// który obiekt jest aktualnym celem narzędzi — bez osobnego przełącznika trybu), LoadDicomData
    /// woła SpawnPieceObject zaraz po wydzieleniu fragmentu (ExtractPickedIslandAsObject), a UI
    /// (VolumeObjectListUI) woła SetVisible z ręcznego menu widoczności.
    /// </summary>
    public class VolumeObjectManager : MonoBehaviour
    {
        // Najwyższy dopuszczalny OwnerId. 254/255 zostawiamy wolne jako margines na ewentualne
        // wartości wartownicze — cały zakres 1..253 dzielą między siebie wydzielone kawałki i kosze
        // (kosz to zwykły obiekt z własnym OwnerId, nie osobna kategoria w masce własności).
        public const byte MaxOwnerId = 253;

        [Tooltip("Podepnij główny skrypt ładujący dane, lub zostaw puste (znajdzie go automatycznie)")]
        public LoadDicomData dicomDataRef;
        private LoadDicomData _dicomData;

        private readonly List<VolumeRenderTarget> _targets = new List<VolumeRenderTarget>();
        public IReadOnlyList<VolumeRenderTarget> Targets => _targets;

        // KAŻDY obiekt ma swój własny kosz, tworzony leniwie przy PIERWSZYM cięciu z tego obiektu
        // (klucz = OwnerId źródła). Dzięki temu materiał wycięty z czaszki, z kawałka nr 3 i z
        // zawartości innego kosza nigdy się nie miesza, a w każdym koszu da się dalej pracować
        // (Pick / Wydziel jako obiekt / kolejne cięcia, które trafią do kosza TEGO kosza).
        private readonly Dictionary<byte, VolumeRenderTarget> _binBySource = new Dictionary<byte, VolumeRenderTarget>();

        // Wspólna pula identyfikatorów dla kawałków I koszy — jeden licznik, żeby nie dało się
        // przydzielić tego samego OwnerId dwa razy z dwóch różnych miejsc.
        private int _nextOwnerId = 1;

        /// <summary>
        /// Kosz GŁÓWNEJ czaszki (źródło OwnerId=0) albo null, jeśli z czaszki nie wycięto jeszcze
        /// niczego. Wygodny skrót dla UI i dla nakładania kosza na czaszkę — pozostałe kosze
        /// znajdziesz przez Targets (VolumeRenderTarget.IsCutBin) albo GetOrCreateCutBinFor.
        /// </summary>
        public VolumeRenderTarget CutBin => _binBySource.TryGetValue(0, out var bin) ? bin : null;

        /// <summary>Odpalane, gdy dochodzi lub znika obiekt — UI listy się przebudowuje.</summary>
        public event Action OnTargetsChanged;

        /// <summary>
        /// Odpalane TUŻ PRZED zniszczeniem obiektu (Reset Cuts), żeby konsumenci trzymający własne
        /// mapy/cache po tym obiekcie (np. VolumePicker._colliderToTarget) mogli je posprzątać,
        /// zamiast zostać ze wpisami wskazującymi na zniszczone komponenty Unity.
        /// </summary>
        public event Action<VolumeRenderTarget> OnTargetRemoved;

        /// <summary>
        /// Przydziela kolejny wolny OwnerId ze wspólnej puli (kawałki + kosze). Zwraca 0, gdy pula
        /// się wyczerpie — wywołujący traktuje to jako "nie da się utworzyć kolejnego obiektu".
        /// </summary>
        public byte AllocateOwnerId()
        {
            if (_nextOwnerId > MaxOwnerId)
            {
                Debug.LogError($"[VolumeObjectManager] Wyczerpano pulę identyfikatorów obiektów (max {MaxOwnerId}). " +
                    "Użyj Reset Cuts, żeby wrócić do stanu początkowego i zwolnić numery.");
                return 0;
            }
            return (byte)_nextOwnerId++;
        }

        /// <summary>
        /// Zwraca (tworząc przy pierwszym użyciu) kosz obiektu o podanym OwnerId — miejsce, do
        /// którego trafia WSZYSTKO wycięte/usunięte z TEGO obiektu (pędzel Cut, TunnelCut, Usuń
        /// wyspę) zamiast być trwale skasowane. Kosz jest zwykłym, w pełni
        /// chwytalnym obiektem (SpawnPieceObject), więc można go dalej pickować, ciąć i wydzielać z
        /// niego pojedyncze kawałki. Zwraca null, jeśli źródło nie istnieje albo pula ID się wyczerpała.
        /// </summary>
        public VolumeRenderTarget GetOrCreateCutBinFor(byte sourceOwnerId)
        {
            if (_binBySource.TryGetValue(sourceOwnerId, out var existing) && existing != null) return existing;

            VolumeRenderTarget source = FindTarget(sourceOwnerId);
            if (source == null)
            {
                Debug.LogWarning($"[VolumeObjectManager] Nie mogę utworzyć kosza dla właściciela {sourceOwnerId} — nie ma takiego obiektu.");
                return null;
            }

            byte binId = AllocateOwnerId();
            if (binId == 0) return null;

            // Kosz staje OBOK swojego źródła (po przeciwnej stronie niż lądują wydzielane kawałki,
            // które idą wzdłuż +right) i przejmuje DOKŁADNIE ten sam sub-region co źródło.
            //
            // Sub-region MUSI się zgadzać, bo skalę świata bierzemy ze źródła: te dwie wartości opisują
            // razem "ile oryginalnego wolumenu mieści się w pudle tej wielkości". Kosz z
            // identycznościowym sub-regionem (cały wolumen) w pudle wielkości MAŁEGO fragmentu
            // upychałby cały skan w tę małą bryłę, więc jego zawartość renderowała się pomniejszona o
            // stosunek obu obszarów. Na głównej czaszce błąd był niewidoczny, bo jej sub-region i tak
            // jest całym wolumenem — ujawniał się dopiero na koszach wydzielonych fragmentów.
            //
            // Zgodność sub-regionów jest tu też poprawna merytorycznie: do kosza trafia wyłącznie
            // materiał wycięty ZE ŹRÓDŁA, a źródło renderuje tylko woksele ze swojego sub-regionu —
            // więc nic, co wpadnie do kosza, nie może leżeć poza tym obszarem. Dzięki temu ten sam
            // woksel wypada w obu bryłach w tym samym miejscu, co jest też warunkiem poprawnego
            // "nałożenia" kosza na źródło (SetBinAligned).
            Transform srcT = source.ProxyTransform;
            float halfExtent = srcT.lossyScale.x * 0.5f;
            Vector3 restPosition = srcT.position - srcT.right * (halfExtent * 2f + halfExtent * 0.6f);

            var bin = SpawnPieceObject(binId, source.SubLocalCenter, source.SubLocalSize,
                restPosition, srcT.rotation, srcT.lossyScale, $"Kosz: {source.DisplayName}");
            if (bin == null) return null;

            bin.IsCutBin = true;
            bin.BinSourceOwnerId = sourceOwnerId;
            bin.RestPosition = restPosition;
            bin.RestRotation = srcT.rotation;
            bin.RestScale = srcT.lossyScale;

            // Kosz bywa świadomie nakładany DOKŁADNIE na swoje źródło (SetCutBinAlignedToSkull) —
            // dwa idealnie pokrywające się, półprzezroczyste wolumeny o tym samym środku bryły sortują
            // się niedeterministycznie, więc wymuszamy stałą, przewidywalną kolejność rysowania.
            bin.Material.renderQueue = _dicomData.InstancedMaterial.renderQueue + 1;

            // Kosz powstaje WIDOCZNY. Ukrywanie go tutaj miało dwa skutki uboczne: użytkownik nie widział,
            // dokąd trafia wycinany materiał, a lista obiektów pokazywała go jako włączonego — bo
            // SpawnPieceObject zgłasza OnTargetsChanged ZANIM ta linia zdążyła go ukryć, a SetVisible
            // już listy nie odświeża. Trzeba było przełączyć widoczność dwa razy, żeby stany się zgadzały.
            _binBySource[sourceOwnerId] = bin;

            Debug.Log($"[VolumeObjectManager] Utworzono kosz '{bin.DisplayName}' (OwnerId {binId}) dla obiektu '{source.DisplayName}' (OwnerId {sourceOwnerId}).");
            return bin;
        }

        /// <summary>
        /// OwnerId kosza danego obiektu (tworząc kosz przy pierwszym użyciu) — wygodne dla ścieżek
        /// cięcia, które potrzebują tylko numeru do wpisania w maskę własności. Zwraca sourceOwnerId
        /// (czyli "nic nie rób"), jeśli kosza nie da się utworzyć — dzięki temu cięcie nie kasuje
        /// wtedy materiału po cichu, tylko po prostu nie robi nic.
        /// </summary>
        public byte GetCutBinOwnerFor(byte sourceOwnerId)
        {
            var bin = GetOrCreateCutBinFor(sourceOwnerId);
            return bin != null ? bin.OwnerId : sourceOwnerId;
        }

        /// <summary>
        /// Jak GetOrCreateCutBinFor, ale NIE tworzy kosza, jeśli jeszcze nie istnieje — dla operacji,
        /// które kosz tylko czytają (np. gumka przywracająca materiał: skoro nic nie wycięto, nie ma
        /// czego przywracać, a tworzenie pustego kosza tylko zaśmiecałoby scenę i listę).
        /// </summary>
        public bool TryGetCutBin(byte sourceOwnerId, out VolumeRenderTarget bin)
        {
            return _binBySource.TryGetValue(sourceOwnerId, out bin) && bin != null;
        }

        public VolumeRenderTarget FindTarget(byte ownerId)
        {
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i].OwnerId == ownerId) return _targets[i];
            return null;
        }

        void Start()
        {
            _dicomData = dicomDataRef;
            if (_dicomData == null) _dicomData = GetComponent<LoadDicomData>();
            if (_dicomData == null) _dicomData = FindObjectOfType<LoadDicomData>();

            if (_dicomData == null)
            {
                Debug.LogError("[VolumeObjectManager] Nie znaleziono skryptu LoadDicomData w scenie!");
                return;
            }

            // Ułatwiamy podłączenie w Edytorze — jeśli LoadDicomData.volumeObjectManager nie jest
            // ręcznie przypisane, robimy to tutaj, żeby ExtractPickedIslandAsObject zawsze miało referencję.
            if (_dicomData.volumeObjectManager == null) _dicomData.volumeObjectManager = this;

            _dicomData.OnVolumeReady += OnVolumeReady;
        }

        private void OnDestroy()
        {
            if (_dicomData != null) _dicomData.OnVolumeReady -= OnVolumeReady;
        }

        /// <summary>
        /// Rejestruje główny wolumen jako cel narzędzi. Wołane przy KAŻDYM wczytaniu serii, także przy
        /// przeładowaniu na inny skan, więc musi być idempotentne — dawniej handler odpinał się od
        /// eventu po pierwszym wywołaniu, przez co druga i każda kolejna seria zostawała bez celu
        /// narzędzi (Cut/Picker nie miały w co trafić, a lista obiektów była pusta).
        /// </summary>
        private void OnVolumeReady()
        {
            var mainTarget = FindTarget(0);
            if (mainTarget == null)
            {
                mainTarget = new VolumeRenderTarget { OwnerId = 0, DisplayName = "Czaszka" };
                _targets.Add(mainTarget);
            }

            // Referencje odświeżamy ZAWSZE — nowa seria oznacza nową instancję materiału, a
            // BuildTexture3D potrafi dołożyć brakujący collider/manipulator dopiero po wczytaniu.
            mainTarget.ProxyTransform = _dicomData.volumeCube.transform;
            mainTarget.Renderer = _dicomData.volumeCube.GetComponent<MeshRenderer>();
            mainTarget.Collider = _dicomData.volumeCube.GetComponent<BoxCollider>();
            mainTarget.Material = _dicomData.InstancedMaterial;
            mainTarget.Manipulator = _dicomData.volumeCube.GetComponent<MixedReality.Toolkit.SpatialManipulation.ObjectManipulator>();
            mainTarget.BoundsControl = _dicomData.volumeCube.GetComponent<MixedReality.Toolkit.SpatialManipulation.BoundsControl>();
            mainTarget.SubLocalCenter = Vector3.zero;
            mainTarget.SubLocalSize = Vector3.one;

            // Świeżo wczytany skan musi być widoczny, nawet jeśli poprzedni został schowany z listy.
            mainTarget.Visible = true;
            if (mainTarget.Renderer != null) mainTarget.Renderer.enabled = true;
            if (mainTarget.Collider != null) mainTarget.Collider.enabled = true;
            // Czaszka też dostaje mapę świadomą właściciela — dla właściciela 0 jest ona niemal
            // równoważna gęstościowej, ale dzięki temu wszystkie obiekty idą jedną ścieżką i mapa
            // czaszki przestaje uwzględniać materiał wycięty do koszy.
            _dicomData.RebuildOwnerOccupancy(mainTarget);

            // Kosze NIE powstają z góry — każdy tworzy się leniwie przy pierwszym cięciu ze swojego
            // obiektu (GetOrCreateCutBinFor). Dzięki temu scena nie startuje z pustym, niepotrzebnym
            // obiektem, a liczba koszy zawsze odpowiada temu, co użytkownik faktycznie porozcinał.
            OnTargetsChanged?.Invoke();
        }

        /// <summary>
        /// Wyłącza na klonie materiału stary, GLOBALNY mechanizm podglądu izolacji (_MaskIDToKeep i
        /// spółka — patrz Morphology Mask w RaymarchCT.shader) — wydzielone obiekty/Kosz używają
        /// WYŁĄCZNIE własności (_OwnerFilterID) do decydowania co pokazać, więc ten mechanizm im
        /// niepotrzebny. KRYTYCZNE, żeby to jawnie wyzerować, nie polegać na tym co akurat
        /// Instantiate(_dicomData.InstancedMaterial) odziedziczy: główny materiał ma te wartości
        /// aktualizowane CO KLATKĘ (LoadDicomData.UpdateMorphologyMaskID) zgodnie z bieżącym
        /// podglądem Pickera — ale klon dostaje tylko JEDNORAZOWY zrzut z chwili wydzielenia i
        /// NIGDY już nie jest aktualizowany. Jeśli akurat w tej chwili trwał podgląd izolacji
        /// akcesorium (_MaskIDToKeep=255 "AccessoryPreviewLabel", _MaskKeepBackground=0 "Full
        /// Isolation"), ten stan zamrażał się na kawałku NA STAŁE — a maskLabels (globalne, dzielone
        /// z całą sceną) potrafi później gdzie indziej wyzerować akurat te same wartości 255 (patrz
        /// PickAccessoryIslandAtAsync — czyści POPRZEDNIE zaznaczenie 255 przy KAŻDYM kolejnym
        /// pick'u), przez co zamrożony cel przestawał się zgadzać z tym co faktycznie widać w
        /// _MaskTex i CAŁY kawałek nagle znikał, mimo że jego OwnerId się nie zmienił.
        /// </summary>
        public static void ResetMorphologyMaskProperties(Material mat)
        {
            mat.SetFloat("_MaskIDToKeep", 0f);
            mat.SetFloat("_MaskKeepBackground", 1f);
            mat.SetFloat("_MaskNegate", 0f);
            mat.SetFloat("_MaskExtraHide1", 0f);
            mat.SetFloat("_MaskExtraHide2", 0f);
            mat.SetFloat("_MaskExtraHide3", 0f);
        }

        /// <summary>
        /// Materializuje wydzielony fragment jako nowy, niezależnie chwytalny GameObject. Klonujemy
        /// CAŁY volumeCube (Instantiate), NIE tworzymy gołego prymitywu — to jedyny niezawodny sposób
        /// (działa identycznie w Edytorze i w buildzie) żeby nowy obiekt dostał TAKIE SAME uchwyty co
        /// oryginał: MRTK3 komponenty jak BoundsControl przypisują domyślne prefaby rączek w
        /// Reset()/OnValidate() w Edytorze, ale Unity NIGDY nie woła Reset() dla komponentu dodanego
        /// przez AddComponent&lt;T&gt;() z poziomu kodu w Play Mode — świeżo dodany BoundsControl
        /// zostawał bez żadnych wizualnych rączek. Klonowanie już poprawnie skonfigurowanego
        /// volumeCube (rączki, ograniczenia obrotu/skali) w pełni to omija.
        ///
        /// Klon materiału współdzieli _VolumeTex/_MaskTex/_OwnerTex — te same obiekty Texture co
        /// oryginał, różni się tylko skalarami _OwnerFilterID/_SubLocalCenter/_SubLocalSize. Wołane
        /// przez LoadDicomData.ExtractPickedIslandAsObject — zarówno gdy wydzielasz coś wprost z
        /// czaszki, jak i gdy wyciągasz pojedynczy kawałek Z KOSZA (Kosz to zwykły target, więc
        /// Pick+ekstrakcja działają na nim identycznie) — które już policzyło AABB wydzielonych
        /// wokseli (subLocalCenter/subLocalSize, w lokalnej przestrzeni oryginalnego VolumeCube) oraz
        /// odpowiadający jej world Center/Rotation/Scale (przesunięty OBOK źródła, nie na nim — patrz
        /// LoadDicomData.FinalizeExtractionAsync — żeby dało się nowy kawałek złapać osobno). Używane
        /// TAKŻE przez GetOrCreateCutBinFor — kosz to pod tym względem zwykły "kawałek" (własne uchwyty,
        /// chwytalny), tylko z sub-regionem PRZEJĘTYM ZE ŹRÓDŁA (musi się zgadzać ze skalą świata,
        /// którą też bierzemy ze źródła — patrz GetOrCreateCutBinFor) i domyślnie schowany.
        /// </summary>
        public VolumeRenderTarget SpawnPieceObject(int ownerId, Vector3 subLocalCenter, Vector3 subLocalSize,
            Vector3 worldCenter, Quaternion worldRotation, Vector3 worldScale, string displayName)
        {
            // Unity woła Awake/OnEnable klonowanych komponentów SYNCHRONICZNIE w trakcie samego
            // Instantiate() — więc obojętnie co zrobimy PO tym wywołaniu, MRTK3 komponenty (BoundsControl
            // i inne) zdążą już zbudować się/wygenerować swoje wizualne rączki dla transformu, jaki miał
            // ORYGINAŁ w CHWILI klonowania. Na chwilę przestawiamy SAM volumeCube na docelowy transform
            // kawałka, klonujemy (więc klon "rodzi się" już z poprawnym rozmiarem/pozycją — jego
            // komponenty widzą WŁAŚCIWY transform już w swoim Awake/OnEnable), i NATYCHMIAST przywracamy
            // volumeCube z powrotem. Całość dzieje się synchronicznie (bez await w środku) — Unity
            // renderuje dopiero PO zakończeniu bieżącej klatki, więc użytkownik nigdy nie zobaczy
            // czaszki "skaczącej" w miejsce kawałka.
            //
            // DRUGI problem, niezależny od powyższego: volumeCube ma pod sobą WŁASNE dzieci (jego
            // DAWNO wygenerowaną wizualizację rączek BoundsControl — złego, pełnowolumenowego
            // rozmiaru; BrushProxy; ewentualnie ClipPlaneHandle — Kosz od niedawna NIE jest już
            // jednym z nich, chyba że jest właśnie "nałożony" — patrz SetBinAligned) —
            // Instantiate(volumeCube) klonuje je WSZYSTKIE razem
            // z nim, więc KAŻDY kolejny wydzielony kawałek dostawał w prezencie starą, źle dopasowaną
            // wizualizację rączek obok świeżo wygenerowanej poprawnej. Naprawa: na chwilę wyłączamy
            // WSZYSTKIE aktywne dzieci
            // volumeCube TUŻ PRZED klonowaniem (klon i tak je odziedziczy, ale jako NIEAKTYWNE,
            // niewidoczne kopie), przywracamy je aktywne z powrotem na volumeCube zaraz po, a na końcu
            // usuwamy z klonu WSZYSTKO co nieaktywne — to bezbłędnie rozróżnia "odziedziczony śmieć"
            // (nieaktywny) od świeżo wygenerowanej wizualizacji WŁASNEGO BoundsControl tego kawałka
            // (zawsze aktywna), bez zgadywania nazw czy liczenia na konkretny moment Awake/OnEnable.
            Transform cubeT = _dicomData.volumeCube.transform;
            Vector3 origCubePos = cubeT.position;
            Quaternion origCubeRot = cubeT.rotation;
            Vector3 origCubeScale = cubeT.localScale;

            var deactivatedChildren = new List<GameObject>();
            for (int i = 0; i < cubeT.childCount; i++)
            {
                GameObject child = cubeT.GetChild(i).gameObject;
                if (child.activeSelf)
                {
                    child.SetActive(false);
                    deactivatedChildren.Add(child);
                }
            }

            cubeT.position = worldCenter;
            cubeT.rotation = worldRotation;
            cubeT.localScale = worldScale;

            GameObject go = Instantiate(_dicomData.volumeCube);
            go.name = displayName;

            cubeT.position = origCubePos;
            cubeT.rotation = origCubeRot;
            cubeT.localScale = origCubeScale;
            foreach (var child in deactivatedChildren) child.SetActive(true);

            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = go.transform.GetChild(i).gameObject;
                if (!child.activeSelf) Destroy(child);
            }

            var renderer = go.GetComponent<MeshRenderer>();
            Material mat = Instantiate(_dicomData.InstancedMaterial);
            renderer.material = mat;
            mat.SetFloat("_OwnerFilterID", ownerId);
            mat.SetVector("_SubLocalCenter", new Vector4(subLocalCenter.x, subLocalCenter.y, subLocalCenter.z, 0f));
            mat.SetVector("_SubLocalSize", new Vector4(subLocalSize.x, subLocalSize.y, subLocalSize.z, 0f));
            ResetMorphologyMaskProperties(mat);
            // Mapa przeskakiwania pustki — klon zwykle dziedziczy referencję, ale tylko jeśli mapa
            // istniała w chwili klonowania; ustawiamy jawnie, żeby nie zależeć od kolejności startu.
            _dicomData.ApplyOccupancyToClonedMaterial(mat);

            // Transform klonu już powinien być poprawny (odziedziczony od przestawionego volumeCube) —
            // ustawiamy jeszcze raz jawnie tylko jako defensywne potwierdzenie / zabezpieczenie.
            go.transform.position = worldCenter;
            go.transform.rotation = worldRotation;
            go.transform.localScale = worldScale;

            var collider = go.GetComponent<BoxCollider>();
            if (collider == null) collider = go.AddComponent<BoxCollider>();
            collider.size = Vector3.one;
            collider.center = Vector3.zero;

            var manipulator = go.GetComponent<MixedReality.Toolkit.SpatialManipulation.ObjectManipulator>();
            if (manipulator == null) manipulator = go.AddComponent<MixedReality.Toolkit.SpatialManipulation.ObjectManipulator>();
            manipulator.HostTransform = go.transform;

            var scaleC = go.GetComponent<MixedReality.Toolkit.SpatialManipulation.MinMaxScaleConstraint>();
            if (scaleC == null) scaleC = go.AddComponent<MixedReality.Toolkit.SpatialManipulation.MinMaxScaleConstraint>();
            scaleC.MinimumScale = Vector3.one * 0.05f;
            scaleC.MaximumScale = Vector3.one * 5.0f;

            var boundsCtrl = go.GetComponent<MixedReality.Toolkit.SpatialManipulation.BoundsControl>();
            if (boundsCtrl == null) boundsCtrl = go.AddComponent<MixedReality.Toolkit.SpatialManipulation.BoundsControl>();

            // Wyłączenie samego komponentu BoundsControl NIE chowa rączek — MRTK3 trzyma ich
            // wizualizację w OSOBNYM dziecku (BoundsControl.CreateBoundsVisuals →
            // Instantiate(prefab, transform)), które ma własny Update() (SqueezableBoxVisuals) i
            // własne collidery, a BoundsControl nie ma żadnego OnDisable, który by je ruszał.
            // Wyłączony komponent przestaje tylko przeliczać transform, a rączki dalej wiszą w
            // powietrzu i dają się chwytać, blokując chwyt innych obiektów. Dlatego zapamiętujemy
            // tu ten obiekt i chowamy go jawnie razem z rączkami (patrz VolumePicker.Update).
            // W TYM miejscu identyfikacja jest jednoznaczna, bez zgadywania nazw: wszystkie
            // odziedziczone po volumeCube dzieci są NIEAKTYWNE (zostały przed chwilą oddane do
            // skasowania), a BrushProxy dokłada VolumePicker dopiero później — więc jedyne AKTYWNE
            // dziecko to świeżo wygenerowana wizualizacja rączek WŁASNEGO BoundsControl tego
            // obiektu. Szukamy po aktywności, NIE po indeksie: Destroy() w Unity jest odroczone do
            // końca klatki, więc odziedziczone dzieci wciąż tu wiszą i GetChild(0) trafiłoby w jedno z nich.
            GameObject boundsVisuals = null;
            for (int i = 0; i < go.transform.childCount; i++)
            {
                GameObject child = go.transform.GetChild(i).gameObject;
                if (child.activeSelf) { boundsVisuals = child; break; }
            }

            var target = new VolumeRenderTarget
            {
                ProxyTransform = go.transform,
                Renderer = renderer,
                Collider = collider,
                Material = mat,
                Manipulator = manipulator,
                BoundsControl = boundsCtrl,
                BoundsVisuals = boundsVisuals,
                OwnerId = (byte)ownerId,
                DisplayName = displayName,
                Visible = true,
                SubLocalCenter = subLocalCenter,
                SubLocalSize = subLocalSize
            };
            _targets.Add(target);
            // Własna mapa zajętości — dopiero teraz, bo wymaga znanego OwnerId i wpisu w rejestrze.
            _dicomData.RebuildOwnerOccupancy(target);
            OnTargetsChanged?.Invoke();
            return target;
        }

        /// <summary>Czy dany kosz jest aktualnie "nałożony" na swój obiekt źródłowy.</summary>
        public bool IsBinAligned(VolumeRenderTarget bin) => bin != null && bin.SuppressOwnHandle;

        /// <summary>Skrót zgodny z dawnym API — dotyczy kosza GŁÓWNEJ czaszki.</summary>
        public bool IsCutBinAlignedToSkull => IsBinAligned(CutBin);

        /// <summary>Skrót zgodny z dawnym API — nakłada/odsuwa kosz głównej czaszki.</summary>
        public void SetCutBinAlignedToSkull(bool align) => SetBinAligned(CutBin, align);

        /// <summary>
        /// "Nakłada" kosz DOKŁADNIE na jego obiekt źródłowy (żeby zobaczyć, SKĄD dokładnie co zostało
        /// wycięte) — albo odsuwa go z powrotem obok. PRAWDZIWE parentowanie pod transform źródła (nie
        /// jednorazowe skopiowanie transformu), więc kosz CIĄGLE podąża za źródłem, także gdy
        /// użytkownik obróci/przesunie je PO włączeniu nałożenia — tym samym uchwytem BoundsControl co
        /// źródło. Kosz dostaje wtedy SuppressOwnHandle=true, żeby nie miał WŁASNEGO, nakładającego się
        /// uchwytu (patrz VolumePicker.Update()). Bezpieczne względem SpawnPieceObject: gdyby w trakcie
        /// nałożenia powstał NOWY wydzielony kawałek, kosz jako aktywne dziecko źródła zostanie
        /// tymczasowo dezaktywowany/sklonowany/przywrócony, a jego nieaktywna kopia w klonie usunięta
        /// (ten sam mechanizm, który naprawił duplikowanie się rączek — patrz komentarz w SpawnPieceObject).
        /// </summary>
        public void SetBinAligned(VolumeRenderTarget bin, bool align)
        {
            if (bin?.ProxyTransform == null) return;

            VolumeRenderTarget source = FindTarget(bin.BinSourceOwnerId);
            if (align && source?.ProxyTransform == null) return;

            bin.SuppressOwnHandle = align;

            if (align)
            {
                bin.ProxyTransform.SetParent(source.ProxyTransform, false);
                bin.ProxyTransform.localPosition = Vector3.zero;
                bin.ProxyTransform.localRotation = Quaternion.identity;
                bin.ProxyTransform.localScale = Vector3.one;
            }
            else
            {
                bin.ProxyTransform.SetParent(null, false);
                bin.ProxyTransform.position = bin.RestPosition;
                bin.ProxyTransform.rotation = bin.RestRotation;
                bin.ProxyTransform.localScale = bin.RestScale;
            }
        }

        /// <summary>
        /// Reset Cuts po stronie sceny: kasuje WSZYSTKIE kosze i WSZYSTKIE wydzielone fragmenty,
        /// zostawiając wyłącznie główną czaszkę, i zwalnia całą pulę identyfikatorów. Sama maska
        /// własności (pieceOwnerMask/_OwnerTex) jest zerowana po stronie LoadDicomData.ResetCutsAsync —
        /// tutaj sprzątamy tylko obiekty sceny, żeby oba źródła prawdy wróciły do stanu początkowego
        /// razem. Konsumenci trzymający własne cache dostają OnTargetRemoved PRZED zniszczeniem obiektu.
        /// </summary>
        public void ResetAllDerivedObjects()
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var target = _targets[i];
                if (target.OwnerId == 0) continue; // główna czaszka zostaje

                OnTargetRemoved?.Invoke(target);
                _targets.RemoveAt(i);

                // RenderTexture nie jest sprzątana przez GC razem z GameObjectem — bez jawnego
                // Release() każdy Reset Cuts zostawiałby po sobie tekstury wszystkich obiektów.
                if (target.Occupancy != null) { target.Occupancy.Release(); target.Occupancy = null; }

                if (target.ProxyTransform != null)
                {
                    // Odpinamy od ewentualnego rodzica (nałożony kosz jest dzieckiem swojego źródła),
                    // żeby zniszczenie nie pociągnęło za sobą niczego, co do niego nie należy.
                    target.ProxyTransform.SetParent(null, false);
                    Destroy(target.ProxyTransform.gameObject);
                }
            }

            _binBySource.Clear();
            _nextOwnerId = 1;
            OnTargetsChanged?.Invoke();
        }

        /// <summary>
        /// Chowa/pokazuje CAŁY obiekt — renderer + collider razem, żeby schowany obiekt nie renderował
        /// się I nie dał się trafić promieniem (patrz sekcja "Widoczność" w planie — świadomie
        /// NIEZALEŻNE od tego, który obiekt jest aktualnie celem narzędzi cięcia). ObjectManipulator/
        /// BrushProxy CELOWO nie są tu ustawiane wprost — VolumePicker.Update() przelicza ich stan co
        /// klatkę z uwzględnieniem target.Visible ORAZ aktualnego trybu narzędzia; dwa niezależne
        /// miejsca ustawiające ten sam stan łatwo się rozjeżdżają (jedno źródło prawdy per klatkę).
        /// </summary>
        public void SetVisible(VolumeRenderTarget target, bool visible)
        {
            if (target == null || target.Visible == visible) return;
            target.Visible = visible;
            if (target.Renderer != null) target.Renderer.enabled = visible;
            if (target.Collider != null) target.Collider.enabled = visible;

            // Gwarancja świeżości dokładnie w chwili, gdy zaczyna to być widać. Mapa zajętości bywa
            // odświeżana z wyciszeniem (po pociągnięciu pędzlem), a kosz jest zwykle schowany właśnie
            // wtedy, gdy się do niego tnie — bez tego pierwsze pokazanie kosza mogłoby przeskoczyć
            // bloki z materiałem, który dopiero co do niego trafił.
            if (visible && _dicomData != null) _dicomData.RebuildOwnerOccupancy(target);
        }
    }
}
