using FellowOakDicom;
using FellowOakDicom.Imaging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Helpers;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using TMPro;



public class LoadDicomData : MonoBehaviour
{
    [Header("Paths")]
    [Tooltip("Domyślna seria (względem StreamingAssets) wczytywana przy starcie, wyłącznie dla wygody pracy w Edytorze. W gotowej aplikacji skan wskazuje użytkownik na ekranie startowym — patrz LoadSeriesAsync.")]
    public string studyFolder  = "Scan/STU00001";
    public string seriesFolder = "SER00001";

    [Tooltip("Czy wczytać serię z pól wyżej od razu po starcie sceny. WYŁĄCZ, gdy skanem steruje ekran startowy (AppFlow) — inaczej aplikacja wczytałaby najpierw skan domyślny tylko po to, żeby zaraz zwolnić go pod wybór użytkownika.")]
    public bool autoLoadOnStart = true;


    [Header("References")]
    public GameObject volumeCube;
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private Vector3 originalScale;
    // Transform sprzed JAKIEGOKOLWIEK wczytania — patrz Awake. Osobny od original*, bo tamte są
    // nadpisywane przy każdej serii (skala proporcjonalna do jej wymiarów fizycznych).
    private Vector3 _basePosition;
    private Quaternion _baseRotation;
    private Vector3 _baseScale = Vector3.one;
    public Material   volumeMaterial;

    [Header("Clip")]
    [SerializeField, Range(-1f, 1f)]
    private float cutHeight = 1f;
    // Uchwyt (Quad + ObjectManipulator) do RĘCZNEGO ustawiania dowolnie zorientowanej płaszczyzny
    // przekroju — patrz UpdateClipPlane/SetCutHeight. Czysto wizualne (test odległości w shaderze),
    // NIE dotyka pieceOwnerMask/morfologii, więc jest natychmiastowe i w pełni odwracalne.
    private GameObject _clipPlaneHandle;

    [Header("GPU Brush (Natychmiastowe Cięcie)")]
    [Tooltip("Przypisz Assets/Shaders/PaintCutMask.compute. Bez tego cięcie będzie widoczne dopiero po pełnym przeliczeniu maski morfologicznej (stare, wolne zachowanie).")]
    public ComputeShader cutPaintCompute;

    [Tooltip("Przypisz Assets/Shaders/VolumeOccupancy.compute. Buduje zgrubną mapę zajętości, dzięki której raymarching przeskakuje puste powietrze jednym krokiem zamiast maszerować przez nie po _StepSize. Bez tego renderowanie nadal działa poprawnie, tylko znacznie wolniej.")]
    public ComputeShader occupancyCompute;

    public enum RaymarchQuality
    {
        Auto,   // dobierz na podstawie klasy urządzenia (patrz ApplyRaymarchQuality)
        High,   // desktop / mocny GPU
        Medium,
        Low     // HoloLens i pokrewne — priorytet płynności nad drobnym detalem
    }

    [Header("Wydajność renderowania")]
    [Tooltip("Gęstość próbkowania raymarchingu. Auto dobiera ją z klasy urządzenia (typ GPU, pamięć, liczba rdzeni) — na HoloLens i podobnych schodzi na Low, gdzie liczy się płynność. Każdy poziom to inny _StepSize: mniejszy krok = więcej próbek na promień = ostrzejszy obraz i niższy FPS.")]
    public RaymarchQuality raymarchQuality = RaymarchQuality.Auto;

    [Tooltip("DIAGNOSTYKA: wyłącz, żeby całkowicie pominąć przeskakiwanie pustki (raymarching wraca do maszerowania krok po kroku — wolniej, ale bez udziału mapy zajętości). Jeśli artefakt graficzny znika po wyłączeniu, jego przyczyna leży w mapie zajętości; jeśli zostaje, winne jest coś innego.")]
    public bool enableEmptySkipping = true;

    [Header("Vessel Colors")]
    public Color vesselColorLow  = new Color(1.00f, 0.60f, 0.10f);
    public Color vesselColorHigh = new Color(1.00f, 0.85f, 0.20f);

    // --- Progi HU — 2 osobne pola, świadomie ROZDZIELONE (patrz tooltips). Trzymane razem, bo to
    // najczęstsze źródło pomyłki: "który próg za co odpowiada". Szczegóły -> najedź myszką na pole.
    [Header("Morphology Segmentation")]
    [Tooltip("PRÓG SEGMENTACJI — decyduje co liczy się jako 'wyspa' dla Pick/RemoveIsland. Ustaw WYSOKO " +
             "(250-350, gęstość kości) dla czystej, precyzyjnej segmentacji. NIE wpływa na to co może wyciąć Cut " +
             "(patrz Cut Threshold HU na VolumePicker).")]
    public float morphThresholdHU = 300f;
    [Tooltip("Promień (w wokselach) EROZJI — JEDNA gałka używana WSZĘDZIE, gdzie trzeba przeciąć cienki mostek " +
             "(typowo skóra/tkanka miękka) między dwoma fizycznie stykającymi się obiektami: (1) budowa podglądu " +
             "segmentacji (kolorowanie wysp, ten sam pipeline co closing/expand wyżej), ORAZ (2) Picker na obiekcie " +
             "bez etykiety kostnej i Remove Island/'Usuń spickowaną wyspę' na obiekcie, który mimo etykiety okazał " +
             "się akcesorium (patrz IsLegitBoneLabel w VolumePicker). CZYSTA topologia oparta WYŁĄCZNIE na " +
             "odległości: maska obecności materiału jest ERODOWANA o ten promień PRZED szukaniem spójnych " +
             "obiektów, więc cienkie mostki znikają i obiekty się rozdzielają, a fizycznie grube rzeczy (czaszka, " +
             "sam rdzeń akcesorium) przetrwają erozję jako jeden komponent. Pełny kształt jest potem odtwarzany " +
             "z powrotem BEZ ograniczenia odległości (długie cienkie fragmenty typu pasek maseczki wracają w " +
             "całości, nie 'poszarpanie') — ten promień wpływa TYLKO na to, jak GRUBY musi być mostek, żeby " +
             "przetrwać, nie na jakość odtworzenia. Działa NIEZALEŻNIE od gęstości akcesorium. Zbyt mały: obiekty " +
             "nadal się sklejają przez skórę (podgląd ORAZ usuwanie — teraz to ta sama wartość). Zbyt duży: " +
             "cienkie, faktycznie przyrośnięte struktury (odłamek kości, łuk jarzmowy) mogą się fałszywie " +
             "odseparować od głównej struktury.")]
    [Range(0, 10)] public int morphErosionRadius = 3;
    [Range(0, 10)] public int morphExpandRadius = 0;
    public int morphMaskToKeep = 0; // 0 = Pokaż wszystko, 1 = Maska nr 1 (największa), 2 = Maska nr 2 itd.
    public Vector3Int? morphPickedVoxel = null; // Śledzi kliknięty woksel, aby przy przerenderowywaniu trzymać się właściwej części.
    // Właściciel (patrz pieceOwnerMask/VolumeObjectManager) obiektu, NA KTÓRYM został wykonany aktualny
    // Pick — 0 = główny wolumen. Ustawiane przez VolumePicker razem z morphPickedVoxel, żeby DeletePickedIsland/
    // ExtractPickedIslandAsObject wiedziały, w obrębie CZYJEGO ownership ograniczyć dalsze liczenie łączności
    // (patrz FindComponentContainingSeedAsync/pieceOwnerMask) — bez tego dalsze cięcie/wydzielanie z już
    // wydzielonego (i przesuniętego!) kawałka mogłoby przypadkiem "sięgnąć" do głównego wolumenu albo innego kawałka.
    public byte morphPickedVoxelOwnerId = 0;
    public bool morphKeepBackground = true; // Czy pokazywać miękkie tkanki (poza maską)
    public bool morphNegateMask = false; // true = Ukryj wybraną maskę, false = Pokaż tylko wybraną maskę

    [Tooltip("Najniższa gęstość uznawana za WIDOCZNY materiał. Steruje dwiema rzeczami: progiem, od którego " +
             "shader zaczyna barwić naczynia (_VesselMinNorm), oraz tym, w co Picker uzna, że trafił promieniem. " +
             "Nie ma nic wspólnego z segmentacją (Morph Threshold HU) ani z tym, co może wyciąć pędzel " +
             "(Cut Threshold HU na VolumePicker).")]
    // Nazwa zmieniona z VisibleMaterialThresholdHU po usunięciu operacji Auto-Strip — pole nigdy nie służyło
    // wyłącznie jej. FormerlySerializedAs zachowuje wartość zapisaną w scenie mimo zmiany nazwy.
    [UnityEngine.Serialization.FormerlySerializedAs("AutoStripThresholdHU")]
    public float VisibleMaterialThresholdHU = 25f;

    // Próg "obecności materiału" (co w ogóle jest czymś, a nie powietrzem) dla topologii akcesoriów
    // (Picker/Remove Island) — CELOWO stała, NIE pole w Inspectorze. Powietrze na skali Hounsfielda to
    // ZAWSZE (z definicji) ok. -1000 HU, więc wartość dobrze poniżej wszystkiego realnego (tkanka, kość,
    // plastik, pianka) a wyraźnie powyżej -1000 nie wymaga dostrajania per skan — separacja obiektów
    // dotykających się przez skórę/tkankę miękką jest robiona WYŁĄCZNIE geometrycznie, promieniem erozji
    // (morphErosionRadius, patrz tooltip przy tym polu — jedna gałka, wspólna z podglądem segmentacji).
    private const float AccessoryPresenceThresholdHU = -500f;

    [Tooltip("Minimalny rozmiar (w wokselach) etykiety kostnej, żeby Picker uznał ją za PRAWDZIWĄ strukturę. " +
             "Przy nisko ustawionym Morph Threshold HU segmentacja potrafi wygenerować tysiące mikroskopijnych " +
             "'wysp' z szumu CT (patrz ostrzeżenie w konsoli: 'znaleziono X wysp — to bardzo dużo'). Bez tego " +
             "progu Picker czasem trafiał akurat w taką drobinę szumu (np. na powierzchni maseczki) i próbował " +
             "traktować ją jak prawdziwą kość zamiast policzyć izolację akcesorium pod spodem.")]
    public int MinLegitBoneIslandVoxels = 50;
    // Zarezerwowana etykieta dla doraźnego podglądu izolacji akcesorium (Picker na obiekcie bez etykiety kostnej).
    // RemapLabelsAsync nigdy nie przydziela 255 zwykłym wyspom kostnym (maks. 254), więc nie ma kolizji.
    private const byte AccessoryPreviewLabel = 255;

    [Header("Extra Masks to Hide (Negate Only)")]
    [Range(0, 10)] public int morphExtraHide1 = 0;
    [Range(0, 10)] public int morphExtraHide2 = 0;
    [Range(0, 10)] public int morphExtraHide3 = 0;
    public TextMeshProUGUI morphologyStatsText;

    // --- wewnętrzne ---
    private string              _seriesPath;
    private NativeArray<short>  _volumeHu;
    public NativeArray<short> VolumeHu => _volumeHu;
    public NativeArray<byte> maskLabels;
    // Rozmiar (w wokselach) każdej etykiety kostnej PRZED domalowaniem obrzeża (Morph Expand Radius),
    // indeks = etykieta (1..254). Używane przez Picker, żeby odróżnić PRAWDZIWĄ strukturę kostną od
    // szumu CT — przy nisko ustawionym Morph Threshold HU segmentacja potrafi wygenerować tysiące
    // mikroskopijnych "wysp" (patrz ostrzeżenie w RemapLabelsAsync); bez tego rozróżnienia Picker
    // czasem trafiał w taką drobinę szumu zamiast w prawdziwe akcesorium (np. maseczkę) pod spodem.
    private int[] _maskLabelSizes;
    public int GetMaskLabelSize(byte label) => (_maskLabelSizes != null && label < _maskLabelSizes.Length) ? _maskLabelSizes[label] : 0;
    private int                 _width, _height, _depth;
    public int Width => _width;
    public int Height => _height;
    public int Depth => _depth;
    // Trwały mask własności — 0 = główny wolumen, N = któryś z pozostałych obiektów sceny. Kosze NIE
    // są tu osobną kategorią: każdy kosz ma zwykły OwnerId ze wspólnej puli (patrz
    // VolumeObjectManager.GetOrCreateCutBinFor), bo każdy obiekt ma WŁASNY kosz — dzięki temu materiał
    // wycięty z czaszki i z wydzielonego kawałka nigdy się nie miesza, a kosz da się dalej ciąć jak
    // każdy inny obiekt. W PRZECIWIEŃSTWIE do maskLabels NIE jest przeliczany przez segmentację — to
    // jedyny identyfikator wystarczająco stabilny, żeby obiekt mógł istnieć na scenie przez dłuższy
    // czas. Jedyne źródło prawdy o widoczności — patrz "Piece Ownership" w RaymarchCT.shader.
    public NativeArray<byte> pieceOwnerMask;

    /// <summary>
    /// Historia zmian własności pozwalająca cofnąć OSTATNIĄ operację, a nie wyłącznie wszystkie
    /// naraz — patrz Helpers.VolumeEditHistory (tam też opis, które operacje obejmuje i dlaczego
    /// nie wszystkie).
    /// </summary>
    public readonly Helpers.VolumeEditHistory EditHistory = new Helpers.VolumeEditHistory();

    public VolumeObjectManager volumeObjectManager;
    private float               _pixelSpacingX  = 1f;
    public float PixelSpacingX => _pixelSpacingX;
    private float               _pixelSpacingY  = 1f;
    public float PixelSpacingY => _pixelSpacingY;
    private float               _sliceThickness = 1f;
    public float SliceThickness => _sliceThickness;

    private int _morphologyGeneration = 0;
    private readonly int        _huMin = -1000;
    private readonly int        _huMax = 3000;

    private Texture3D  _volumeTexture;
    private Texture3D  _maskTexture;
    // RenderTexture (enableRandomWrite), NIE Texture3D — RenderTexture nie wspiera SetPixelData,
    // więc WSZYSTKIE zapisy (pędzel, TunnelCut, masowa synchronizacja z pieceOwnerMask) idą przez
    // compute shader (PaintCutMask.compute). Tworzony TYLKO RAZ i potem mutowany w miejscu —
    // referencję do niego mają WSZYSTKIE klony materiału (główny wolumen + każdy wydzielony
    // kawałek + Kosz, patrz VolumeObjectManager) — Destroy+recreate zostawiłoby już istniejące
    // obiekty wskazujące na zniszczoną teksturę.
    private RenderTexture _ownerTexture;
    // Zgrubna mapa zajętości (maksimum gęstości na blok 8^3) — patrz VolumeOccupancy.compute.
    private RenderTexture _occupancyTexture;
    // Najniższa znormalizowana gęstość, przy której funkcja transferu daje JAKĄKOLWIEK widoczną
    // alfę — poniżej niej blok na pewno nic nie wnosi do obrazu i wolno go pominąć w całości.
    private float _emptySkipDensity;
    private const int OccupancyBlock = 8; // musi się zgadzać z OCCUPANCY_BLOCK w VolumeOccupancy.compute
    // Wspólny bufor pośredni dla budowy map zajętości (buduj → rozszerz). Jeden na całą scenę, bo
    // mapy powstają sekwencyjnie i są maleńkie (siatka 512x mniejsza od wolumenu).
    private RenderTexture _occupancyScratch;
    private Texture2D  _transferTexture;
    private int        _paintOwnerKernel = -1;
    private int        _clearOwnerKernel = -1;
    private int        _tunnelOwnerKernel = -1;
    private int        _syncOwnerKernel = -1;
    private bool       _computeCutsSupported;
    private Renderer   _cubeRenderer;
    private Material   _instancedMaterial; // Caching the material instance
    public Material InstancedMaterial => _instancedMaterial;
    private bool       _finishedRender;

    // transform cache (MRTK NaN guard)
    private Vector3    _lastValidPos;
    private Quaternion _lastValidRot;
    private Vector3    _lastValidScale;
    private bool       _hasValidTransform;

    /// <summary>Wywoływane gdy wolumin jest gotowy (po załadowaniu DICOM i zbudowaniu Texture3D).</summary>
    public System.Action OnVolumeReady;

    /// <summary>
    /// Postęp wczytywania serii, raportowany do ekranu startowego — przy kilkuset plastrach
    /// wczytywanie trwa dziesiątki sekund i bez tego wyglądałoby jak zawieszona aplikacja.
    /// </summary>
    public readonly struct LoadProgress
    {
        public readonly string Stage;
        public readonly int Current;
        public readonly int Total;

        public LoadProgress(string stage, int current, int total)
        {
            Stage = stage; Current = current; Total = total;
        }

        /// <summary>0..1, albo -1 dla etapu, którego nie da się wyrazić liczbowo (czytanie nagłówków).</summary>
        public float Fraction => Total > 0 ? Mathf.Clamp01(Current / (float)Total) : -1f;
    }

    /// <summary>Ścieżka aktualnie wczytanej serii, albo null gdy nic nie jest wczytane.</summary>
    public string CurrentSeriesPath => _seriesPath;
    /// <summary>Czy wolumin jest gotowy do renderowania i edycji.</summary>
    public bool IsVolumeReady => _finishedRender;
    /// <summary>Czy trwa wczytywanie serii — blokuje równoległe żądania.</summary>
    public bool IsLoading { get; private set; }

    class SliceInfo
    {
        public string FilePath;
        public double ZPosition;
    }

    // -----------------------------------------------------------------------
    private void Awake()
    {
        // Transform bazowy zapamiętujemy RAZ, przed jakimkolwiek wczytaniem — NIE przy każdej serii.
        // BuildTexture3D nadpisuje originalScale skalą proporcjonalną do wymiarów fizycznych danej
        // serii, a użytkownik w międzyczasie przesuwa i obraca model; wzięcie tego stanu za "oryginał"
        // przy drugim wczytaniu sprawiłoby, że każdy kolejny skan startuje tam, gdzie został
        // porzucony poprzedni, zamiast na swoim miejscu.
        if (volumeCube != null)
        {
            _basePosition = volumeCube.transform.position;
            _baseRotation = volumeCube.transform.rotation;
            _baseScale    = volumeCube.transform.localScale;

            originalPosition = _basePosition;
            originalRotation = _baseRotation;
            originalScale    = _baseScale;
        }
    }

    async UniTaskVoid Start()
    {
        if (!autoLoadOnStart) return;
        await LoadSeriesAsync(Path.Combine(Application.streamingAssetsPath, studyFolder, seriesFolder));
    }

    /// <summary>
    /// Wczytuje serię DICOM z DOWOLNEGO folderu na dysku, zwalniając wcześniej to, co było wczytane
    /// (patrz UnloadCurrent). Zwraca true tylko gdy wolumin faktycznie jest gotowy do renderowania.
    /// Jedyna droga wczytania skanu w runtime — Start() jedynie ją woła dla serii domyślnej.
    /// </summary>
    public async UniTask<bool> LoadSeriesAsync(string absolutePath,
                                               System.IProgress<LoadProgress> progress = null,
                                               System.Threading.CancellationToken ct = default)
    {
        if (IsLoading)
        {
            Debug.LogWarning("[LoadDicomData] Wczytywanie już trwa — pomijam kolejne żądanie.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(absolutePath) || !Directory.Exists(absolutePath))
        {
            Debug.LogError("[LoadDicomData] Folder serii nie istnieje: " + absolutePath);
            return false;
        }

        IsLoading = true;
        try
        {
            // Poprzednia seria musi zniknąć ZANIM zaczniemy alokować nową — trzymanie obu naraz
            // podwaja szczyt zużycia pamięci, a bufory natywne liczą się tu w gigabajtach.
            UnloadCurrent();

            progress?.Report(new LoadProgress("Czytanie nagłówków DICOM", 0, 0));
            var sortedFiles = await ScanSeriesFolderAsync(absolutePath, ct);
            if (sortedFiles == null || sortedFiles.Count == 0)
            {
                Debug.LogError("[LoadDicomData] Nie znaleziono prawidłowych plików DICOM w: " + absolutePath);
                return false;
            }

            _seriesPath = absolutePath;
            await ExtractHounsfieldUnits(sortedFiles, progress, ct);
            return _finishedRender;
        }
        catch (System.OperationCanceledException)
        {
            Debug.Log("[LoadDicomData] Wczytywanie anulowane — zwalniam to, co zdążyło się zaalokować.");
            UnloadCurrent();
            return false;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[LoadDicomData] Błąd wczytywania serii: {ex.Message}\n{ex.StackTrace}");
            UnloadCurrent();
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Czyta same nagłówki wszystkich plików w folderze i zwraca ścieżki posortowane wzdłuż osi Z
    /// (kolejność plastrów w wolumenie), albo null gdy nie ma tam ani jednego prawidłowego DICOM-a.
    /// </summary>
    private async UniTask<List<string>> ScanSeriesFolderAsync(string seriesPath, System.Threading.CancellationToken ct)
    {
        var files = Directory.GetFiles(seriesPath);
        var sliceInfos = new SliceInfo[files.Length];

        await UniTask.RunOnThreadPool(() =>
        {
            // Ograniczamy liczbę wątków, by fo-dicom nie zaalokował zbyt wielu buforów na raz
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                string file = files[i];
                if (file.EndsWith(".meta") || !DicomFile.HasValidHeader(file)) return;
                try
                {
                    // SkipLargeTags zamyka strumień natychmiast i ignoruje ciężkie piksele!
                    var ds = DicomFile.Open(file, FileReadOption.SkipLargeTags).Dataset;
                    double z = 0;
                    if (ds.TryGetValues(DicomTag.ImagePositionPatient, out double[] pos)) z = pos[2];
                    else if (ds.TryGetSingleValue(DicomTag.SliceLocation, out double loc)) z = loc;
                    else z = (double)ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0);

                    sliceInfos[i] = new SliceInfo { FilePath = file, ZPosition = z };
                }
                catch { Debug.LogWarning("Failed to load: " + file); }
            });
        }, cancellationToken: ct);

        ct.ThrowIfCancellationRequested();

        var validSlices = sliceInfos.Where(s => s != null).ToList();
        if (validSlices.Count == 0) return null;
        Debug.Log($"[LoadDicomData] Wczytano metadane {validSlices.Count} plastrów.");

        var sortedFiles = validSlices.OrderBy(s => s.ZPosition).Select(s => s.FilePath).ToList();
        sliceInfos = null;
        System.GC.Collect();
        return sortedFiles;
    }

    /// <summary>
    /// Zwalnia WSZYSTKO, co należy do aktualnie wczytanej serii — bufory natywne, tekstury GPU,
    /// wydzielone obiekty i kosze, stan Pickera — i przywraca model do transformu bazowego. Wołane
    /// przed każdym wczytaniem (żeby dwie serie nigdy nie istniały naraz) oraz przy niszczeniu sceny.
    /// Bezpieczne do wywołania, gdy nic nie jest wczytane.
    /// </summary>
    public void UnloadCurrent()
    {
        _finishedRender = false;

        // Trwające liczenie Pickera / segmentacji sięgnęłoby po bufory zwalniane poniżej. Anulujemy
        // je ZANIM cokolwiek zwolnimy; bump generacji unieważnia też wynik maski, który mógłby
        // wrócić z tła już po podmianie serii.
        _pickCts?.Cancel();
        _pickCts?.Dispose();
        _pickCts = null;
        _morphologyGeneration++;

        // Wydzielone kawałki i kosze należą do POPRZEDNIEJ serii — ich pieceOwnerMask za chwilę
        // przestanie istnieć, więc muszą zniknąć ze sceny razem z nią.
        if (volumeObjectManager != null) volumeObjectManager.ResetAllDerivedObjects();

        if (_volumeHu.IsCreated) _volumeHu.Dispose();
        if (pieceOwnerMask.IsCreated) pieceOwnerMask.Dispose();
        if (maskLabels.IsCreated) maskLabels.Dispose();
        _maskLabelSizes = null;

        // Zapisane indeksy odnoszą się do wymiarów POPRZEDNIEJ serii — po wczytaniu innej wskazywałyby
        // zupełnie inne miejsca w wolumenie.
        EditHistory.Clear();

        morphPickedVoxel = null;
        morphPickedVoxelOwnerId = 0;
        morphMaskToKeep = 0;

        // RenderTexture nie znika z GC — bez jawnego Release() każde przeładowanie skanu zostawiałoby
        // po sobie komplet tekstur wolumetrycznych. Zerujemy referencje, bo wymiary NASTĘPNEJ serii
        // prawie na pewno będą inne, więc tekstury muszą powstać od nowa (patrz EnsureOwnerRenderTexture).
        if (_ownerTexture != null)     { _ownerTexture.Release();     _ownerTexture = null; }
        if (_occupancyTexture != null) { _occupancyTexture.Release(); _occupancyTexture = null; }
        if (_occupancyScratch != null) { _occupancyScratch.Release(); _occupancyScratch = null; }
        if (_maskTexture != null)      { Destroy(_maskTexture);       _maskTexture = null; }
        if (_volumeTexture != null)    { Destroy(_volumeTexture);     _volumeTexture = null; }

        // Materiał wciąż wskazuje na właśnie zniszczone tekstury, a wolumin będzie gotowy dopiero za
        // kilkadziesiąt sekund — chowamy go, zamiast renderować śmieci przez cały czas wczytywania.
        if (_cubeRenderer != null) _cubeRenderer.enabled = false;
        if (_instancedMaterial != null) VolumeObjectManager.ResetMorphologyMaskProperties(_instancedMaterial);

        // Bufory robocze morfologii (statyczne, Allocator.Persistent) — patrz VolumeMorphology.
        Helpers.VolumeMorphology.DisposeStaticBuffers();

        if (volumeCube != null)
        {
            volumeCube.transform.position   = _basePosition;
            volumeCube.transform.rotation   = _baseRotation;
            volumeCube.transform.localScale = _baseScale;
            originalPosition = _basePosition;
            originalRotation = _baseRotation;
            originalScale    = _baseScale;
            _hasValidTransform = false;
        }

        _seriesPath = null;
        _width = _height = _depth = 0;
    }

    // -----------------------------------------------------------------------
    private void LateUpdate()
    {
        if (!_finishedRender || volumeCube == null) return;

        Transform t = volumeCube.transform;
        bool ok = !float.IsNaN(t.position.x)   && !float.IsInfinity(t.position.x)  &&
                  !float.IsNaN(t.rotation.x)   && !float.IsInfinity(t.rotation.x)  &&
                  !float.IsNaN(t.localScale.x) && !float.IsInfinity(t.localScale.x) &&
                  t.localScale.x > 0.0001f;

        if (ok) { _lastValidPos = t.position; _lastValidRot = t.rotation; _lastValidScale = t.localScale; _hasValidTransform = true; }
        else if (_hasValidTransform) { t.position = _lastValidPos; t.rotation = _lastValidRot; t.localScale = _lastValidScale; }

        UpdateClipPlane();
        UpdateMorphologyMaskID();
    }

    [ContextMenu("Reset Position/Rotation/Scale")]
    public void ResetPosition()
    {
        if (volumeCube != null)
        {
            volumeCube.transform.position = originalPosition;
            volumeCube.transform.rotation = originalRotation;
            volumeCube.transform.localScale = originalScale;
        }
    }

    // -----------------------------------------------------------------------
    private async UniTask ExtractHounsfieldUnits(List<string> sortedFiles,
                                                 System.IProgress<LoadProgress> progress = null,
                                                 System.Threading.CancellationToken ct = default)
    {
        _depth  = sortedFiles.Count;
        
        var firstDs = DicomFile.Open(sortedFiles[0], FileReadOption.SkipLargeTags).Dataset;
        _width  = firstDs.GetSingleValue<int>(DicomTag.Columns);
        _height = firstDs.GetSingleValue<int>(DicomTag.Rows);

        if (firstDs.TryGetValues(DicomTag.PixelSpacing, out double[] spacing))
        { _pixelSpacingX = (float)spacing[0]; _pixelSpacingY = (float)spacing[1]; }

        if (sortedFiles.Count > 1)
        {
            var secondDs = DicomFile.Open(sortedFiles[1], FileReadOption.SkipLargeTags).Dataset;
            if (secondDs.TryGetValues(DicomTag.ImagePositionPatient, out double[] p1) &&
                firstDs.TryGetValues(DicomTag.ImagePositionPatient, out double[] p0))
                _sliceThickness = Mathf.Abs((float)(p1[2] - p0[2]));
            else
                _sliceThickness = firstDs.GetSingleValueOrDefault(DicomTag.SliceThickness, 1f);
        }
        else
        {
            _sliceThickness = firstDs.GetSingleValueOrDefault(DicomTag.SliceThickness, 1f);
        }

        if (_pixelSpacingX  <= 0) _pixelSpacingX  = 1f;
        if (_pixelSpacingY  <= 0) _pixelSpacingY  = 1f;
        if (_sliceThickness <= 0) _sliceThickness = 1f;

        int total           = _width * _height * _depth;
        
        // Alokacja w pamięci natywnej, omija stertę Mono zapobiegając OOM
        if (_volumeHu.IsCreated) _volumeHu.Dispose();
        if (pieceOwnerMask.IsCreated) pieceOwnerMask.Dispose();
        _volumeHu           = new NativeArray<short>(total, Allocator.Persistent);
        pieceOwnerMask      = new NativeArray<byte>(total, Allocator.Persistent); // zero-init = wszystko należy do głównego wolumenu

        int slicePx         = _width * _height;

        // Przetwarzamy plastry sekwencyjnie (lub w małych paczkach), 
        // aby uniknąć alokowania 1-2 GB pamięci NativeArray na raz, co powodowało OOM przy 684 plastrach.
        for (int z = 0; z < _depth; z++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new LoadProgress("Wczytywanie plastrów", z, _depth));

            string filePath = sortedFiles[z];
            float slope = 1.0f;
            float intercept = 0.0f;
            
            byte[] raw = null;
            int bitsStored = 16;
            bool isSigned = true;

            // Wyciąganie pikseli zwalniamy na ThreadPool, by nie blokować wątku głównego i zminimalizować peak GC
            await UniTask.RunOnThreadPool(() => 
            {
                var ds = DicomFile.Open(filePath).Dataset;
                slope = (float)ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
                intercept = (float)ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
                
                var pd = DicomPixelData.Create(ds);
                raw = pd.GetFrame(0).Data;
                bitsStored = pd.BitsStored;
                isSigned = pd.PixelRepresentation == PixelRepresentation.Signed;
            });

            using var nativeOutputs = new NativeArray<int>(slicePx, Allocator.Persistent);
            using var nativeBytes   = new NativeArray<byte>(raw.Length, Allocator.Persistent);
            
            nativeBytes.CopyFrom(raw);

            var job = new HounsfieldConversionJob
            {
                slope         = slope,
                intercept     = intercept,
                rawDicomBytes = nativeBytes,
                bitsStored    = bitsStored,
                isSigned      = isSigned,
                output        = nativeOutputs
            };

            // Czekamy na zakończenie Joba dla tego plastra
            await job.Schedule(slicePx, 64).ToUniTask(PlayerLoopTiming.Update);

            // Kopiujemy wynik
            int off = z * slicePx;
            for (int i = 0; i < slicePx; i++) 
            {
                _volumeHu[off + i] = (short)nativeOutputs[i];
            }
            
            // Wymuszamy wyczyszczenie referencji do surowych danych
            raw = null;
            
            // Co 50 plastrów puszczamy GC, żeby czyściło bufory fo-dicom (zapobiega to puchnięciu RAMu)
            if (z % 50 == 0) System.GC.Collect();
        }

        ct.ThrowIfCancellationRequested();

        short mn = short.MaxValue, mx = short.MinValue;
        foreach (short v in _volumeHu) { if (v < mn) mn = v; if (v > mx) mx = v; }
        Debug.Log($"HU range: [{mn}, {mx}]");

        progress?.Report(new LoadProgress("Budowanie tekstury 3D", 0, 0));
        await BuildTexture3D();
        progress?.Report(new LoadProgress("Gotowe", _depth, _depth));
    }

    // -----------------------------------------------------------------------
    private async UniTask BuildTexture3D()
    {
        int voxelCount = _width * _height * _depth;
        
        // R16 (unorm 16-bit) zamiast RFloat (32-bit): DOKŁADNIE ta sama znormalizowana wartość 0..1 po
        // stronie shadera, ale połowa pamięci i połowa przepustowości na każde pobranie tekstury —
        // a raymarching pobiera gęstość na każdym kroku każdego promienia, więc to jednocześnie
        // największa pozycja w budżecie RAM i realny zysk prędkości. 65536 poziomów na zakres HU
        // (~0,06 HU na poziom) jest bezpiecznie poniżej rozdzielczości samych danych CT — 12-bitowych.
        var voxels = new NativeArray<ushort>(voxelCount, Allocator.Persistent);
        try
        {
            await UniTask.RunOnThreadPool(() =>
            {
                float range    = Mathf.Max(1f, _huMax - _huMin);
                float invRange = 1f / range;
                for (int i = 0; i < voxelCount; i++)
                {
                    float norm = (_volumeHu[i] - _huMin) * invRange;
                    voxels[i] = (ushort)(Mathf.Clamp01(norm) * 65535f + 0.5f);
                }
            });

            if (_volumeTexture != null) Destroy(_volumeTexture);
            _volumeTexture             = new Texture3D(_width, _height, _depth, TextureFormat.R16, false);
            // NIE zapisujemy do sceny: to jest wolumen pacjenta zbudowany z wczytanego badania.
            // Bez tej flagi Unity potrafi go zserializować razem ze sceną (przez materiał, który go
            // trzyma) — plik sceny puchnie wtedy o setki megabajtów, a dane obrazowe wyciekają do
            // repozytorium mimo tego, że same pliki DICOM są w .gitignore. Zdarzyło się to naprawdę.
            _volumeTexture.hideFlags   = HideFlags.DontSave;
            _volumeTexture.wrapMode    = TextureWrapMode.Clamp;
            _volumeTexture.filterMode  = FilterMode.Bilinear;
            _volumeTexture.SetPixelData(voxels, 0);
            _volumeTexture.Apply(false, true);
        }
        finally
        {
            voxels.Dispose();
        }

        if (volumeCube == null) { Debug.LogError("volumeCube not assigned!"); return; }

        _cubeRenderer = volumeCube.GetComponent<Renderer>();
        if (_cubeRenderer != null)
        {
            // Create a single instance to avoid sharedMaterial/material conflicts
            _instancedMaterial = _cubeRenderer.material;

            // UnloadCurrent chowa model na czas wczytywania (materiał wskazuje wtedy na zwolnione
            // tekstury) — tu mamy komplet danych, więc wolno go pokazać z powrotem.
            _cubeRenderer.enabled = true;

            _instancedMaterial.SetTexture("_VolumeTex",       _volumeTexture);
            _instancedMaterial.SetFloat  ("_HUMin",           _huMin);
            _instancedMaterial.SetFloat  ("_HUMax",           _huMax);
            _instancedMaterial.SetVector ("_VolumeTex_TexelSize",
                new Vector4(1f / _width, 1f / _height, 1f / _depth, 0f));
            _instancedMaterial.SetFloat("_WindowCenter", 191f);
            _instancedMaterial.SetFloat("_WindowWidth",  353f);
            _instancedMaterial.SetFloat("_VesselMinNorm", HuToNormalized(VisibleMaterialThresholdHU));

            // Inicjalizujemy _MaskTex czarną teksturą 1x1x1, aby uniknąć błędów próbkowania na start
            Texture3D emptyMask = new Texture3D(1, 1, 1, TextureFormat.R8, false) { hideFlags = HideFlags.DontSave };
            emptyMask.SetPixelData(new byte[] { 0 }, 0);
            emptyMask.Apply();
            _instancedMaterial.SetTexture("_MaskTex", emptyMask);

            // Piece Ownership (patrz pieceOwnerMask/VolumeObjectManager) — na starcie WSZYSTKO należy do
            // głównego wolumenu (właściciel 0), a lokalna sub-region to identyczność (cały wolumen).
            _instancedMaterial.SetFloat("_OwnerFilterID", 0f);
            _instancedMaterial.SetVector("_SubLocalCenter", Vector4.zero);
            _instancedMaterial.SetVector("_SubLocalSize", new Vector4(1f, 1f, 1f, 0f));
        }

        EnsureOwnerRenderTexture();
        BuildOccupancyMap();
        ApplyRaymarchQuality();

        UpdateClipPlane();
        BakeTransferTexture();

        // Fizyczne proporcje z zachowaniem skali z edytora
        float pw = _width  * _pixelSpacingX;
        float ph = _height * _pixelSpacingY;
        float pd = _depth  * _sliceThickness;
        // Podstawą jest ZAWSZE skala bazowa (patrz Awake), nie ta z poprzedniej serii — inaczej każdy
        // kolejny skan skalowałby się względem proporcji poprzedniego.
        Vector3 scale = new Vector3(_baseScale.x, _baseScale.x * (ph / pw), _baseScale.x * (pd / pw));
        if (float.IsNaN(scale.y) || float.IsInfinity(scale.y)) scale = _baseScale;
        volumeCube.transform.localScale = scale;
        originalScale = scale; // Zapisujemy proporcjonalną skalę do resetu
        _lastValidScale    = scale;
        _lastValidPos      = volumeCube.transform.position;
        _lastValidRot      = volumeCube.transform.rotation;
        _hasValidTransform = true;

        // MRTK stabilizacja & gesty (ObjectManipulator)
        var objMan = volumeCube.GetComponent<MixedReality.Toolkit.SpatialManipulation.ObjectManipulator>();
        if (objMan == null)
        {
            // ObjectManipulator wymaga collidery
            if (volumeCube.GetComponent<Collider>() == null)
                volumeCube.gameObject.AddComponent<BoxCollider>();

            objMan = volumeCube.gameObject.AddComponent<MixedReality.Toolkit.SpatialManipulation.ObjectManipulator>();
        }

        // ZABEZPIECZENIE: cały raymarching interakcji (VolumePicker.RayBoxIntersect) zakłada, że
        // BoxCollider na volumeCube DOKŁADNIE pokrywa siatkę (-0.5..0.5 lokalnie, czyli Size=(1,1,1),
        // Center=(0,0,0)) — tak jak standardowy Unity Cube. Jeśli ten collider zostanie kiedykolwiek
        // ręcznie zmniejszony/przesunięty w Edytorze (realnie się zdarzyło: Size=(0.5,0.5,0.5) sprawiało,
        // że promienie w ogóle nie trafiały w kolider bliżej krawędzi/boku wolumenu — Cut/TunnelCut/
        // Picker/RemoveIsland po prostu "nie widziały" niczego poza samym środkiem), wymuszamy tu
        // poprawne wymiary przy każdym budowaniu wolumenu, więc błąd nie może się cicho powtórzyć.
        var volumeBoxCollider = volumeCube.GetComponent<BoxCollider>();
        if (volumeBoxCollider != null)
        {
            volumeBoxCollider.size = Vector3.one;
            volumeBoxCollider.center = Vector3.zero;
        }

        if (objMan != null)
        {
            objMan.HostTransform = volumeCube.transform;
            var rotC = volumeCube.GetComponent<MixedReality.Toolkit.SpatialManipulation.RotationAxisConstraint>();
            if (rotC != null) rotC.ConstraintOnRotation = (MixedReality.Toolkit.AxisFlags)0;
            var scaleC = volumeCube.GetComponent<MixedReality.Toolkit.SpatialManipulation.MinMaxScaleConstraint>();
            if (scaleC == null) scaleC = volumeCube.gameObject.AddComponent<MixedReality.Toolkit.SpatialManipulation.MinMaxScaleConstraint>();
            scaleC.MinimumScale = Vector3.one * 0.05f;
            scaleC.MaximumScale = Vector3.one * 5.0f;
            
            // Opcjonalnie dodaj BoundsControl dla białej ramki dookoła i rączek do skalowania/obrotu
            var boundsCtrl = volumeCube.GetComponent<MixedReality.Toolkit.SpatialManipulation.BoundsControl>();
            if (boundsCtrl == null) boundsCtrl = volumeCube.gameObject.AddComponent<MixedReality.Toolkit.SpatialManipulation.BoundsControl>();
        }

        CreateClipPlaneHandleIfNeeded();

        _finishedRender = true;
        Debug.Log($"Texture3D built. Scale={scale}");

        // Powiadomienie zewnętrznych słuchaczy (np. VolumePicker) że wolumin jest gotowy
        OnVolumeReady?.Invoke();
    }


    // -----------------------------------------------------------------------
    #region GPU Brush (Natychmiastowe Cięcie)

    /// <summary>
    /// Tworzy (jeśli jeszcze nie istnieje) RW RenderTexture dla _OwnerTex + znajduje kernele w
    /// PaintCutMask.compute. RenderTexture (NIE Texture3D) — bo WSZYSTKIE zapisy własności (pędzel,
    /// TunnelCut, masowa synchronizacja z pieceOwnerMask) idą przez compute shader, dokładnie jak
    /// dawna _cutsTexture (RenderTexture nie wspiera SetPixelData). To jest jedyne źródło
    /// NATYCHMIASTOWEJ widoczności cięcia/chowania — aktualizowane na GPU, w typowym przypadku
    /// (pędzel) wyłącznie w obrębie bounding boxa (PaintOwnerBrush), NIGDY pełnym reuploadem co
    /// klatkę. Pełna segmentacja morfologiczna (_MaskTex) nadal liczy się w tle, wolno, tylko na
    /// potrzeby Pick/RemoveIsland — nie wpływa na to, czy coś jest widoczne.
    /// </summary>
    private void EnsureOwnerRenderTexture()
    {
        _computeCutsSupported = SystemInfo.supportsComputeShaders && cutPaintCompute != null;

        if (!_computeCutsSupported)
        {
            Debug.LogWarning("[LoadDicomData] Compute shaders niedostępne lub 'Cut Paint Compute' nie jest przypisany w Inspektorze " +
                              "— malowanie pędzlem/TunnelCut nie będzie miało natychmiastowego efektu wizualnego.");
            return;
        }

        if (_ownerTexture == null)
        {
            _ownerTexture = new RenderTexture(_width, _height, 0, RenderTextureFormat.R8)
            {
                dimension         = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth       = _depth,
                enableRandomWrite = true,
                filterMode        = FilterMode.Point,
                wrapMode          = TextureWrapMode.Clamp
            };
            _ownerTexture.Create();
        }

        _paintOwnerKernel = cutPaintCompute.FindKernel("CSPaintOwnerBrush");
        _clearOwnerKernel = cutPaintCompute.FindKernel("CSClearOwner");
        _tunnelOwnerKernel = cutPaintCompute.FindKernel("CSTunnelOwner");
        _syncOwnerKernel = cutPaintCompute.FindKernel("CSSyncOwnerFromCpu");

        cutPaintCompute.SetTexture(_paintOwnerKernel, "_OwnerTexRW", _ownerTexture);
        cutPaintCompute.SetTexture(_tunnelOwnerKernel, "_OwnerTexRW", _ownerTexture);
        cutPaintCompute.SetTexture(_syncOwnerKernel, "_OwnerTexRW", _ownerTexture);
        cutPaintCompute.SetInts("_VolumeDims", _width, _height, _depth);

        // Nowa RenderTexture nie gwarantuje zainicjalizowania na zero — czyścimy jawnie, jednorazowo
        // (patrz komentarz przy CSClearOwner w PaintCutMask.compute). pieceOwnerMask (CPU) startuje
        // też na zero (alokacja w ExtractHounsfieldUnits), więc to jednocześnie poprawna
        // synchronizacja startowa CPU<->GPU.
        cutPaintCompute.SetTexture(_clearOwnerKernel, "_OwnerTexRW", _ownerTexture);
        cutPaintCompute.Dispatch(_clearOwnerKernel,
            Mathf.CeilToInt(_width  / 4f),
            Mathf.CeilToInt(_height / 4f),
            Mathf.CeilToInt(_depth  / 4f));

        if (_instancedMaterial != null) _instancedMaterial.SetTexture("_OwnerTex", _ownerTexture);
    }

    /// <summary>
    /// Synchronizuje CAŁĄ teksturę _OwnerTexRW WPROST z aktualnym CPU-owym pieceOwnerMask —
    /// używane przez WSZYSTKIE masowe, rzadkie zmiany własności (RemoveConnectedObjectAt,
    /// ExtractPickedIslandAsObject, Reset Cuts), gdzie CPU mutuje dowolne, nieciągłe
    /// zbiory wokseli naraz. Pełnowolumenowy dispatch — akceptowalne, bo to rzadka, ręcznie
    /// wywoływana operacja, nie coś per-klatka (w przeciwieństwie do PaintOwnerBrush/TunnelOwnerGPU,
    /// ograniczonych do bounding boxa pędzla/tunelu).
    /// </summary>
    private void SyncOwnerMaskToGPU()
    {
        if (!_computeCutsSupported || !pieceOwnerMask.IsCreated) return;

        Texture3D sourceTex = new Texture3D(_width, _height, _depth, TextureFormat.R8, false)
        {
            hideFlags = HideFlags.DontSave, // patrz komentarz przy _volumeTexture
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        sourceTex.SetPixelData(pieceOwnerMask, 0);
        sourceTex.Apply(false, true);

        cutPaintCompute.SetTexture(_syncOwnerKernel, "_SourceOwnerTex", sourceTex);
        cutPaintCompute.SetTexture(_syncOwnerKernel, "_OwnerTexRW", _ownerTexture);
        cutPaintCompute.SetInts("_VolumeDims", _width, _height, _depth);

        cutPaintCompute.Dispatch(_syncOwnerKernel,
            Mathf.CeilToInt(_width  / 4f),
            Mathf.CeilToInt(_height / 4f),
            Mathf.CeilToInt(_depth  / 4f));

        Destroy(sourceTex);

        // Własność właśnie się zmieniła masowo, więc mapy zajętości per obiekt są nieaktualne.
        // Muszą iść RAZEM z _OwnerTex: mapa jest konserwatywna, ale wyłącznie względem stanu, z
        // którego ją zbudowano — nieodświeżona kazałaby przeskakiwać bloki, w których materiał
        // dopiero co się pojawił, czyli objawiłaby się znikaniem materiału, nie samym spadkiem FPS.
        RebuildAllOwnerOccupancy();
    }

    /// <summary>
    /// Maluje elipsoidę pędzla wprost na GPU (_OwnerTexRW), tylko w obrębie jego bounding boxa —
    /// następca dawnego PaintCutsBrush. Wołane z VolumePicker.ApplyBrushAt zaraz po zapisaniu tych
    /// samych wokseli w pieceOwnerMask (CPU), żeby własność widoczna na GPU i ta używana przez
    /// segmentację (CPU) nigdy się nie rozjechały. `erase=true` (gumka RemoveIsland) przywraca do
    /// właściciela 0 TYLKO tam, gdzie aktualnie należy do Kosza — patrz CSPaintOwnerBrush.
    /// </summary>
    public void PaintOwnerBrush(int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                                 Vector3Int center, int rx, int ry, int rz, bool erase,
                                 byte sourceOwnerId, byte binOwnerId)
    {
        if (!_computeCutsSupported) return;

        int sizeX = maxX - minX + 1;
        int sizeY = maxY - minY + 1;
        int sizeZ = maxZ - minZ + 1;
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0) return;

        cutPaintCompute.SetInts("_BrushMin",    minX, minY, minZ);
        cutPaintCompute.SetInts("_BrushCenter", center.x, center.y, center.z);
        cutPaintCompute.SetInts("_BrushRadius", rx, ry, rz);
        cutPaintCompute.SetFloat("_PaintOwnerNorm", binOwnerId / 255f);
        cutPaintCompute.SetFloat("_SourceOwnerNorm", sourceOwnerId / 255f);
        cutPaintCompute.SetFloat("_CutBinOwnerNorm", binOwnerId / 255f);
        cutPaintCompute.SetFloat("_RestoreOwnerNorm", sourceOwnerId / 255f);
        cutPaintCompute.SetFloat("_EraseMode", erase ? 1f : 0f);

        int gx = Mathf.CeilToInt(sizeX / 4f);
        int gy = Mathf.CeilToInt(sizeY / 4f);
        int gz = Mathf.CeilToInt(sizeZ / 4f);
        cutPaintCompute.Dispatch(_paintOwnerKernel, gx, gy, gz);
    }

    /// <summary>
    /// "Usuwa" (w praktyce: chowa do Kosza — patrz VolumeObjectManager.GetOrCreateCutBinFor, odwracalne
    /// przez Reset Cuts) CAŁY fizycznie odłączony obiekt (wyspę) zawierający podany woksel — liczy
    /// łączność OD NOWA przy AccessoryPresenceThresholdHU + morphErosionRadius (patrz komentarz w treści metody),
    /// czysto geometrycznie/topologicznie, BEZ pasma gęstości wokół klikniętego punktu. To naprawia TRZY
    /// problemy starego podejścia (magic wand po HU seeda): (1) obiekt o zmiennej wewnętrznej gęstości
    /// (gąbczasta kość) nie rozpada się na warstwy, bo próg obecności łapie go w całości niezależnie od
    /// wahań gęstości w środku; (2) coś, co dotyka głównej struktury WYŁĄCZNIE przez skórę/tkankę miękką
    /// (np. maseczka przylegająca do twarzy) NIE jest już traktowane jak "ta sama wyspa" — cienki mostek
    /// skóry zostaje odcięty erozją PRZED liczeniem łączności, dotykanie bezpośrednio kością wciąż tak
    /// (trafia do "główna struktura" i zostaje bezpiecznie odrzucone, patrz niżej); (3) działa też dla
    /// akcesoriów RZADSZYCH niż skóra (np. piankowa poduszka), bo separacja nie zależy od gęstości
    /// akcesorium, tylko od grubości mostka. Coś naprawdę odseparowanego (choćby cienką szczeliną
    /// powietrza) trafia do INNEGO komponentu i usuwa się w całości bez ruszania reszty.
    /// </summary>
    public void RemoveConnectedObjectAt(Vector3Int voxel, byte ownerId = 0)
    {
        RemoveConnectedObjectAtAsync(voxel, ownerId).Forget();
    }

    /// <summary>
    /// "Usuwa" (chowa do Kosza, odwracalnie — patrz VolumeObjectManager.GetOrCreateCutBinFor/Reset Cuts)
    /// CAŁĄ wyspę aktualnie wyizolowaną Pickerem (morphPickedVoxel) — pozwala spickować obiekt
    /// (żeby zobaczyć CO dokładnie zostanie schowane, w izolacji od reszty), a potem schować go jednym
    /// przyciskiem, bez konieczności ponownego trafiania weń promieniem (Remove Island wymaga, żeby
    /// obiekt był aktualnie WIDOCZNY pod kursorem — po wyizolowaniu Pickerem często jest to niepraktyczne).
    ///
    /// Dwie ścieżki, zależnie CO jest aktualnie spickowane — OBIE liczą łączność identycznie (AccessoryPresenceThresholdHU
    /// jako próg obecności materiału + morphErosionRadius jako erozja tnąca cienkie mostki skóry PRZED CCL,
    /// czysto topologicznie, żadnego pasma gęstości ani domykania — patrz komentarz przy wywołaniu niżej
    /// dlaczego domykanie tu szkodzi), różni je tylko to, CZY liczenie jest wykonywane od nowa czy odtwarzane z podglądu:
    /// - Etykieta kostna (1..254, z GenerateMaskAsync) → RemoveConnectedObjectAt liczy łączność OD NOWA. Dawniej
    ///   używał SAMEGO VisibleMaterialThresholdHU (wysoki, 25 HU) bez erozji — skóra między dwoma fizycznie odrębnymi
    ///   obiektami liczyła się jako połączenie, więc obiekty, które segmentacja przypadkiem oznaczyła etykietą
    ///   kostną mimo że fizycznie to obce akcesorium (gęste plastikowe/metalowe części maseczki itp.), fałszywie
    ///   trafiały do "główna struktura" i Remove Island odmawiał usunięcia.
    /// - AccessoryPreviewLabel (255, z PickAccessoryIslandAt) → usuwamy DOKŁADNIE zaznaczenie z podglądu, bez
    ///   ponownego liczenia — to co Picker pokazał w izolacji, to dokładnie to, co tu znika.
    /// </summary>
    public void DeletePickedIsland()
    {
        DeletePickedIslandAsync().Forget();
    }

    /// <summary>
    /// Awaitowalny wariant DeletePickedIsland — dla UI, które musi wiedzieć, kiedy operacja faktycznie
    /// się skończyła (blokada przycisków, patrz App.VolumeSession).
    /// </summary>
    public async UniTask DeletePickedIslandAsync()
    {
        if (!morphPickedVoxel.HasValue)
        {
            Debug.LogWarning("[LoadDicomData] DeletePickedIsland: nic nie jest aktualnie spickowane (morphPickedVoxel jest puste). Najpierw użyj Pickera.");
            return;
        }

        int pIndex = VolumeSpaceTransform.GetFlatIndex(morphPickedVoxel.Value, _width, _height);
        if (maskLabels.IsCreated && pIndex >= 0 && pIndex < maskLabels.Length && maskLabels[pIndex] == AccessoryPreviewLabel)
        {
            await DeleteAccessorySelectionAsync();
        }
        else
        {
            await RemoveConnectedObjectAtAsync(morphPickedVoxel.Value, morphPickedVoxelOwnerId);
        }
    }

    /// <summary>
    /// Picker na obiekcie BEZ etykiety kostnej (maskLabels[seed] == 0) — np. guma maseczki, korek od linii
    /// do narkozy, łóżko skanera. Liczymy CZYSTO TOPOLOGICZNY komponent (ta sama metoda co dla głównej
    /// struktury, FindComponentContainingSeedAsync) przy AccessoryPresenceThresholdHU (próg OBECNOŚCI materiału,
    /// ustawiony nisko na stałe — łapie prawie wszystko poza powietrzem, WŁĄCZNIE z niskogęstościowymi
    /// akcesoriami jak piankowa poduszka) + morphErosionRadius (erozja PRZED CCL, tnie cienkie mostki skóry między głową a
    /// akcesorium — GEOMETRYCZNIE, nie progiem gęstości, więc działa niezależnie od tego, czy akcesorium
    /// jest gęstsze czy rzadsze niż skóra; TA SAMA gałka co podgląd segmentacji, żeby "co widać w podglądzie" i
    /// "co faktycznie da się usunąć" zawsze były zgodne). Fizyczna przerwa (np. między łóżkiem skanera a głową) i tak
    /// zawsze rozdzieli obiekty niezależnie od tych parametrów — to jest czysta topologia. NIE domykamy tu
    /// (celowo, closingRadius=0 w wywołaniu) — domykanie sklejałoby wąskie przerwy
    /// powietrza między dwoma różnymi obiektami ZANIM erozja separująca je zobaczy, co unieważniałoby całą
    /// separację niezależnie od promienia erozji; scalanie fragmentów TEGO SAMEGO obiektu (np.
    /// korek z maseczką, rozdrobnione szumem blisko progu) załatwia teraz drugi, nieerodowany przebieg CCL
    /// wewnątrz FindComponentContainingSeedAsync. Wynik zapisujemy jako doraźną etykietę 255 w maskLabels
    /// i SZYBKO (bez pełnej segmentacji) odświeżamy widok na GPU.
    /// </summary>
    public void PickAccessoryIslandAt(Vector3Int voxel, byte ownerId = 0)
    {
        PickAccessoryIslandAtAsync(voxel, ownerId).Forget();
    }

    // Wspólny token dla całej pracy zainicjowanej Pickerem (podgląd izolacji ORAZ usuwanie
    // spickowanej wyspy). Jedna operacja trwa sekundy, a liczy się wyłącznie wynik OSTATNIEGO
    // kliknięcia — bez anulowania kolejne kliknięcia ustawiały się w kolejce na semaforze
    // VolumeMorphology i każde odrabiało pełne liczenie po kolei, przez co aplikacja stawała.
    private System.Threading.CancellationTokenSource _pickCts;

    /// <summary>
    /// Anuluje poprzednią, wciąż trwającą operację Pickera i zwraca token dla nowej. Wołane na
    /// początku KAŻDEJ takiej operacji.
    /// </summary>
    private System.Threading.CancellationToken BeginNewPickOperation()
    {
        _pickCts?.Cancel();
        _pickCts?.Dispose();
        _pickCts = new System.Threading.CancellationTokenSource();
        return _pickCts.Token;
    }

    private async UniTask PickAccessoryIslandAtAsync(Vector3Int voxel, byte ownerId = 0)
    {
        if (!maskLabels.IsCreated || !_volumeHu.IsCreated) return;

        var ct = BeginNewPickOperation();

        int seedIndex = VolumeSpaceTransform.GetFlatIndex(voxel, _width, _height);

        // UWAGA: closingRadius=0 tu CELOWO. Domykanie (rozszerz-potem-zwęź) sklejałoby
        // NAPRAWDĘ WĄSKIE przerwy powietrza między dwoma fizycznie różnymi obiektami na etapie budowania maski obecności — ZANIM erozja separująca w ogóle
        // dostanie szansę ją zobaczyć, więc żaden erosionRadius by tego nie naprawił (dokładnie
        // ten bug: zwiększanie promienia separacji nie miało żadnego efektu, bo przerwa była już wcześniej
        // zasklepiona). Domykanie nie jest tu już potrzebne do sklejania szumu progowania (próg obecności jest
        // tak niski, że praktycznie nic realnego nie leży blisko niego) — a drugi przebieg CCL bez erozji
        // (patrz FindComponentContainingSeedAsync) i tak scala fragmenty JEDNEGO fizycznego obiektu.
        // ownerId ogranicza wyszukiwanie do wokseli NALEŻĄCYCH JUŻ do aktualnie celowanego obiektu
        // (0 = główny wolumen) — patrz pieceOwnerMask/FindComponentContainingSeedAsync.
        NativeArray<byte> accessoryMask;
        bool isMainBody;
        try
        {
            (accessoryMask, isMainBody) = await VolumeMorphology.FindComponentContainingSeedAsync(
                _volumeHu, _width, _height, _depth, AccessoryPresenceThresholdHU, seedIndex,
                Mathf.Max(morphExpandRadius, 1), _pixelSpacingX, _sliceThickness, morphErosionRadius,
                pieceOwnerMask, ownerId, ct);
        }
        catch (System.OperationCanceledException)
        {
            // Normalna ścieżka, nie błąd: użytkownik kliknął ponownie, zanim to liczenie się skończyło.
            Debug.Log("[LoadDicomData] Pick anulowany — zastąpiony nowszym kliknięciem.");
            return;
        }

        try
        {
            if (isMainBody)
            {
                // Kliknięty punkt łączy się (nawet po odcięciu cienkich mostków skóry erozją, patrz
                // morphErosionRadius) z największym komponentem — to nie odrębne akcesorium, tylko
                // coś fizycznie zrośnięte z główną strukturą. Nic nie izolujemy (ostrzeżenie już zalogowane
                // w FindComponentContainingSeedAsync).
                return;
            }

            int len = _width * _height * _depth;
            int count = 0;
            for (int i = 0; i < len; i++)
            {
                // Zamazujemy każde POPRZEDNIE doraźne zaznaczenie (AccessoryPreviewLabel) i wpisujemy nowe —
                // tylko jedno takie zaznaczenie może być aktywne naraz.
                if (accessoryMask[i] == 255) { maskLabels[i] = AccessoryPreviewLabel; count++; }
                else if (maskLabels[i] == AccessoryPreviewLabel) { maskLabels[i] = 0; }
            }

            if (count == 0)
            {
                Debug.LogWarning("[LoadDicomData] PickAccessoryIslandAt: nie znaleziono żadnej struktury powyżej Accessory Presence Threshold HU pod klikniętym punktem.");
                return;
            }

            morphPickedVoxel = voxel;
            morphPickedVoxelOwnerId = ownerId;
            morphMaskToKeep = AccessoryPreviewLabel;
            morphNegateMask = false;
            // Wymuszamy pełną izolację (nie "keep background") — inaczej skóra/tkanka miękka (maskID=0,
            // to samo co "tło") zostałaby widoczna OBOK wyizolowanego akcesorium, co zaprzeczałoby całemu
            // celowi tego podglądu: pokazać DOKŁADNIE to, co DeletePickedIsland za chwilę usunie.
            morphKeepBackground = false;
            UploadMaskLabelsToGPU();

            Debug.Log($"[LoadDicomData] Izolacja akcesorium: {count} wokseli jako doraźna wyspa (etykieta {AccessoryPreviewLabel}).");
        }
        finally
        {
            accessoryMask.Dispose();
        }
    }

    /// <summary>
    /// Szybki reupload _MaskTex WPROST z aktualnego CPU-owego maskLabels, bez ponownego liczenia
    /// segmentacji (erozja/CCL/dylatacja) — używane po doraźnej, lokalnej zmianie etykiet
    /// (PickAccessoryIslandAt), gdzie pełna regeneracja (sekundy) byłaby zbyt wolna dla podglądu na żywo.
    /// </summary>
    private void UploadMaskLabelsToGPU()
    {
        if (_instancedMaterial == null || !maskLabels.IsCreated) return;

        Texture3D maskTex = new Texture3D(_width, _height, _depth, TextureFormat.R8, false)
        {
            hideFlags = HideFlags.DontSave, // patrz komentarz przy _volumeTexture
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        maskTex.SetPixelData(maskLabels, 0);
        maskTex.Apply(false, true);

        if (_maskTexture != null) Destroy(_maskTexture);
        _maskTexture = maskTex;
        _instancedMaterial.SetTexture("_MaskTex", _maskTexture);
    }

    /// <summary>
    /// Wydziela AKTUALNIE SPICKOWANĄ wyspę (morphPickedVoxel) do osobnego, niezależnie ruchomego
    /// GameObjectu (patrz VolumeObjectManager) — lustrzane wobec DeletePickedIsland (te same dwie
    /// gałęzie: AccessoryPreviewLabel z PickAccessoryIslandAt vs. etykieta kostna wymagająca ponownego
    /// policzenia łączności), ale zamiast chowania do Kosza woksele dostają WŁASNEGO, nowego,
    /// niezależnie ruchomego właściciela w pieceOwnerMask.
    /// </summary>
    public void ExtractPickedIslandAsObject()
    {
        ExtractPickedIslandAsObjectAsync().Forget();
    }

    /// <summary>Awaitowalny wariant ExtractPickedIslandAsObject — patrz DeletePickedIslandAsync.</summary>
    public async UniTask ExtractPickedIslandAsObjectAsync()
    {
        if (!morphPickedVoxel.HasValue)
        {
            Debug.LogWarning("[LoadDicomData] ExtractPickedIslandAsObject: nic nie jest aktualnie spickowane (morphPickedVoxel jest puste). Najpierw użyj Pickera.");
            return;
        }

        int pIndex = VolumeSpaceTransform.GetFlatIndex(morphPickedVoxel.Value, _width, _height);
        if (maskLabels.IsCreated && pIndex >= 0 && pIndex < maskLabels.Length && maskLabels[pIndex] == AccessoryPreviewLabel)
        {
            await ExtractAccessorySelectionAsync(morphPickedVoxelOwnerId);
        }
        else
        {
            await ExtractConnectedObjectAtAsync(morphPickedVoxel.Value, morphPickedVoxelOwnerId);
        }
    }

    private async UniTask ExtractAccessorySelectionAsync(byte ownerId)
    {
        if (!pieceOwnerMask.IsCreated || !maskLabels.IsCreated) return;

        int len = _width * _height * _depth;
        var extractMask = new NativeArray<byte>(len, Allocator.Persistent);
        try
        {
            var labels = maskLabels;
            await UniTask.RunOnThreadPool(() =>
            {
                for (int i = 0; i < len; i++)
                    if (labels[i] == AccessoryPreviewLabel) extractMask[i] = 255;
            });

            await FinalizeExtractionAsync(extractMask, ownerId);
        }
        finally
        {
            extractMask.Dispose();
        }
    }

    /// <summary>
    /// Odpowiednik RemoveConnectedObjectAtAsync — TA SAMA reguła wyboru progu obecności materiału
    /// (etykieta kostna → morphThresholdHU, inaczej AccessoryPresenceThresholdHU) i TA SAMA topologia
    /// (erozja separacyjna morphErosionRadius PRZED CCL) — różni się tylko tym, co dzieje się z wynikiem:
    /// tu NIE kasujemy wokseli, tylko wydzielamy je jako osobny obiekt. `ownerId` ogranicza wyszukiwanie
    /// do wokseli NALEŻĄCYCH JUŻ do tego samego (aktualnie celowanego) obiektu — patrz
    /// FindComponentContainingSeedAsync/pieceOwnerMask — więc dalsze dzielenie JUŻ wydzielonego kawałka
    /// nigdy nie sięga do głównego wolumenu ani innego kawałka.
    /// </summary>
    private async UniTask ExtractConnectedObjectAtAsync(Vector3Int voxel, byte ownerId)
    {
        if (!pieceOwnerMask.IsCreated || !_volumeHu.IsCreated) return;

        int seedIndex = VolumeSpaceTransform.GetFlatIndex(voxel, _width, _height);

        byte seedLabel = maskLabels.IsCreated && seedIndex >= 0 && seedIndex < maskLabels.Length ? maskLabels[seedIndex] : (byte)0;
        bool seedIsLegitBone = seedLabel > 0 &&
            _volumeHu[seedIndex] >= morphThresholdHU &&
            GetMaskLabelSize(seedLabel) >= MinLegitBoneIslandVoxels;
        float presenceThresholdHU = seedIsLegitBone ? morphThresholdHU : AccessoryPresenceThresholdHU;

        var (componentMask, isMainBody) = await VolumeMorphology.FindComponentContainingSeedAsync(
            _volumeHu, _width, _height, _depth, presenceThresholdHU, seedIndex,
            Mathf.Max(morphExpandRadius, 1), _pixelSpacingX, _sliceThickness, morphErosionRadius,
            pieceOwnerMask, ownerId);

        try
        {
            if (isMainBody)
            {
                Debug.LogWarning("[LoadDicomData] ExtractPickedIslandAsObject: kliknięty punkt należy do GŁÓWNEJ struktury tego obiektu — nie można wydzielić całości (to opróżniłoby rodzica bez reszty). Użyj Cut, jeśli chcesz przyciąć tylko fragment.");
                return;
            }

            await FinalizeExtractionAsync(componentMask, ownerId);
        }
        finally
        {
            componentMask.Dispose();
        }
    }

    /// <summary>
    /// Wspólna końcówka obu ścieżek ekstrakcji: przydziela nowego właściciela, zapisuje go w
    /// pieceOwnerMask, synchronizuje GPU, liczy AABB wydzielonych wokseli w lokalnej przestrzeni
    /// -0.5..0.5 ORYGINALNEGO volumeCube (transform-niezmiennicza — patrz VolumeSpaceTransform.
    /// SubLocalToOriginalLocal) i każe VolumeObjectManager zmaterializować nowy, niezależnie
    /// chwytalny GameObject dokładnie w tym miejscu, gdzie fragment już wizualnie jest.
    ///
    /// `sourceOwnerId`: czyj obiekt był źródłem (0 = główna czaszka, N = wcześniej wydzielony kawałek
    /// ALBO któryś z koszy — kosz jest zwykłym obiektem, więc wydzielanie z niego działa identycznie).
    /// Świat (worldCenter/Rotation/Scale) liczymy WZGLĘDEM TRANSFORMU TEGO ŹRÓDŁA, nie zawsze względem
    /// volumeCube — bez tego dalsze dzielenie już przesuniętego/obróconego kawałka umieszczałoby nowy
    /// pod-kawałek w BŁĘDNYM miejscu (tam, gdzie by był, gdyby rodzic nigdy się nie ruszył).
    /// </summary>
    private async UniTask FinalizeExtractionAsync(NativeArray<byte> maskToExtract, byte sourceOwnerId)
    {
        if (volumeObjectManager == null) volumeObjectManager = FindObjectOfType<VolumeObjectManager>();
        if (volumeObjectManager == null)
        {
            Debug.LogError("[LoadDicomData] ExtractPickedIslandAsObject: brak VolumeObjectManager w scenie — nie można wydzielić obiektu.");
            return;
        }

        int len = _width * _height * _depth;
        // Identyfikatory przydziela WYŁĄCZNIE VolumeObjectManager — kawałki i kosze dzielą jedną pulę,
        // więc dwa niezależne liczniki mogłyby przydzielić ten sam numer dwóm różnym obiektom.
        byte newId = volumeObjectManager.AllocateOwnerId();
        if (newId == 0) return;

        Vector3 rotOffset = Vector3.zero;
        if (_instancedMaterial != null && _instancedMaterial.HasProperty("_RotationOffset"))
            rotOffset = _instancedMaterial.GetVector("_RotationOffset");

        var owners = pieceOwnerMask;
        int width = _width, height = _height, depth = _depth;

        // Zabezpieczenie: jeśli zaznaczenie obejmuje CAŁĄ aktualną zawartość źródła (np. użytkownik
        // trafił Pickerem powtórnie w JUŻ wydzielony kawałek i "wydzielił" go w całości ponownie),
        // to nie ma sensu — nowy wrapper zawierałby DOKŁADNIE to samo, a stary zostałby całkowicie
        // pusty (i niewidoczny, bo żaden woksel nie ma już jego OwnerId). Odmawiamy PRZED jakąkolwiek
        // mutacją pieceOwnerMask, więc źródło zostaje nietknięte.
        var (totalSourceOwned, candidateCount) = await UniTask.RunOnThreadPool(() =>
        {
            int total = 0, candidate = 0;
            for (int i = 0; i < len; i++)
            {
                if (owners[i] == sourceOwnerId) total++;
                if (maskToExtract[i] == 255) candidate++;
            }
            return (total, candidate);
        });

        if (totalSourceOwned > 0 && candidateCount >= totalSourceOwned)
        {
            Debug.LogWarning($"[LoadDicomData] ExtractPickedIslandAsObject: zaznaczenie obejmuje CAŁY źródłowy obiekt " +
                $"({candidateCount}/{totalSourceOwned} wokseli) — to by tylko przepakowało go w nowy wrapper, nic by nie " +
                "zostało w oryginale. Pomijam (jeśli chcesz dalej podzielić ten kawałek, zaznacz Pickerem tylko JEGO fragment).");
            morphPickedVoxel = null;
            morphPickedVoxelOwnerId = 0;
            morphMaskToKeep = 0;
            return;
        }

        EditHistory.Begin("Odłożenie struktury na bok");

        var (count, localMin, localMax) = await UniTask.RunOnThreadPool(() =>
        {
            int localCount = 0;
            Vector3 lMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 lMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < len; i++)
            {
                if (maskToExtract[i] != 255) continue;

                EditHistory.Record(i, owners[i]);
                owners[i] = newId;
                localCount++;

                int z = i / (width * height);
                int rem = i - z * width * height;
                int y = rem / width;
                int x = rem - y * width;

                Vector3 uvw = new Vector3((x + 0.5f) / width, (y + 0.5f) / height, (z + 0.5f) / depth);
                Vector3 localPos = VolumeSpaceTransform.UvwToLocal(uvw, rotOffset);

                if (localPos.x < lMin.x) lMin.x = localPos.x;
                if (localPos.y < lMin.y) lMin.y = localPos.y;
                if (localPos.z < lMin.z) lMin.z = localPos.z;
                if (localPos.x > lMax.x) lMax.x = localPos.x;
                if (localPos.y > lMax.y) lMax.y = localPos.y;
                if (localPos.z > lMax.z) lMax.z = localPos.z;
            }

            return (localCount, lMin, lMax);
        });

        if (count == 0)
        {
            EditHistory.Abort();
            Debug.LogWarning("[LoadDicomData] ExtractPickedIslandAsObject: zaznaczenie było puste, nic nie wydzielono.");
            return;
        }

        // Krok zamykamy dopiero po sprawdzeniu, czy operacja w ogóle coś zmieniła — pusty wpis
        // w historii oznaczałby cofnięcie, które nic nie robi.
        EditHistory.Commit();

        SyncOwnerMaskToGPU();

        Vector3 subLocalCenter = (localMin + localMax) * 0.5f;
        Vector3 subLocalSize = localMax - localMin;
        // Zabezpieczenie przed zerową rozciągłością (np. pojedyncza warstwa wokseli w jakiejś osi) —
        // zerowy rozmiar dałby w shaderze zdegenerowany box, w którym RayBoxIntersect nigdy niczego
        // by nie trafił.
        float minVoxelExtent = 1f / Mathf.Max(width, Mathf.Max(height, depth));
        subLocalSize = new Vector3(
            Mathf.Max(subLocalSize.x, minVoxelExtent),
            Mathf.Max(subLocalSize.y, minVoxelExtent),
            Mathf.Max(subLocalSize.z, minVoxelExtent));

        // Świat liczymy WZGLĘDEM TRANSFORMU ŹRÓDŁA (sourceOwnerId), nie zawsze volumeCube — patrz
        // komentarz przy sygnaturze metody. Domyślnie (źródło nie znalezione/główny wolumen) spada
        // to z powrotem do volumeCube.transform, identycznie jak dawniej.
        Transform sourceTransform = volumeCube.transform;
        Vector3 sourceSubCenter = Vector3.zero;
        Vector3 sourceSubSize = Vector3.one;
        if (sourceOwnerId != 0 && volumeObjectManager != null)
        {
            foreach (var t in volumeObjectManager.Targets)
            {
                if (t.OwnerId == sourceOwnerId)
                {
                    sourceTransform = t.ProxyTransform;
                    sourceSubCenter = t.SubLocalCenter;
                    sourceSubSize = t.SubLocalSize;
                    break;
                }
            }
        }

        Vector3 pieceLocalCenter = VolumeSpaceTransform.OriginalLocalToSubLocal(subLocalCenter, sourceSubCenter, sourceSubSize);
        Vector3 pieceLocalSize = VolumeSpaceTransform.OriginalLocalToSubLocal(subLocalCenter + subLocalSize, sourceSubCenter, sourceSubSize) - pieceLocalCenter;

        Quaternion worldRotation = sourceTransform.rotation;
        Vector3 worldScale = Vector3.Scale(sourceTransform.lossyScale, pieceLocalSize);

        // Przesuwamy nowy kawałek OBOK źródła zamiast zostawiać go DOKŁADNIE na miejscu (nakładając
        // się z oryginałem) — inaczej collider źródła zasłania to samo miejsce w przestrzeni i świeżo
        // wydzielony kawałek jest praktycznie niemożliwy do złapania osobno. Przesunięcie wzdłuż
        // lokalnej osi X źródła (jego "prawo"), o połowę szerokości źródła + połowę szerokości nowego
        // kawałka + mały margines — przybliżone (dla kawałka mocno przesuniętego od środka źródła nie
        // gwarantuje zerowego nakładania), ale w praktyce zawsze daje wyraźny, łatwy do złapania odstęp.
        float sourceHalfExtent = sourceTransform.lossyScale.x * 0.5f;
        float pieceHalfExtent = worldScale.x * 0.5f;
        float margin = Mathf.Max(pieceHalfExtent, sourceHalfExtent) * 0.3f;
        Vector3 offsetDir = sourceTransform.right;
        Vector3 worldCenter = sourceTransform.TransformPoint(pieceLocalCenter)
            + offsetDir * (sourceHalfExtent + pieceHalfExtent + margin);

        // Dalsze odpychanie od WSZYSTKICH już istniejących (widocznych) obiektów — bez tego kolejne
        // wydzielenia z TEGO SAMEGO źródła (np. wyciąganie kilku kawałków z Kosza pod rząd) lądowałyby
        // za każdym razem w tym samym miejscu obok źródła i zbierały się jedne na drugich. Przesuwamy
        // kandydata dalej wzdłuż TEGO SAMEGO kierunku (offsetDir), aż nie nachodzi na żaden istniejący
        // obiekt (przybliżenie kulami otaczającymi — promień = połowa długości przekątnej lossyScale)
        // — w praktyce nowe kawałki układają się w rządku obok źródła, każdy dalej niż poprzedni.
        float pieceRadius = worldScale.magnitude * 0.5f;
        const int maxPushIterations = 32;
        for (int iter = 0; iter < maxPushIterations; iter++)
        {
            bool overlapping = false;
            foreach (var t in volumeObjectManager.Targets)
            {
                if (!t.Visible || t.ProxyTransform == null) continue;
                float otherRadius = t.ProxyTransform.lossyScale.magnitude * 0.5f;
                float minDist = pieceRadius + otherRadius + margin;
                if ((worldCenter - t.ProxyTransform.position).sqrMagnitude < minDist * minDist)
                {
                    overlapping = true;
                    break;
                }
            }
            if (!overlapping) break;
            worldCenter += offsetDir * (pieceRadius + margin);
        }

        volumeObjectManager.SpawnPieceObject(newId, subLocalCenter, subLocalSize, worldCenter, worldRotation, worldScale,
            $"Fragment {newId}");

        Debug.Log($"[LoadDicomData] Wydzielono {count} wokseli jako nowy obiekt (właściciel {newId}).");

        morphPickedVoxel = null;
        morphPickedVoxelOwnerId = 0;
        morphMaskToKeep = 0;
    }

    /// <summary>
    /// "Usuwa" (w praktyce: chowa do Kosza — patrz VolumeObjectManager.GetOrCreateCutBinFor) DOKŁADNIE
    /// zaznaczenie z podglądu Pickera (AccessoryPreviewLabel). Nic nie jest kasowane trwale —
    /// materiał trafia do wspólnego Kosza, skąd nadal da się go obejrzeć/wydzielić pojedynczo.
    /// </summary>
    private async UniTask DeleteAccessorySelectionAsync()
    {
        if (!pieceOwnerMask.IsCreated || !maskLabels.IsCreated) return;

        int len = _width * _height * _depth;
        var owners = pieceOwnerMask;
        var labels = maskLabels;
        int modifiedCount = 0;

        // Materiał trafia do kosza TEGO obiektu, na którym zaznaczenie zrobiono (Pick zapamiętał go
        // w morphPickedVoxelOwnerId) — nie do jakiegoś wspólnego worka.
        byte sourceOwner = morphPickedVoxelOwnerId;
        byte binOwner = ResolveCutBinOwner(sourceOwner);
        if (binOwner == 0) return;

        EditHistory.Begin("Schowanie struktury do kosza");

        await UniTask.RunOnThreadPool(() =>
        {
            for (int i = 0; i < len; i++)
            {
                if (labels[i] == AccessoryPreviewLabel && owners[i] == sourceOwner)
                {
                    EditHistory.Record(i, owners[i]);
                    owners[i] = binOwner;
                    modifiedCount++;
                }
            }
        });

        EditHistory.Commit();
        SyncOwnerMaskToGPU();

        Debug.Log($"[LoadDicomData] Schowano do kosza obiektu {sourceOwner} doraźne zaznaczenie akcesorium: {modifiedCount} wokseli.");

        morphPickedVoxel = null;
        morphMaskToKeep = 0;
        await GenerateMorphologyMask();
    }

    private async UniTask RemoveConnectedObjectAtAsync(Vector3Int voxel, byte ownerId = 0)
    {
        if (!pieceOwnerMask.IsCreated || !_volumeHu.IsCreated) return;

        var ct = BeginNewPickOperation();

        int seedIndex = VolumeSpaceTransform.GetFlatIndex(voxel, _width, _height);

        // KTÓRY próg obecności materiału użyć zależy od tego, CO seed już jest: jeśli leży w PRAWDZIWEJ
        // etykiecie kostnej (ta sama definicja "prawdziwej" co IsLegitBoneLabel w VolumePicker: label>0,
        // HU >= morphThresholdHU, rozmiar wyspy >= MinLegitBoneIslandVoxels), liczymy łączność PRZY TYM
        // SAMYM progu i promieniu co segmentacja, która już tę izolację poprawnie policzyła (morphThresholdHU +
        // morphErosionRadius) — NIE przy dużo niższym AccessoryPresenceThresholdHU. Powód: przy progu blisko
        // powietrza cała skóra/tkanka miękka wokół głowy liczy się jako "materiał" i staje się jedną ciągłą
        // powłoką — obiekt stykający się z twarzą na SZEROKIEJ powierzchni (nie przez wąski mostek) zlewa się
        // z tą powłoką i ŻADEN promień erozji rozsądnej wielkości go nie odetnie, mimo że przy progu kostnym
        // (gdzie skóra w ogóle nie liczy się jako "materiał") ten sam obiekt poprawnie odseparował się jako
        // osobna wyspa — to właśnie dawało "Kliknięty punkt należy do GŁÓWNEJ struktury" mimo poprawnego
        // podglądu. Dla obiektów BEZ etykiety kostnej (np. piankowa poduszka, poniżej progu kostnego) nadal
        // trzeba niskiego progu — inaczej PickAccessoryIslandAt w ogóle by ich nie wykrył jako "coś".
        byte seedLabel = maskLabels.IsCreated && seedIndex >= 0 && seedIndex < maskLabels.Length ? maskLabels[seedIndex] : (byte)0;
        bool seedIsLegitBone = seedLabel > 0 &&
            _volumeHu[seedIndex] >= morphThresholdHU &&
            GetMaskLabelSize(seedLabel) >= MinLegitBoneIslandVoxels;
        float presenceThresholdHU = seedIsLegitBone ? morphThresholdHU : AccessoryPresenceThresholdHU;

        // closingRadius=0 CELOWO (patrz komentarz w PickAccessoryIslandAtAsync) — domykanie sklejałoby wąskie
        // przerwy powietrza między dwoma różnymi obiektami ZANIM erozja separująca je zobaczy, więc żaden
        // erosionRadius by tego nie naprawił. To samo dotyczy wysp z etykietą kostną, które okazały się
        // akcesorium (patrz IsLegitBoneLabel w VolumePicker — HU/rozmiar same w sobie nie odróżniają anatomii
        // od przedmiotu), nie tylko obiektów bez etykiety — dlatego RemoveConnectedObjectAt używa TEJ SAMEJ,
        // czysto topologicznej reguły co Picker na akcesorium, niezależnie od tego, jaką etykietę segmentacja
        // przypadkiem nadała.
        // ownerId ogranicza wyszukiwanie do wokseli NALEŻĄCYCH JUŻ do aktualnie celowanego obiektu
        // (0 = główny wolumen) — patrz pieceOwnerMask/FindComponentContainingSeedAsync.
        NativeArray<byte> componentMask;
        bool isMainBody;
        try
        {
            (componentMask, isMainBody) = await VolumeMorphology.FindComponentContainingSeedAsync(
                _volumeHu, _width, _height, _depth, presenceThresholdHU, seedIndex,
                Mathf.Max(morphExpandRadius, 1), _pixelSpacingX, _sliceThickness, morphErosionRadius,
                pieceOwnerMask, ownerId, ct);
        }
        catch (System.OperationCanceledException)
        {
            // Anulowane nowszym kliknięciem Pickera. Bezpieczne: maska własności jest mutowana dopiero
            // PO powrocie z liczenia, więc przerwanie w jego trakcie nie zostawia stanu w połowie drogi.
            Debug.Log("[LoadDicomData] Usuwanie wyspy anulowane — zastąpione nowszą operacją Pickera.");
            return;
        }

        try
        {
            if (isMainBody)
            {
                // Kliknięty punkt fizycznie należy do głównej struktury (czaszka + przylegająca
                // tkanka) — Remove Island celowo tego nie usuwa (ostrzeżenie już zalogowane w
                // FindComponentContainingSeedAsync). Do przycinania głównego obiektu służy Cut.
                return;
            }

            int len = _width * _height * _depth;
            var owners = pieceOwnerMask;
            int modifiedCount = 0;

            // Wyspa wraca do kosza obiektu, z którego ją zdjęto (ownerId to właściciel celowanego
            // obiektu, przekazany przez VolumePicker) — a nie do jednego wspólnego worka.
            byte binOwner = ResolveCutBinOwner(ownerId);
            if (binOwner == 0) return;

            EditHistory.Begin("Schowanie struktury do kosza");

            await UniTask.RunOnThreadPool(() =>
            {
                for (int i = 0; i < len; i++)
                {
                    if (componentMask[i] == 255 && owners[i] == ownerId)
                    {
                        EditHistory.Record(i, owners[i]);
                        owners[i] = binOwner;
                        modifiedCount++;
                    }
                }
            });

            EditHistory.Commit();

            // Natychmiastowa wizualizacja na GPU — BEZ TEGO pieceOwnerMask (CPU) jest poprawnie
            // zaktualizowany, ale niewidoczny dopóki nie skończy się wolna pełna regeneracja
            // _MaskTex — historycznie najczęstsze źródło "cięcie zadziałało, ale go nie widać".
            SyncOwnerMaskToGPU();

            Debug.Log($"[LoadDicomData] Schowano do Kosza połączony obiekt: {modifiedCount} wokseli.");
        }
        finally
        {
            componentMask.Dispose();
        }

        await GenerateMorphologyMask();
    }

    /// <summary>
    /// TunnelCut na GPU: wycina "na wylot" wzdłuż odcinka (przestrzeń wokseli) od wejścia do
    /// wyjścia promienia z bryły wolumenu. Dispatch obejmuje tylko bounding box odcinka+promienia,
    /// nie cały wolumen. Wołane z VolumePicker.ApplyTunnelCut zaraz przed (wolniejszą, w tle) aktualizacją CPU.
    /// </summary>
    public void TunnelOwnerGPU(Vector3 voxelStart, Vector3 voxelEnd, float radiusVoxels,
                               byte sourceOwnerId, byte binOwnerId)
    {
        if (!_computeCutsSupported)
        {
            Debug.LogWarning("[LoadDicomData] TunnelOwnerGPU: compute shaders NIEWSPIERANE (brak przypisanego Cut Paint Compute albo SystemInfo.supportsComputeShaders=false) — " +
                "tunel będzie widoczny dopiero po pełnym przeliczeniu maski (kilka sekund), nie natychmiast.");
            return;
        }

        int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(voxelStart.x, voxelEnd.x) - radiusVoxels), 0, _width  - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(voxelStart.x, voxelEnd.x) + radiusVoxels), 0, _width  - 1);
        int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(voxelStart.y, voxelEnd.y) - radiusVoxels), 0, _height - 1);
        int maxY = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(voxelStart.y, voxelEnd.y) + radiusVoxels), 0, _height - 1);
        int minZ = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(voxelStart.z, voxelEnd.z) - radiusVoxels), 0, _depth  - 1);
        int maxZ = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(voxelStart.z, voxelEnd.z) + radiusVoxels), 0, _depth  - 1);

        int sizeX = maxX - minX + 1, sizeY = maxY - minY + 1, sizeZ = maxZ - minZ + 1;
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
        {
            Debug.LogWarning($"[LoadDicomData] TunnelOwnerGPU: bounding box tunelu jest pusty/nieprawidłowy (sizeX={sizeX}, sizeY={sizeY}, sizeZ={sizeZ}) — nic nie zostanie schowane.");
            return;
        }

        cutPaintCompute.SetVector("_TunnelStart", voxelStart);
        cutPaintCompute.SetVector("_TunnelEnd", voxelEnd);
        cutPaintCompute.SetFloat("_TunnelRadius", radiusVoxels);
        cutPaintCompute.SetInts("_TunnelBoundsMin", minX, minY, minZ);
        cutPaintCompute.SetFloat("_PaintOwnerNorm", binOwnerId / 255f);
        cutPaintCompute.SetFloat("_SourceOwnerNorm", sourceOwnerId / 255f);

        cutPaintCompute.Dispatch(_tunnelOwnerKernel,
            Mathf.CeilToInt(sizeX / 4f),
            Mathf.CeilToInt(sizeY / 4f),
            Mathf.CeilToInt(sizeZ / 4f));
    }


    /// <summary>
    /// Zwraca OwnerId kosza, do którego ma trafić materiał wycinany z obiektu `sourceOwnerId`
    /// (tworząc ten kosz przy pierwszym cięciu z danego obiektu). Jedno miejsce decydujące "co gdzie
    /// ląduje" — używają go WSZYSTKIE ścieżki chowania: pędzel, TunnelCut, Usuń wyspę
    /// Zwraca 0, gdy kosza nie da się utworzyć — wywołujący ma wtedy NIC nie robić,
    /// zamiast po cichu skasować materiał, przypisując go byle komu.
    /// </summary>
    public byte ResolveCutBinOwner(byte sourceOwnerId)
    {
        if (volumeObjectManager == null) volumeObjectManager = FindObjectOfType<VolumeObjectManager>();
        if (volumeObjectManager == null)
        {
            Debug.LogError("[LoadDicomData] Brak VolumeObjectManager w scenie — nie mam gdzie schować wycinanego materiału.");
            return 0;
        }

        var bin = volumeObjectManager.GetOrCreateCutBinFor(sourceOwnerId);
        return bin != null ? bin.OwnerId : (byte)0;
    }

    /// <summary>
    /// Jak ResolveCutBinOwner, ale bez tworzenia kosza — zwraca 0, jeśli z danego obiektu nic jeszcze
    /// nie wycięto. Dla gumki (RemoveIsland): nie ma kosza, więc nie ma czego przywracać.
    /// </summary>
    public byte GetExistingCutBinOwner(byte sourceOwnerId)
    {
        if (volumeObjectManager == null) return 0;
        return volumeObjectManager.TryGetCutBin(sourceOwnerId, out var bin) ? bin.OwnerId : (byte)0;
    }

    private float HuToNormalized(float hu)
    {
        return (hu - _huMin) / Mathf.Max(1f, (_huMax - _huMin));
    }


    /// <summary>
    /// Przywraca CAŁĄ scenę do stanu początkowego: każdy woksel wraca do głównej czaszki (właściciel
    /// 0), a wszystkie kosze ORAZ wszystkie wydzielone fragmenty znikają ze sceny, zwalniając pulę
    /// identyfikatorów. Świadomie mocniejsze niż dawniej (kiedy Reset cofał tylko cięcia, zostawiając
    /// wydzielone obiekty) — teraz to jedno, przewidywalne "wróć do punktu wyjścia", bez resztek po
    /// poprzedniej sesji pracy. Wołane np. z przycisku UI "Reset Cuts"
    /// (patrz DynamicUIManager.OnResetCutsButtonPressed).
    /// </summary>
    [ContextMenu("Reset Cuts (przywróć stan początkowy)")]
    public void ResetCuts()
    {
        if (!pieceOwnerMask.IsCreated) return;
        ResetCutsAsync().Forget();
    }

    /// <summary>
    /// Cofa OSTATNIĄ zapamiętaną operację edycyjną (patrz EditHistory). Zwraca opis cofniętej
    /// operacji albo null, gdy nie było czego cofać.
    ///
    /// Po przywróceniu własności przechodzimy tą samą drogą co każda inna edycja: synchronizacja
    /// z GPU (żeby zmianę było widać natychmiast) i przeliczenie segmentacji (żeby wskazywanie
    /// struktur zgadzało się z tym, co widać).
    /// </summary>
    public async UniTask<string> UndoLastEditAsync()
    {
        if (!pieceOwnerMask.IsCreated) return null;
        if (!EditHistory.Undo(pieceOwnerMask, out string label)) return null;

        SyncOwnerMaskToGPU();
        await GenerateMorphologyMask();

        Debug.Log($"[LoadDicomData] Cofnięto: {label}.");
        return label;
    }

    /// <summary>Awaitowalny wariant ResetCuts — patrz DeletePickedIslandAsync.</summary>
    public async UniTask ResetCutsAsync()
    {
        var owners = pieceOwnerMask;
        int modifiedCount = 0;

        // Reset cofa WSZYSTKO naraz, więc pojedyncze kroki przestają mieć do czego się odnosić —
        // cofnięcie „ostatniego cięcia” po resecie przywróciłoby fragment stanu, którego już nie ma.
        EditHistory.Clear();

        // Pełny przebieg (potencjalnie 100M+ elementów) — ZAWSZE na wątku tła,
        // żeby nie zamrozić klatki przy dużych skanach.
        await UniTask.RunOnThreadPool(() =>
        {
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i] != 0)
                {
                    owners[i] = 0;
                    modifiedCount++;
                }
            }
        });

        SyncOwnerMaskToGPU();

        // Obiekty sceny muszą zniknąć RAZEM z wyzerowaniem własności — inaczej zostałyby puste
        // "duchy" (kosze i fragmenty, do których nie należy już ani jeden woksel), dalej chwytalne
        // i mylące. Kolejność: najpierw dane, potem scena, żeby nic nie renderowało stanu pośredniego.
        if (volumeObjectManager == null) volumeObjectManager = FindObjectOfType<VolumeObjectManager>();
        if (volumeObjectManager != null) volumeObjectManager.ResetAllDerivedObjects();

        // Podgląd izolacji też przestaje mieć sens — wskazywał na obiekt, którego już nie ma.
        morphPickedVoxel = null;
        morphPickedVoxelOwnerId = 0;
        morphMaskToKeep = 0;

        // Segmentacja (ID wysp) musi też wrócić do stanu sprzed cięć.
        await GenerateMorphologyMask();

        Debug.Log($"[LoadDicomData] Reset — {modifiedCount} wokseli wróciło do czaszki, wszystkie kosze i wydzielone fragmenty usunięte.");
    }

    #endregion

    // -----------------------------------------------------------------------
    #region Transfer Texture

    private void BakeTransferTexture()
    {
        int w = 512;
        if (_transferTexture == null)
        {
            _transferTexture            = new Texture2D(w, 1, TextureFormat.RGBA32, false);
            _transferTexture.hideFlags  = HideFlags.DontSave; // patrz komentarz przy _volumeTexture
            _transferTexture.wrapMode   = TextureWrapMode.Clamp;
            _transferTexture.filterMode = FilterMode.Bilinear;
        }

        Color[] colors = new Color[w];
        for (int i = 0; i < w; i++)
        {
            float t  = (float)i / (w - 1);
            float hu = Mathf.Lerp(_huMin, _huMax, t);

            // Kolory i progi zostają dokładnie takie, jak zdefiniowano dla absolutnych HU; okno
            // gęstości wpływa WYŁĄCZNIE na przezroczystość — patrz SetWindowCenter.
            Color c = GetColorForHU(hu);
            c.a *= WindowVisibility(hu);
            colors[i] = c;
        }
        _transferTexture.SetPixels(colors);
        _transferTexture.Apply();

        // Próg pomijania pustki liczymy WPROST z tej samej tablicy, która trafia do shadera — zamiast
        // zgadywać stałą, która rozjechałaby się przy każdej zmianie funkcji transferu. Szukamy
        // pierwszej gęstości dającej alfę powyżej 0.005, bo dokładnie tego progu używa pętla
        // raymarchingu jako minimum "czy to w ogóle coś wnosi". Cokolwiek poniżej jest niewidoczne
        // niezależnie od ustawień, więc blok o takim maksimum wolno przeskoczyć bez zmiany obrazu.
        _emptySkipDensity = 1f;
        for (int i = 0; i < w; i++)
        {
            if (colors[i].a > 0.005f)
            {
                _emptySkipDensity = (float)i / (w - 1);
                break;
            }
        }
        // Margines bezpieczeństwa na interpolację dwuliniową tekstury gęstości: próbka między
        // wokselami może wypaść wyżej niż maksimum bloku sąsiadującego z materiałem.
        _emptySkipDensity = Mathf.Max(0f, _emptySkipDensity - 0.01f);
        PushOccupancyPropertiesToMaterials();

        if (_instancedMaterial != null)
            _instancedMaterial.SetTexture("_TransferTex", _transferTexture);
        else if (_cubeRenderer != null)
            _cubeRenderer.sharedMaterial.SetTexture("_TransferTex", _transferTexture);
        else
            volumeMaterial.SetTexture("_TransferTex", _transferTexture);
    }

    /// <summary>
    /// Buduje zgrubną mapę zajętości (maksimum gęstości na blok 8^3) używaną przez raymarching do
    /// przeskakiwania powietrza. Skan CT to w większości powietrze, a dotąd każdy krok przez nie
    /// kosztował komplet pobrań tekstury nie wnosząc nic — to jest główny koszt renderowania na
    /// słabym sprzęcie XR. Mapa jest ~512x mniejsza od wolumenu, więc pamięciowo praktycznie darmowa.
    ///
    /// Zależy WYŁĄCZNIE od gęstości, więc cięcia/wydzielanie (które zmieniają własność, nie gęstość)
    /// NIE wymagają jej przebudowy — budujemy ją raz, po wczytaniu wolumenu.
    /// </summary>
    private void BuildOccupancyMap()
    {
        if (_volumeTexture == null) return;
        if (occupancyCompute == null)
        {
            Debug.LogWarning("[LoadDicomData] Brak przypisanego Occupancy Compute (Assets/Shaders/VolumeOccupancy.compute) — " +
                "renderowanie zadziała poprawnie, ale BEZ przeskakiwania pustki, czyli znacznie wolniej. Przypisz go w Inspektorze.");
            return;
        }

        int ow = Mathf.CeilToInt(_width  / (float)OccupancyBlock);
        int oh = Mathf.CeilToInt(_height / (float)OccupancyBlock);
        int od = Mathf.CeilToInt(_depth  / (float)OccupancyBlock);

        if (_occupancyTexture != null) { _occupancyTexture.Release(); _occupancyTexture = null; }
        _occupancyTexture = CreateOccupancyTexture(ow, oh, od);
        EnsureOccupancyScratch(ow, oh, od);

        int kernel = occupancyCompute.FindKernel("CSBuildOccupancy");
        occupancyCompute.SetTexture(kernel, "_VolumeTexRO", _volumeTexture);
        occupancyCompute.SetTexture(kernel, "_OccupancyTexRW", _occupancyScratch);
        occupancyCompute.SetInts("_VolumeDims", _width, _height, _depth);
        occupancyCompute.SetInts("_OccupancyDims", ow, oh, od);
        occupancyCompute.Dispatch(kernel,
            Mathf.CeilToInt(ow / 4f), Mathf.CeilToInt(oh / 4f), Mathf.CeilToInt(od / 4f));

        // Ta sama gwarancja co przy mapach per obiekt — halo szerokości komórki.
        DilateOccupancy(_occupancyScratch, _occupancyTexture, ow, oh, od);

        Debug.Log($"[LoadDicomData] Mapa zajętości zbudowana: {ow}x{oh}x{od} (blok {OccupancyBlock}^3, z marginesem 1 komórki) — raymarching przeskakuje teraz puste bloki.");
        PushOccupancyPropertiesToMaterials();
    }

    /// <summary>
    /// Rozsyła teksturę zajętości i jej parametry na materiał główny ORAZ na materiały wszystkich
    /// wydzielonych obiektów/koszy — te są klonami, więc referencje tekstur dziedziczą tylko te,
    /// które istniały w chwili klonowania; obiekty powstałe wcześniej trzeba doposażyć jawnie.
    /// </summary>
    private void PushOccupancyPropertiesToMaterials()
    {
        ApplyOccupancyTo(_instancedMaterial, _occupancyTexture);
        if (volumeObjectManager != null)
        {
            var targets = volumeObjectManager.Targets;
            for (int i = 0; i < targets.Count; i++)
                ApplyOccupancyTo(targets[i].Material, targets[i].Occupancy != null ? targets[i].Occupancy : _occupancyTexture);
        }
    }

    /// <summary>
    /// Buduje (lub odświeża) mapę zajętości JEDNEGO obiektu — bloki zawierające jego własny, widoczny
    /// materiał. To jest lekarstwo na drastyczny spadek FPS po pokazaniu kosza: kosz jest pudłem
    /// całego wolumenu o rzadkiej zawartości, a wspólna mapa gęstościowa uznawała za zajęte wszystkie
    /// bloki wypełnione czaszką, więc promień kosza maszerował przez nie pełnym krokiem tylko po to,
    /// żeby odrzucić każdą próbkę po właścicielu. Mapa per obiekt pozwala mu przelecieć te bloki
    /// jednym skokiem. Kosztuje ~25 KB na obiekt (siatka 512x mniejsza od wolumenu).
    /// </summary>
    public void RebuildOwnerOccupancy(Helpers.VolumeRenderTarget target)
    {
        if (target == null || occupancyCompute == null || _volumeTexture == null || _ownerTexture == null) return;

        int ow = Mathf.CeilToInt(_width  / (float)OccupancyBlock);
        int oh = Mathf.CeilToInt(_height / (float)OccupancyBlock);
        int od = Mathf.CeilToInt(_depth  / (float)OccupancyBlock);

        // Siatka musi odpowiadać BIEŻĄCEMU wolumenowi. Po wczytaniu innej serii stara tekstura wciąż
        // jest "created", tylko opisuje wymiary poprzedniego skanu — bez tego głównemu obiektowi
        // zostawałaby mapa o złej rozdzielczości (materiał znikałby blokami albo skakał FPS).
        if (target.Occupancy != null && target.Occupancy.IsCreated() &&
            (target.Occupancy.width != ow || target.Occupancy.height != oh || target.Occupancy.volumeDepth != od))
        {
            target.Occupancy.Release();
            target.Occupancy = null;
        }

        if (target.Occupancy == null || !target.Occupancy.IsCreated())
            target.Occupancy = CreateOccupancyTexture(ow, oh, od);

        EnsureOccupancyScratch(ow, oh, od);

        // Budujemy do bufora pośredniego, a dopiero rozszerzenie o jedną komórkę trafia do mapy,
        // z której korzysta shader — patrz CSDilateOccupancy.
        int kernel = occupancyCompute.FindKernel("CSBuildOwnerOccupancy");
        occupancyCompute.SetTexture(kernel, "_VolumeTexRO", _volumeTexture);
        occupancyCompute.SetTexture(kernel, "_OwnerTexRO", _ownerTexture);
        occupancyCompute.SetTexture(kernel, "_OccupancyTexRW", _occupancyScratch);
        occupancyCompute.SetInts("_VolumeDims", _width, _height, _depth);
        occupancyCompute.SetInts("_OccupancyDims", ow, oh, od);
        occupancyCompute.SetFloat("_TargetOwnerNorm", target.OwnerId / 255f);
        occupancyCompute.SetFloat("_SkipDensity", _emptySkipDensity);
        occupancyCompute.Dispatch(kernel,
            Mathf.CeilToInt(ow / 4f), Mathf.CeilToInt(oh / 4f), Mathf.CeilToInt(od / 4f));

        DilateOccupancy(_occupancyScratch, target.Occupancy, ow, oh, od);
        ApplyOccupancyTo(target.Material, target.Occupancy);
    }

    private RenderTexture CreateOccupancyTexture(int ow, int oh, int od)
    {
        var rt = new RenderTexture(ow, oh, 0, RenderTextureFormat.R8)
        {
            dimension         = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth       = od,
            enableRandomWrite = true,
            // Point, NIE Bilinear: chcemy wartość DOKŁADNIE tej komórki, w której jesteśmy.
            filterMode        = FilterMode.Point,
            wrapMode          = TextureWrapMode.Clamp
        };
        rt.Create();
        return rt;
    }

    private void EnsureOccupancyScratch(int ow, int oh, int od)
    {
        if (_occupancyScratch != null && _occupancyScratch.IsCreated() &&
            _occupancyScratch.width == ow && _occupancyScratch.height == oh && _occupancyScratch.volumeDepth == od)
            return;

        if (_occupancyScratch != null) _occupancyScratch.Release();
        _occupancyScratch = CreateOccupancyTexture(ow, oh, od);
    }

    /// <summary>
    /// Rozszerza mapę zajętości o jedną komórkę we wszystkich kierunkach — patrz CSDilateOccupancy.
    /// Dzięki temu poprawność przeskakiwania pustki nie zależy od idealnej zgodności odwzorowania
    /// komórka↔woksel między compute a samplerem, tylko jest zagwarantowana strukturalnie.
    /// </summary>
    private void DilateOccupancy(RenderTexture src, RenderTexture dst, int ow, int oh, int od)
    {
        int kernel = occupancyCompute.FindKernel("CSDilateOccupancy");
        occupancyCompute.SetTexture(kernel, "_OccupancySrc", src);
        occupancyCompute.SetTexture(kernel, "_OccupancyTexRW", dst);
        occupancyCompute.SetInts("_OccupancyDims", ow, oh, od);
        occupancyCompute.Dispatch(kernel,
            Mathf.CeilToInt(ow / 4f), Mathf.CeilToInt(oh / 4f), Mathf.CeilToInt(od / 4f));
    }

    /// <summary>
    /// Odświeża mapy zajętości wszystkich zarejestrowanych obiektów — wołane po operacjach masowo
    /// zmieniających własność (SyncOwnerMaskToGPU: usuwanie wyspy, ekstrakcja,
    /// Reset). Mapy zależą od własności, więc po takiej zmianie te stare pokazywałyby nieaktualny
    /// obraz tego, gdzie co jest — a że są binarne i konserwatywne, błąd objawiłby się znikaniem
    /// materiału, nie tylko spadkiem wydajności.
    /// </summary>
    public void RebuildAllOwnerOccupancy()
    {
        if (volumeObjectManager == null) return;
        var targets = volumeObjectManager.Targets;
        for (int i = 0; i < targets.Count; i++) RebuildOwnerOccupancy(targets[i]);
    }

    /// <summary>
    /// Doposaża świeżo sklonowany materiał wydzielonego obiektu/kosza w mapę zajętości. Klon
    /// Instantiate(InstancedMaterial) dziedziczy referencje tekstur, więc zwykle już ją ma — ale
    /// tylko jeśli mapa istniała w chwili klonowania. Wołane jawnie z VolumeObjectManager, żeby nie
    /// zależeć od kolejności inicjalizacji.
    /// </summary>
    public void ApplyOccupancyToClonedMaterial(Material mat)
    {
        // Na tym etapie obiekt nie ma jeszcze własnej mapy — dostaje wspólną (gęstościową), czyli
        // zachowanie poprawne, tylko nieoptymalne. Własną dostanie z RebuildOwnerOccupancy zaraz po
        // zarejestrowaniu, gdy znany jest już jego OwnerId.
        ApplyOccupancyTo(mat, _occupancyTexture);
        // Krok raymarchingu też jest ustawieniem wydajnościowym, nie właściwością materiału z
        // Inspektora — bez tego świeżo wydzielony obiekt renderowałby się w domyślnej, najgęstszej
        // jakości niezależnie od klasy urządzenia.
        ApplyStepSizeTo(mat);
    }

    /// <summary>
    /// Ustawia gęstość próbkowania raymarchingu (_StepSize) odpowiednio do klasy urządzenia. Koszt
    /// renderowania jest wprost proporcjonalny do liczby kroków na promień, więc to najprostsza
    /// dźwignia jakość↔płynność, jaką mamy — i jedyna, która pozwala tej samej scenie działać i na
    /// desktopie, i na HoloLens. Wołane po zbudowaniu wolumenu oraz z SetRaymarchQuality.
    /// </summary>
    /// <summary>
    /// Krok raymarchingu dla danego poziomu. Wydzielone z ApplyRaymarchQuality, żeby interfejs mógł
    /// pokazać, co poziomy właściwie znaczą, bez powielania tych liczb w drugim miejscu.
    /// </summary>
    public static float StepSizeFor(RaymarchQuality tier) => tier switch
    {
        RaymarchQuality.Low    => 0.0025f,
        RaymarchQuality.Medium => 0.0012f,
        _                      => 0.0005f
    };

    /// <summary>
    /// Ile próbek przypada na przekrój modelu przy danym poziomie. Bryła w przestrzeni lokalnej ma
    /// rozmiar 1.0, więc to po prostu odwrotność kroku — i jest to liczba znacznie bardziej mówiąca
    /// niż samo „Wysoka/Średnia/Niska".
    /// </summary>
    public static int SamplesPerModelFor(RaymarchQuality tier) =>
        Mathf.RoundToInt(1f / StepSizeFor(tier));

    /// <summary>Poziom faktycznie użyty — dla Auto rozwiązany po właściwościach sprzętu.</summary>
    public RaymarchQuality ResolvedRaymarchQuality { get; private set; } = RaymarchQuality.High;

    private void ApplyRaymarchQuality()
    {
        RaymarchQuality tier = raymarchQuality;
        if (tier == RaymarchQuality.Auto)
        {
            // Rozpoznanie po realnych właściwościach sprzętu, nie po nazwie platformy: build UWP
            // potrafi chodzić i na HoloLens, i przez Holographic Remoting z mocnego desktopa.
            bool weakGpu = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3
                        || SystemInfo.graphicsMemorySize <= 1024
                        || SystemInfo.processorCount <= 4;
            bool midGpu  = SystemInfo.graphicsMemorySize <= 4096;
            tier = weakGpu ? RaymarchQuality.Low : (midGpu ? RaymarchQuality.Medium : RaymarchQuality.High);
        }

        ResolvedRaymarchQuality = tier;
        float step = StepSizeFor(tier);
        _stepSizeInUse = step;

        // Limit iteracji MUSI wynikać z kroku, nie być stałą: promień musi móc przejść bryłę na wylot
        // po NAJDŁUŻSZEJ drodze, czyli przekątnej sqrt(3) w lokalnej przestrzeni -0.5..0.5. Zapas 1.3x
        // pokrywa jitter startowy i zaokrąglenia. Przy stałym limicie mniejszy krok (wyższa jakość)
        // paradoksalnie URYWAŁ promień w środku modelu, i to tylko pod kątami o najdłuższej drodze.
        _maxRayStepsInUse = Mathf.CeilToInt(1.7320508f / step * 1.3f);

        ApplyStepSizeTo(_instancedMaterial);
        if (volumeObjectManager != null)
        {
            var targets = volumeObjectManager.Targets;
            for (int i = 0; i < targets.Count; i++) ApplyStepSizeTo(targets[i].Material);
        }

        Debug.Log($"[LoadDicomData] Jakość raymarchingu: {raymarchQuality} → {tier}, _StepSize={step}, " +
                  $"limit kroków={_maxRayStepsInUse} " +
                  $"(GPU: {SystemInfo.graphicsDeviceType}, VRAM {SystemInfo.graphicsMemorySize} MB, rdzenie {SystemInfo.processorCount}).");
    }

    private float _stepSizeInUse = 0.0005f;
    private int _maxRayStepsInUse = 4096;

    private void ApplyStepSizeTo(Material mat)
    {
        if (mat == null) return;
        mat.SetFloat("_StepSize", _stepSizeInUse);
        mat.SetFloat("_MaxRaySteps", _maxRayStepsInUse);
    }

    /// <summary>Zmiana jakości w locie (np. z UI) — przelicza i rozsyła na wszystkie materiały.</summary>
    public void SetRaymarchQuality(RaymarchQuality quality)
    {
        raymarchQuality = quality;
        ApplyRaymarchQuality();
    }

    /// <summary>
    /// Rozsyła ponownie WSZYSTKIE ustawienia renderowania (krok, limit kroków, mapa zajętości i jej
    /// próg) na materiał główny i wszystkie klony — po zmianie przełącznika w Inspektorze w trakcie
    /// Play Mode, bez restartu sceny.
    /// </summary>
    public void RefreshRenderingSettings()
    {
        ApplyRaymarchQuality();
        PushOccupancyPropertiesToMaterials();
    }

    private void ApplyOccupancyTo(Material mat, RenderTexture occupancy)
    {
        if (mat == null) return;
        if (occupancy != null) mat.SetTexture("_OccupancyTex", occupancy);

        // Mapy per obiekt są BINARNE (0/1), wspólna mapa zapasowa trzyma maksimum gęstości — próg
        // musi pasować do tej, która faktycznie poszła na materiał.
        bool perOwner = occupancy != null && occupancy != _occupancyTexture;
        float threshold = occupancy == null ? -1f : (perOwner ? 0.5f : _emptySkipDensity);
        // Ujemny próg = shader całkowicie pomija gałąź przeskakiwania (patrz skipEnabled).
        if (!enableEmptySkipping) threshold = -1f;
        mat.SetFloat("_EmptySkipDensity", threshold);

        // Rozmiar komórki w UVW — shader liczy z niego dokładne wyjście z bieżącej komórki, zamiast
        // skakać o stałą długość (co przestrzeliwało w głąb następnego bloku i dawało paski).
        //
        // KRYTYCZNE: liczony z FAKTYCZNYCH wymiarów tekstury (1/ceil(dim/8)), a NIE jako 8/dim. Te dwie
        // wartości są równe tylko wtedy, gdy wymiar dzieli się przez 8; przy typowym skanie (np. 361
        // warstw) siatka DDA rozjeżdżałaby się z siatką tekseli i narastająco ją mijała, przez co
        // pomijane były bloki z materiałem. Że narastanie zależy od kierunku promienia, objawiało się
        // to prostokątnymi dziurami widocznymi tylko pod niektórymi kątami patrzenia.
        mat.SetVector("_OccupancyCellUVW", new Vector4(
            1f / Mathf.CeilToInt(_width  / (float)OccupancyBlock),
            1f / Mathf.CeilToInt(_height / (float)OccupancyBlock),
            1f / Mathf.CeilToInt(_depth  / (float)OccupancyBlock), 0f));
    }

    private Color GetColorForHU(float hu)
    {
                // Powietrze – całkowicie przezroczyste
                if (hu < -100f) return new Color(0,0,0,0);

                // Tłuszcz / tkanki podskórne –100..0 HU – ciemnawa, bardzo transparentna
                if (hu < 0f)
                {
                    float t = Mathf.InverseLerp(-100f, 0f, hu);
                    return new Color(0.45f, 0.28f, 0.15f, Mathf.Lerp(0f, 0.12f, t));
                }

                // Tkanki miękkie 0..80 HU – brązowo-pomarańczowe, semi-przezroczyste
                // Dają "ciało" widoczne przez okno body
                if (hu < 80f)
                {
                    float t = Mathf.InverseLerp(0f, 80f, hu);
                    Color soft = Color.Lerp(new Color(0.52f, 0.28f, 0.14f), new Color(0.65f, 0.32f, 0.12f), t);
                    return new Color(soft.r, soft.g, soft.b, Mathf.Lerp(0.10f, 0.22f, t));
                }

                // Naczynia krwionośne z kontrastem 80..250 HU
                // To jest kluczowy zakres dla widoczności naczyń
                if (hu < 250f)
                {
                    float t = Mathf.InverseLerp(80f, 250f, hu);
                    Color c = Color.Lerp(vesselColorLow, vesselColorHigh, t);
                    float a = Mathf.Lerp(0.35f, 0.95f, t);
                    return new Color(c.r, c.g, c.b, a);
                }

                // Przejście naczynia → kość 250..380 HU
                if (hu < 380f)
                {
                    float t = Mathf.InverseLerp(250f, 380f, hu);
                    Color bone   = new Color(0.88f, 0.80f, 0.62f);
                    Color c = Color.Lerp(vesselColorHigh, bone, t * t); // kwadratowe = ostre przejście
                    float a = Mathf.Lerp(0.90f, 0.98f, t);
                    return new Color(c.r, c.g, c.b, a);
                }

                // Kość zbita 380..800 HU – kremowo-złota, opaque
                if (hu < 800f)
                {
                    float t = Mathf.InverseLerp(380f, 800f, hu);
                    Color c = Color.Lerp(new Color(0.88f, 0.80f, 0.62f), new Color(0.95f, 0.90f, 0.78f), t);
                    return new Color(c.r, c.g, c.b, Mathf.Lerp(0.88f, 0.97f, t));
                }

                // Bardzo twarda kość / metal > 800 HU – biały
                return new Color(0.97f, 0.95f, 0.90f, 1.0f);
    }

    #endregion

    // -----------------------------------------------------------------------
    #region Clip plane & public setters

    private void UpdateClipPlane()
    {
        if (_instancedMaterial == null) return;

        // Jeśli uchwyt istnieje, JEST źródłem prawdy dla płaszczyzny (dowolna orientacja ustawiona
        // ręcznie przez użytkownika) — inaczej fallback na starą, wyłącznie poziomą płaszczyznę
        // sterowaną suwakiem Cut Height (patrz SetCutHeight).
        Vector3 normal, point;
        if (_clipPlaneHandle != null)
        {
            normal = _clipPlaneHandle.transform.forward;
            point  = _clipPlaneHandle.transform.position;
        }
        else
        {
            normal = Vector3.up;
            point  = volumeCube.transform.position + Vector3.up * cutHeight;
        }

        float d = -Vector3.Dot(normal, point);
        Vector4 plane = new Vector4(normal.x, normal.y, normal.z, d);

        // Wyłączona płaszczyzna musi dawać warunek ZAWSZE FAŁSZYWY. Shader odrzuca próbkę, gdy
        // dot(normal, worldPos) + w > 0, więc przesunięcie w stronę DODATNIĄ odcina wszystko —
        // dokładnie tak zniknęła kiedyś cała czaszka. Ujemna, bardzo odległa płaszczyzna zostawia
        // scenę po właściwej stronie niezależnie od tego, gdzie stoi model.
        if (!_clipPlaneEnabled) plane = new Vector4(0f, 1f, 0f, -1e6f);

        _instancedMaterial.SetVector("_ClipPlane", plane);

        // Wydzielone obiekty i kosze mają WŁASNE klony materiału — bez tego przekrój działałby
        // wyłącznie na głównej bryle, a odłożony na bok fragment zostawałby nieprzecięty.
        if (volumeObjectManager == null) return;
        var targets = volumeObjectManager.Targets;
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].Material != null) targets[i].Material.SetVector("_ClipPlane", plane);
    }

    /// <summary>
    /// Tworzy przeciągalny/obracalny uchwyt (płaski Quad + ObjectManipulator) reprezentujący
    /// płaszczyznę przekroju widoku — czysto wizualny odpowiednik istniejącego suwaka Cut Height,
    /// ale z pełną swobodą orientacji (nie tylko pozioma płaszczyzna). Dziecko volumeCube, żeby
    /// przekrój podążał za obróconą/przesuniętą czaszką zamiast zostawać "przyklejony" do świata.
    /// Startowa pozycja/rotacja odtwarza DOKŁADNIE stare zachowanie (pozioma płaszczyzna na
    /// wysokości cutHeight), więc nic się wizualnie nie zmienia, dopóki użytkownik nie złapie uchwytu.
    /// </summary>
    private void CreateClipPlaneHandleIfNeeded()
    {
        if (_clipPlaneHandle != null || volumeCube == null) return;

        _clipPlaneHandle = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _clipPlaneHandle.name = "ClipPlaneHandle";
        Object.Destroy(_clipPlaneHandle.GetComponent<Collider>()); // zamieniamy na grubszy BoxCollider niżej — łatwiej złapać

        // Wypełniona powierzchnia przesłaniała dokładnie to, po co robi się przekrój — wnętrze
        // czaszki. Zostaje sama ramka: pokazuje położenie i orientację płaszczyzny, a widok przez
        // nią jest całkowicie czysty. Siatka Quada służy już tylko jako odniesienie dla rogów.
        var renderer = _clipPlaneHandle.GetComponent<MeshRenderer>();
        renderer.enabled = false;

        Shader lineShader = Shader.Find("Sprites/Default");
        if (lineShader != null)
        {
            var frame = _clipPlaneHandle.AddComponent<LineRenderer>();
            frame.material = new Material(lineShader);
            frame.startColor = frame.endColor = new Color(0.35f, 0.8f, 1f, 0.9f);

            // Współrzędne lokalne: ramka ma podążać za uchwytem przy obrocie i przesunięciu bez
            // przeliczania jej punktów za każdym razem.
            frame.useWorldSpace = false;
            frame.loop = true;
            frame.positionCount = 4;
            frame.SetPositions(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f)
            });

            // Szerokość w jednostkach lokalnych, więc skaluje się razem z uchwytem — stała wartość
            // w metrach byłaby albo niewidoczna na małym modelu, albo gruba jak belka na dużym.
            frame.widthMultiplier = 0.02f;
            frame.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            frame.receiveShadows = false;
            frame.alignment = LineAlignment.TransformZ;
        }
        else
        {
            // Bez shadera ramki nie ma czym narysować — lepiej pokazać półprzezroczystą taflę niż nic.
            renderer.enabled = true;
            Debug.LogWarning("[LoadDicomData] Shader 'Sprites/Default' niedostępny — płaszczyzna przekroju " +
                             "będzie wypełniona zamiast obrysowana ramką.");
        }

        var handleCollider = _clipPlaneHandle.AddComponent<BoxCollider>();
        handleCollider.size = new Vector3(1f, 1f, 0.05f);

        _clipPlaneHandle.transform.SetParent(volumeCube.transform, false);
        _clipPlaneHandle.transform.localScale = Vector3.one * 1.5f; // trochę większy niż wolumin, żeby dobrze było widać orientację
        // Świat, nie lokalnie: odtwarzamy dokładnie starą formułę (worldPos + up*cutHeight), a forward
        // ma wskazywać world-up na start (Quad domyślnie patrzy lokalnie w -Z, stąd jawny LookRotation).
        // Uchwyt startuje UKRYTY: przekrój jest narzędziem doraźnym, a półprzezroczysty prostokąt
        // wiszący nad modelem i niczego nieprzecinający wygląda jak usterka. Pokazuje go dopiero
        // włączenie przekroju w panelu albo w menu na dłoni.
        _clipPlaneHandle.SetActive(_clipPlaneEnabled);
        ApplyClipPlaneFromSettings();

        var handleManip = _clipPlaneHandle.AddComponent<MixedReality.Toolkit.SpatialManipulation.ObjectManipulator>();
        handleManip.HostTransform = _clipPlaneHandle.transform;
        var handleScaleC = _clipPlaneHandle.AddComponent<MixedReality.Toolkit.SpatialManipulation.MinMaxScaleConstraint>();
        // Skala uchwytu jest czysto kosmetyczna (nie wpływa na nieskończoną płaszczyznę w shaderze) —
        // blokujemy ją, żeby przypadkowe uszczypnięcie nie robiło z uchwytu ledwo widocznego punktu.
        handleScaleC.MinimumScale = _clipPlaneHandle.transform.localScale;
        handleScaleC.MaximumScale = _clipPlaneHandle.transform.localScale;
    }

    /// <summary>
    /// Rozprowadza stan podglądu izolacji (Pick) na materiały WSZYSTKICH obiektów wolumetrycznych —
    /// pokazując go DOKŁADNIE na tym, na którym kliknięto (morphPickedVoxelOwnerId), a nie zawsze na
    /// głównej czaszce. Wcześniej te uniformy szły WYŁĄCZNIE do _instancedMaterial (materiał czaszki),
    /// więc Pick wewnątrz Kosza albo wydzielonego kawałka zapalał podgląd izolacji na CZASZCE, podczas
    /// gdy na faktycznie klikniętym obiekcie nic się nie działo — stąd wrażenie, że "picker/cut na
    /// osobnych obiektach wywołuje reakcję na głównym renderze". Materiały pozostałych obiektów są
    /// jawnie zerowane, żeby dwa obiekty nigdy nie pokazywały izolacji naraz.
    /// </summary>
    // Ostatnio ROZESŁANY stan podglądu izolacji. Ta metoda leci z Update() co klatkę, a od czasu
    // wprowadzenia wielu obiektów przechodzi po CAŁEJ ich liście robiąc po kilka SetFloat na każdym —
    // czyli stały koszt CPU i ruch na materiałach za każdą klatkę, mimo że te wartości zmieniają się
    // wyłącznie przy kliknięciu Pickerem. Zapamiętujemy je i wychodzimy od razu, gdy nic się nie zmieniło.
    private int _lastAppliedMaskState = int.MinValue;
    private int _lastAppliedTargetCount = -1;

    private void UpdateMorphologyMaskID()
    {
        int state = morphMaskToKeep
                  ^ (morphPickedVoxelOwnerId << 9)
                  ^ (morphKeepBackground ? 1 << 17 : 0)
                  ^ (morphNegateMask ? 1 << 18 : 0)
                  ^ (morphExtraHide1 << 19) ^ (morphExtraHide2 << 21) ^ (morphExtraHide3 << 23)
                  // VisibleMaterialThresholdHU steruje _VesselMinNorm i jest polem Inspektora — bez niego
                  // w stanie jego pokręcanie w Play Mode przestałoby cokolwiek zmieniać w obrazie.
                  ^ VisibleMaterialThresholdHU.GetHashCode();

        var targets = volumeObjectManager != null ? volumeObjectManager.Targets : null;
        int targetCount = targets != null ? targets.Count : 0;

        // Liczba obiektów też jest częścią stanu — nowy kawałek/kosz musi dostać komplet uniformów
        // nawet wtedy, gdy sam podgląd izolacji się nie zmienił.
        if (state == _lastAppliedMaskState && targetCount == _lastAppliedTargetCount) return;
        _lastAppliedMaskState = state;
        _lastAppliedTargetCount = targetCount;

        if (targets != null && targets.Count > 0)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.Material == null) continue;

                if (t.OwnerId == morphPickedVoxelOwnerId) ApplyMorphologyMaskProperties(t.Material);
                else Helpers.VolumeObjectManager.ResetMorphologyMaskProperties(t.Material);

                // Nie należy do izolacji — to zwykły próg renderowania naczyń, wspólny dla wszystkich.
                t.Material.SetFloat("_VesselMinNorm", HuToNormalized(VisibleMaterialThresholdHU));
            }
            return;
        }

        // Zanim VolumeObjectManager zdąży zarejestrować obiekty (albo gdy go w scenie nie ma) —
        // zachowanie jak dawniej, tylko główny materiał.
        if (_instancedMaterial != null)
        {
            ApplyMorphologyMaskProperties(_instancedMaterial);
            _instancedMaterial.SetFloat("_VesselMinNorm", HuToNormalized(VisibleMaterialThresholdHU));
        }
    }

    private void ApplyMorphologyMaskProperties(Material mat)
    {
        float currentVal = mat.GetFloat("_MaskIDToKeep");
        if (Mathf.Abs(currentVal - morphMaskToKeep) > 0.1f)
        {
            Debug.Log($"[Morphology] Ustawianie _MaskIDToKeep na: {morphMaskToKeep}");
            mat.SetFloat("_MaskIDToKeep", morphMaskToKeep);
        }
        mat.SetFloat("_MaskKeepBackground", morphKeepBackground ? 1f : 0f);
        mat.SetFloat("_MaskNegate", morphNegateMask ? 1f : 0f);
        mat.SetFloat("_MaskExtraHide1", morphExtraHide1);
        mat.SetFloat("_MaskExtraHide2", morphExtraHide2);
        mat.SetFloat("_MaskExtraHide3", morphExtraHide3);
    }

    [ContextMenu("Wygeneruj Maskę Morfologiczną")]
    public async UniTask GenerateMorphologyMask()
    {
        if (_volumeHu == null || _volumeHu.Length == 0)
        {
            Debug.LogError("Brak danych DICOM do przetworzenia!");
            return;
        }

        _morphologyGeneration++;
        int currentGen = _morphologyGeneration;

        Debug.Log("Rozpoczynam proces generowania maski morfologicznej...");
        try 
        {
            if (!maskLabels.IsCreated || maskLabels.Length != _width * _height * _depth)
            {
                if (maskLabels.IsCreated) maskLabels.Dispose();
                maskLabels = new NativeArray<byte>(_width * _height * _depth, Allocator.Persistent);
            }

            var result = await VolumeMorphology.GenerateMaskAsync(_volumeHu, maskLabels, _width, _height, _depth, morphThresholdHU, morphErosionRadius, morphExpandRadius, _pixelSpacingX, _sliceThickness, pieceOwnerMask, 0);

            if (currentGen != _morphologyGeneration)
            {
                // Nowsze żądanie już przetwarza dane, ignorujemy ten wynik, żeby nie nadpisać nowszej maski.
                if (result.mask != null) Destroy(result.mask);
                return;
            }

            _maskLabelSizes = result.labelSizesById;
            
            // AUTOMATYCZNE ŚLEDZENIE: Jeżeli mamy wskazany konkretny woksel, zaktualizuj MaskToKeep
            // na to ID, w którym aktualnie ten woksel się znajduje! Zapobiega to gubieniu czaszki przy cięciu.
            //
            // WAŻNE rozróżnienie: to "ratunkowe" doszukiwanie się najbliższej oznaczonej struktury
            // (i twardy fallback na ID 1) ma sens TYLKO gdy śledzimy GŁÓWNĄ strukturę (ID 1 — po
            // RemapLabelsAsync zawsze największa ocalała bryła, czyli w praktyce czaszka). Tam
            // "zgubienie" jest prawie na pewno tylko zmianą numeru ID po resortowaniu, nie realnym
            // zniknięciem obiektu — stary bug, który to zabezpieczenie miało naprawić.
            // Jeśli jednak użytkownik świadomie wyizolował Pickerem INNĄ, mniejszą wyspę (np. maseczkę)
            // i tnie właśnie ją, jej zniknięcie może być zupełnie realne (właśnie ją całą wyciął) —
            // wtedy "doszukiwanie się najbliższej oznaczonej struktury" bez pytania podmieniało widok
            // (i to, co Cut faktycznie może trafić — patrz IsVoxelVisibleUnderMask) na PRZYPADKOWĄ
            // sąsiednią strukturę, najczęściej właśnie czaszkę — użytkownik dalej tnie w tym samym
            // miejscu ekranu, myśląc że wciąż izoluje maseczkę, a w rzeczywistości wcina się w czaszkę,
            // bez żadnego ostrzeżenia. Dla wyspy innej niż główna, gubiąc śledzenie, po prostu wracamy
            // do widoku "pokaż wszystko" (ID 0) zamiast zgadywać inny obiekt.
            if (morphPickedVoxel.HasValue)
            {
                bool wasTrackingMainBody = morphMaskToKeep == 1;
                int pIndex = VolumeSpaceTransform.GetFlatIndex(morphPickedVoxel.Value, _width, _height);
                byte newId = maskLabels[pIndex];

                if (newId == 0 && wasTrackingMainBody)
                {
                    // Śledzony woksel głównej struktury właśnie zniknął spod tego ID (prawie na pewno
                    // tylko resortowanie numeracji, nie realne usunięcie) — szukamy najbliższego wciąż
                    // oznaczonego woksela w okolicy i dalej śledzimy JEGO. Promień 24 (nie 12) — przy
                    // wycinaniu całego płata jednym ciągłym ruchem CAŁE najbliższe otoczenie punktu
                    // bywa wycięte naraz, więc mały promień i tak zawodzi.
                    var nearby = FindNearestLabeledVoxel(maskLabels, morphPickedVoxel.Value, _width, _height, _depth, 24);
                    if (nearby.HasValue)
                    {
                        morphPickedVoxel = nearby.Value;
                        pIndex = VolumeSpaceTransform.GetFlatIndex(nearby.Value, _width, _height);
                        newId = maskLabels[pIndex];
                    }
                    else
                    {
                        // TWARDE ZABEZPIECZENIE: śledzenie całkowicie zgubione (całe otoczenie
                        // wycięte). Spadamy na ID 1 — po RemapLabelsAsync to ZAWSZE największa
                        // ocalała struktura (prawie na pewno wciąż czaszka).
                        Debug.LogWarning("[Morphology] Śledzenie głównej struktury całkowicie zgubione (całe otoczenie punktu zostało wycięte) — " +
                            "przełączam na ID 1 (największa ocalała struktura), żeby model nie zniknął. Użyj ponownie Pickera, żeby precyzyjnie wskazać właściwy fragment.");
                        morphPickedVoxel = null;
                        newId = 1;
                    }
                }
                else if (newId == 0)
                {
                    // Śledzona MNIEJSZA wyspa (nie główna struktura) zniknęła spod tym ID — najpewniej
                    // została właśnie w całości wycięta. NIE zgadujemy sąsiedniej struktury (mogłaby to
                    // być czaszka) — wracamy do "pokaż wszystko", żeby Cut nie zaczął w ciszy trafiać
                    // w coś zupełnie innego niż to, co użytkownik świadomie wyizolował.
                    Debug.Log("[Morphology] Śledzona wyspa zniknęła (prawdopodobnie w całości wycięta) — wracam do widoku 'pokaż wszystko'.");
                    morphPickedVoxel = null;
                    morphMaskToKeep = 0;
                }

                if (newId > 0 && newId != morphMaskToKeep)
                {
                    Debug.Log($"[Morphology] Aktualizacja ID po cięciu: {morphMaskToKeep} -> {newId}");
                    morphMaskToKeep = newId;
                }
            }
            
            if (_maskTexture != null) Destroy(_maskTexture);
            _maskTexture = result.mask;
            if (morphologyStatsText != null) morphologyStatsText.text = result.stats;
            
            if (_instancedMaterial != null)
            {
                _instancedMaterial.SetTexture("_MaskTex", _maskTexture);
                Debug.Log("[Morphology] Maska została przypisana do shadera!");
            }
            else 
            {
                Debug.LogWarning("[Morphology] _instancedMaterial jest nullem! Maska nie została przypisana.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Morphology] Błąd podczas generowania maski: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Szuka najbliższego woksela z etykietą (label > 0) wokół danego punktu, przeszukując rosnące
    /// "powłoki" sześcianu (promień 1, 2, 3...) zamiast całej bryły — tanie (O(maxRadius^3) w
    /// najgorszym razie), bo działa tylko w małej okolicy, nie po całym wolumenie.
    /// </summary>
    private static Vector3Int? FindNearestLabeledVoxel(NativeArray<byte> labels, Vector3Int start, int width, int height, int depth, int maxRadius)
    {
        int startIndex = VolumeSpaceTransform.GetFlatIndex(start, width, height);
        if (startIndex >= 0 && startIndex < labels.Length && labels[startIndex] > 0) return start;

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        // Tylko powierzchnia bieżącej "powłoki" — wnętrze już sprawdziliśmy przy mniejszym r.
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r && Mathf.Abs(dz) != r) continue;

                        int x = start.x + dx, y = start.y + dy, z = start.z + dz;
                        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth) continue;

                        int idx = z * width * height + y * width + x;
                        if (labels[idx] > 0) return new Vector3Int(x, y, z);
                    }
                }
            }
        }
        return null;
    }

    public void SetCutHeight(float value)
    {
        cutHeight = value;
        ApplyClipPlaneFromSettings();
    }

    /// <summary>
    /// Oś, wzdłuż której tnie płaszczyzna przekroju: 0 = X (lewo-prawo), 1 = Y (góra-dół),
    /// 2 = Z (przód-tył). Osie są liczone WZGLĘDEM MODELU, nie świata — inaczej po obróceniu
    /// czaszki „przekrój poprzeczny” przestawałby być poprzeczny.
    /// </summary>
    public int ClipPlaneAxis
    {
        get => _clipPlaneAxis;
        set { _clipPlaneAxis = Mathf.Clamp(value, 0, 2); ApplyClipPlaneFromSettings(); }
    }

    /// <summary>
    /// Czy płaszczyzna cokolwiek odcina. Wyłączona chowa też uchwyt — wiszący nad modelem
    /// półprzezroczysty prostokąt, który niczego nie przecina, wygląda jak usterka.
    /// </summary>
    public bool ClipPlaneEnabled
    {
        get => _clipPlaneEnabled;
        set
        {
            _clipPlaneEnabled = value;
            if (_clipPlaneHandle != null) _clipPlaneHandle.SetActive(value);
            ApplyClipPlaneFromSettings();
        }
    }

    private int _clipPlaneAxis = 1;
    private bool _clipPlaneEnabled;

    /// <summary>
    /// Ustawia uchwyt zgodnie z osią i przesunięciem. Uchwyt zostaje jedynym źródłem prawdy dla
    /// shadera (patrz UpdateClipPlane) — dzięki temu sterowanie suwakiem z panelu i chwytanie
    /// dłonią w goglach opisują ten sam stan, zamiast walczyć o pierwszeństwo.
    ///
    /// Przesunięcie jest w jednostkach BRYŁY, nie świata: model ma różne wymiary fizyczne
    /// w każdej osi, więc ten sam suwak musi przejechać przez całą czaszkę niezależnie od tego,
    /// którą oś się wybierze.
    /// </summary>
    private void ApplyClipPlaneFromSettings()
    {
        if (_clipPlaneHandle == null || volumeCube == null) return;

        Transform vt = volumeCube.transform;
        Vector3 baseDir = _clipPlaneAxis switch
        {
            0 => vt.right,
            2 => vt.forward,
            _ => vt.up
        };
        float extent = _clipPlaneAxis switch
        {
            0 => vt.lossyScale.x,
            2 => vt.lossyScale.z,
            _ => vt.lossyScale.y
        };

        // Oś to punkt wyjścia, a nie ograniczenie — dwa kąty pozwalają ustawić dowolne nachylenie
        // bez chwytania uchwytu, czyli także na komputerze, gdzie chwytanie nie działa.
        Vector3 sideRef = Mathf.Abs(Vector3.Dot(baseDir, vt.forward)) > 0.9f ? vt.up : vt.forward;
        Vector3 tiltAxis = Vector3.Cross(baseDir, sideRef).normalized;

        Quaternion tilt = Quaternion.AngleAxis(_clipPlanePitch, tiltAxis) *
                          Quaternion.AngleAxis(_clipPlaneYaw, baseDir);
        Vector3 normal = tilt * baseDir;

        _clipPlaneHandle.transform.position = vt.position + normal * (cutHeight * extent * 0.5f);
        _clipPlaneHandle.transform.rotation = Quaternion.LookRotation(normal, sideRef);
    }

    /// <summary>Nachylenie płaszczyzny względem wybranej osi, w stopniach.</summary>
    public float ClipPlanePitch
    {
        get => _clipPlanePitch;
        set { _clipPlanePitch = value; ApplyClipPlaneFromSettings(); }
    }

    /// <summary>Obrót płaszczyzny wokół wybranej osi, w stopniach.</summary>
    public float ClipPlaneYaw
    {
        get => _clipPlaneYaw;
        set { _clipPlaneYaw = value; ApplyClipPlaneFromSettings(); }
    }

    private float _clipPlanePitch;
    private float _clipPlaneYaw;

    public void SetSurfaceThreshold(float value)
    {
        SetFloatOnAllVolumeMaterials("_SurfaceThreshold", value);
    }

    /// <summary>
    /// Środek okna gęstości (HU) — razem z SetWindowWidth decyduje, które gęstości są widoczne.
    ///
    /// UWAGA na architekturę: aktywny shader (RaymarchCT_Simplified) CELOWO nie remapuje gęstości
    /// przez okno — jego funkcja ApplyWindowLevel jest martwa, bo funkcja transferu jest budowana
    /// z ABSOLUTNYCH wartości HU (progi 250 HU = naczynia, 380 = kość). Przemapowanie wejścia
    /// rozjechałoby te progi. Dlatego okno działa tutaj, po stronie C#, jako wygaszanie
    /// przezroczystości przy budowaniu funkcji transferu: paleta i progi zostają nietknięte, a to,
    /// co poza oknem, po prostu przestaje zasłaniać. Uniformy shadera ustawiamy nadal, żeby wartości
    /// zgadzały się po obu stronach, gdyby shader kiedyś zaczął ich używać.
    /// </summary>
    public void SetWindowCenter(float valueHU)
    {
        _windowCenterHU = valueHU;
        SetFloatOnAllVolumeMaterials("_WindowCenter", valueHU);
        if (_transferTexture != null) BakeTransferTexture();
    }

    /// <summary>Szerokość okna gęstości (HU) — patrz SetWindowCenter.</summary>
    public void SetWindowWidth(float valueHU)
    {
        _windowWidthHU = Mathf.Max(1f, valueHU);
        SetFloatOnAllVolumeMaterials("_WindowWidth", _windowWidthHU);
        if (_transferTexture != null) BakeTransferTexture();
    }

    /// <summary>
    /// Ile zostaje z widoczności struktur LEŻĄCYCH POZA oknem. Zero kazałoby im zniknąć zupełnie, co
    /// w obrazie trójwymiarowym gubi kontekst anatomiczny — przy oknie mózgowym czaszka ma zblednąć
    /// i przestać zasłaniać, a nie wyparować, bo bez niej nie wiadomo, na co się patrzy.
    /// </summary>
    [Header("Okno gęstości")]
    [Range(0f, 1f)] public float windowOutsideOpacity = 0.12f;

    // Pełny zakres HU na starcie: okno było dotąd przez renderer ignorowane, więc stanem faktycznym
    // jest „nic nie wygaszone”. Wartości z materiału celowo tego nie zmieniają — inaczej włączenie
    // tego mechanizmu samo z siebie przerysowałoby model, którego nikt nie prosił o zmianę.
    private float _windowCenterHU = 1000f;
    private float _windowWidthHU = 6000f;

    /// <summary>
    /// Waga widoczności dla danej gęstości: 1 wewnątrz okna, windowOutsideOpacity daleko poza nim,
    /// z płynnym przejściem na krawędziach (ostra granica dawałaby widoczny „schodek” na modelu).
    /// </summary>
    private float WindowVisibility(float hu)
    {
        float half = Mathf.Max(1f, _windowWidthHU * 0.5f);
        float wMin = _windowCenterHU - half;
        float wMax = _windowCenterHU + half;
        float feather = Mathf.Max(1f, _windowWidthHU * 0.1f);

        float rising = Mathf.InverseLerp(wMin - feather, wMin + feather, hu);
        float falling = 1f - Mathf.InverseLerp(wMax - feather, wMax + feather, hu);
        float inside = Mathf.Clamp01(rising) * Mathf.Clamp01(falling);

        return Mathf.Lerp(Mathf.Clamp01(windowOutsideOpacity), 1f, inside);
    }

    /// <summary>
    /// Ustawia parametr renderowania na materiale głównego wolumenu ORAZ na materiałach wszystkich
    /// wydzielonych obiektów i koszy. Te są klonami zrobionymi w chwili wydzielenia, więc późniejsza
    /// zmiana na materiale głównym sama z siebie do nich nie dociera — bez tego suwak działałby na
    /// czaszce, a wydzielony fragment obok zostawałby wyrenderowany po staremu.
    /// </summary>
    private void SetFloatOnAllVolumeMaterials(string property, float value)
    {
        if (_instancedMaterial != null) _instancedMaterial.SetFloat(property, value);

        if (volumeObjectManager == null) return;
        var targets = volumeObjectManager.Targets;
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].Material != null) targets[i].Material.SetFloat(property, value);
    }


    /// <summary>
    /// Ustawia oba kolory naczyń WPROST, bez przechodzenia przez odcień. Potrzebne do przywracania
    /// stanu wyjściowego: kolory domyślne mają własne nasycenie (np. 0.9), a droga przez sam odcień
    /// wymusza nasycenie 1.0 — powrót „do domyślnych” dawałby więc kolor podobny, ale nie ten sam.
    /// </summary>
    public void SetVesselColors(Color low, Color high)
    {
        vesselColorLow = low;
        vesselColorHigh = high;
        BakeTransferTexture();
    }

    // Ustawienie niezależnych kolorów dla naczyń nisko- i wysokogęstościowych
    public void SetVesselColorLowHue(float hueNorm)
    {
        // 1.0Saturation, 1.0Value dają intensywny, żywy kolor (np. mocny pomarańcz/czerwień)
        vesselColorLow = Color.HSVToRGB(hueNorm, 1.0f, 1.0f);
        BakeTransferTexture();
    }

    public void SetVesselColorHighHue(float hueNorm)
    {
        // Tutaj nasycenie lekko zmniejszone (0.85) żeby kolor gęstych struktur nadal różnił się głębią
        vesselColorHigh = Color.HSVToRGB(hueNorm, 0.85f, 1.0f);
        BakeTransferTexture();
    }

    #endregion

    // -----------------------------------------------------------------------
    private void OnValidate()
    {
        if (_transferTexture != null && _cubeRenderer != null)
            BakeTransferTexture();
            
        UpdateMorphologyMaskID();
    }

    private void OnDestroy()
    {
        if (_volumeHu.IsCreated) _volumeHu.Dispose();
        if (pieceOwnerMask.IsCreated) pieceOwnerMask.Dispose();
        if (maskLabels.IsCreated) maskLabels.Dispose();
        if (_ownerTexture != null) { _ownerTexture.Release(); _ownerTexture = null; }
        if (_occupancyTexture != null) { _occupancyTexture.Release(); _occupancyTexture = null; }
        if (_occupancyScratch != null) { _occupancyScratch.Release(); _occupancyScratch = null; }
        // Przerywa liczenie Pickera trwające w chwili niszczenia sceny — bez tego operacja dobiegłaby
        // końca i sięgnęła po zwolnione już poniżej bufory natywne.
        _pickCts?.Cancel();
        _pickCts?.Dispose();
        _pickCts = null;
        // VolumeMorphology trzyma własne bufory statyczne (NativeArray, Allocator.Persistent) —
        // w przeciwieństwie do dawnych zarządzanych tablic, GC ich nie posprząta. Patrz też
        // Assets/Editor/VolumeMorphologyEditorCleanup.cs (dodatkowa siatka na wypadek wyłączonego
        // Reload Domain w Enter Play Mode Settings).
        Helpers.VolumeMorphology.DisposeStaticBuffers();
    }
}