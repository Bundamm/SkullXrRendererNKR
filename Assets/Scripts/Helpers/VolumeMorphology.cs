using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;

namespace Helpers
{
    public static class VolumeMorphology
    {
        // Cache statyczny, by zlikwidować 9 GB alokacji pamięci podczas malowania.
        // NativeArray (nie zarządzane byte[]/int[]/ushort[]) — potrzebne, żeby te bufory mogły być polami
        // jobów Burst. W przeciwieństwie do zarządzanych tablic (sprzątanych przez GC), NativeArray z
        // Allocator.Persistent WYMAGA jawnego Dispose() — patrz EnsureArrays/DisposeStaticBuffers.
        private static int s_cachedLen = -1;
        private static NativeArray<byte> s_origMask;
        private static NativeArray<byte> s_erodedMask;
        private static NativeArray<byte> s_temp1;
        private static NativeArray<byte> s_temp2;
        private static NativeArray<int> s_labels;
        private static NativeArray<byte> s_residueMask;
        // ushort (nie byte): odległość geodezyjna w DilateLabelsAsync/ExpandLabelsAsync bywa liczona bez
        // ograniczenia promienia (patrz FindComponentContainingSeedAsync) — byte nasycałby się na 255 i
        // odtwarzanie etykiet urywałoby się w połowie długiego, cienkiego fragmentu (efekt "poszarpania").
        private static NativeArray<ushort> s_dist;
        private static NativeArray<int> s_uf;

        // Chroni dostęp do powyższych tablic statycznych — GenerateMaskAsync i FindComponentContainingSeedAsync
        // współdzielą te same bufory, żeby uniknąć drugiej, ~kilkusetmegabajtowej alokacji
        // dla tej samej operacji. Bez tej blokady, wywołanie ich w tym samym momencie nadpisywałoby
        // sobie nawzajem dane w trakcie liczenia. Semafor asynchroniczny — nie jest przywiązany do wątku,
        // więc nadal poprawnie serializuje dostęp mimo że wnętrze korzysta teraz z JobHandle.Schedule()
        // zamiast Task.Run (harmonogramowanie/Complete() Joba i tak musi zajść na wątku głównym).
        private static readonly System.Threading.SemaphoreSlim s_gate = new System.Threading.SemaphoreSlim(1, 1);

        private static void EnsureArrays(int len)
        {
            // Sprawdzamy NIE TYLKO długość, ale i .IsCreated — jeśli w Editorze wyłączone jest "Reload
            // Domain" przy wejściu w Play Mode, pola statyczne PRZETRWAJĄ między sesjami Play, ale
            // poprzednia sesja mogła je już zdisponować w OnDestroy. s_cachedLen sam w sobie nadal by
            // się zgadzał, wydając zdisponowany uchwyt.
            if (s_cachedLen == len && s_origMask.IsCreated) return;

            DisposeStaticBuffers();

            s_cachedLen = len;
            s_origMask    = new NativeArray<byte>(len, Allocator.Persistent);
            s_erodedMask  = new NativeArray<byte>(len, Allocator.Persistent);
            s_temp1       = new NativeArray<byte>(len, Allocator.Persistent);
            s_temp2       = new NativeArray<byte>(len, Allocator.Persistent);
            s_labels      = new NativeArray<int>(len, Allocator.Persistent);
            s_residueMask = new NativeArray<byte>(len, Allocator.Persistent);
            s_dist        = new NativeArray<ushort>(len, Allocator.Persistent);
            // Rozmiar tablicy UF na stałe na maksymalną możliwą liczbę wysp (len + 1) —
            // zlikwiduje to problem wywołujący Array.Resize i całkowicie zniweluje GC Spikes.
            s_uf          = new NativeArray<int>(len + 1, Allocator.Persistent);
        }

        /// <summary>
        /// Zwalnia bufory statyczne (NativeArray z Allocator.Persistent NIE są sprzątane przez GC —
        /// w przeciwieństwie do dawnych zarządzanych tablic, brak Dispose() = wyciek pamięci natywnej).
        /// Wołane z EnsureArrays przy zmianie rozmiaru wolumenu, z LoadDicomData.OnDestroy() przy
        /// zniszczeniu obiektu sceny, ORAZ (dodatkowa siatka bezpieczeństwa w Editorze) z
        /// AssemblyReloadEvents.beforeAssemblyReload — patrz Assets/Editor/VolumeMorphologyEditorCleanup.cs.
        /// </summary>
        public static void DisposeStaticBuffers()
        {
            if (s_origMask.IsCreated) s_origMask.Dispose();
            if (s_erodedMask.IsCreated) s_erodedMask.Dispose();
            if (s_temp1.IsCreated) s_temp1.Dispose();
            if (s_temp2.IsCreated) s_temp2.Dispose();
            if (s_labels.IsCreated) s_labels.Dispose();
            if (s_residueMask.IsCreated) s_residueMask.Dispose();
            if (s_dist.IsCreated) s_dist.Dispose();
            if (s_uf.IsCreated) s_uf.Dispose();
            s_cachedLen = -1;
        }

        /// <summary>
        /// Gdy true, bufory robocze są zwalniane po KAŻDEJ operacji zamiast wisieć w pamięci do końca
        /// sesji. To 15 bajtów na woksel (5 masek bajtowych + s_labels int + s_dist ushort + s_uf int) —
        /// przy skanie 512x512x400 ok. 1,6 GB trzymane bezczynnie między operacjami, co na urządzeniu
        /// klasy HoloLens (~4 GB RAM na wszystko) jest nie do utrzymania. Kosztem jest ponowna alokacja
        /// przy następnej operacji; alokacja pamięci natywnej jest tania w porównaniu z samą morfologią
        /// (sekundy), więc na sprzęcie XR to opłacalna zamiana. Wyłącz na desktopie, jeśli wolisz
        /// oszczędzić alokacje kosztem stale zajętej pamięci.
        /// </summary>
        public static bool ReleaseScratchAfterUse = true;

        private static void ReleaseScratchIfRequested()
        {
            if (ReleaseScratchAfterUse) DisposeStaticBuffers();
        }

        /// <summary>
        /// Zerowanie bufora Burstem zamiast pętli po elemencie. Wołane po całym wolumenie na starcie
        /// każdej regeneracji maski, więc różnica jest odczuwalna, mimo że to "tylko" zapis zer.
        /// </summary>
        /// Świadomie ASYNCHRONICZNE (await), nie .Complete(): zerowanie stumilionowego bufora to nawet
        /// przy Burście dziesiątki milisekund ruchu pamięci, a zablokowanie na nie wątku głównego
        /// dałoby dokładnie to zacięcie klatki, które staramy się usunąć.
        private static async UniTask ClearBytes(NativeArray<byte> array, int len)
        {
            await new ClearByteJob { target = array }.Schedule(len, 4096).ToUniTask(PlayerLoopTiming.Update);
        }

        private static async UniTask ClearInts(NativeArray<int> array, int len)
        {
            await new ClearIntJob { target = array }.Schedule(len, 4096).ToUniTask(PlayerLoopTiming.Update);
        }

        /// <param name="pieceOwnerMask">
        /// Opcjonalny, trwały mask własności (patrz LoadDicomData.pieceOwnerMask/VolumeObjectManager) —
        /// gdy podany, próg obecności materiału dodatkowo wymaga pieceOwnerMask[i] == requiredOwnerId.
        /// Zastępuje dawny osobny parametr userCuts — "wycięty" to teraz po prostu "należący do innego
        /// właściciela (zwykle Kosza)", więc filtr własności w zupełności to pokrywa: main-body segmentacja
        /// (requiredOwnerId=0) poprawnie NIE liczy schowanych do Kosza wokseli jako część głównej struktury.
        /// </param>
        public static async UniTask<(Texture3D mask, string stats, int[] labelSizesById)> GenerateMaskAsync(NativeArray<short> volumeHu, NativeArray<byte> outLabels, int width, int height, int depth, float thresholdHU, int erosionRadius, int expandRadius = 0, float pixelSpacing = 1f, float sliceThickness = 1f,
            NativeArray<byte> pieceOwnerMask = default, byte requiredOwnerId = 0)
        {
            // Blokada współdzielonych buforów statycznych — patrz komentarz przy s_gate.
            await s_gate.WaitAsync();
            try
            {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            int len = width * height * depth;
            EnsureArrays(len);

            // Czyścimy współdzielone tablice
            await ClearBytes(s_origMask, len);
            await ClearInts(s_labels, len);

            bool hasOwnerFilter = pieceOwnerMask.IsCreated;

            Debug.Log($"[Morphology] 1/5: Thresholding at {thresholdHU} HU...");
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            // Burst + równolegle po wokselu zamiast jednowątkowej pętli na puli wątków. Atrapa o
            // długości 1 dla nieużywanego filtru: Burst wymaga, żeby każde pole NativeArray joba było
            // przypisane, nawet jeśli gałąź go nie czyta.
            var ownerFilterArray = hasOwnerFilter ? pieceOwnerMask : new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                var thresholdJob = new ThresholdMaskJob
                {
                    volumeHu        = volumeHu,
                    pieceOwnerMask  = ownerFilterArray,
                    outMask         = s_origMask,
                    thresholdHU     = thresholdHU,
                    requiredOwnerId = requiredOwnerId,
                    hasOwnerFilter  = (byte)(hasOwnerFilter ? 1 : 0)
                };
                await thresholdJob.Schedule(len, 4096).ToUniTask(PlayerLoopTiming.Update);
            }
            finally
            {
                if (!hasOwnerFilter) ownerFilterArray.Dispose();
            }

            NativeArray<byte> erodedMask = s_origMask;
            if (erosionRadius > 0)
            {
                Debug.Log($"[Morphology] 2/5: Erosion (Radius: {erosionRadius})...");
                swPhase.Restart();
                int rz = 0;
                if (sliceThickness > 0.001f && pixelSpacing > 0.001f)
                    rz = Mathf.RoundToInt(erosionRadius * (pixelSpacing / sliceThickness));
                rz = Mathf.Clamp(rz, 1, erosionRadius); // Dolna granica 1: przy grubych warstwach promień w Z zaokrągliłby się do zera, czyniąc przebieg Z no-opem.
                await ErodeSeparableAsync(s_origMask, s_erodedMask, s_temp1, s_temp2, width, height, depth, erosionRadius, rz);
                erodedMask = s_erodedMask;
                Debug.Log($"[Morphology] Erosion: {swPhase.ElapsedMilliseconds} ms");
            }

            Debug.Log("[Morphology] 3/5: Connected Component Labeling (Thick structures)...");
            swPhase.Restart();
            var thickSizes = await LabelComponentsAsync(erodedMask, s_labels, width, height, depth, 1);
            int nextLabel = 1 + thickSizes.Count;
            Debug.Log($"[Morphology] CCL: {swPhase.ElapsedMilliseconds} ms");

            if (erosionRadius > 0)
            {
                // BEZ limitu promienia (jak w FindComponentContainingSeedAsync) — capowanie na
                // erosionRadius amputowało fizycznie ciągłe, ale lokalnie cieńsze niż 2*erosionRadius
                // fragmenty (np. łuk jarzmowy, przegroda nosowa): erozja usuwała je z rdzenia na całej
                // długości, a odtwarzanie w promieniu erosionRadius sięgało tylko do nasady — reszta
                // trafiała do osobnego przebiegu CCL niżej jako "residue" i dostawała INNĄ etykietę niż
                // reszta tej samej kości ("poszarpanie"). Odtwarzanie bez limitu przypisuje każdy obecny
                // woksel etykiecie geodezyjnie NAJBLIŻSZEGO ocalałego rdzenia — nie psuje separacji
                // faktycznie różnych obiektów (jeśli erozja przecięła cienki mostek, obie strony i tak
                // dostały różne etykiety na etapie CCL PRZED tym odtwarzaniem, więc odtwarzanie bez
                // limitu tylko dzieli sam mostek między nie, nie łączy ich z powrotem).
                int unlimitedRadius = width + height + depth; // maxDist = *10 w DilateLabelsAsync, z zapasem ponad przekątną wolumenu
                Debug.Log("[Morphology] 4/5: Dilation (Restoring boundaries, unlimited geodesic distance)...");
                swPhase.Restart();
                await DilateLabelsAsync(s_labels, s_origMask, s_dist, width, height, depth, unlimitedRadius, pixelSpacing, sliceThickness);
                Debug.Log($"[Morphology] Dilation: {swPhase.ElapsedMilliseconds} ms");

                Debug.Log("[Morphology] 4.2/5: Identifying and labeling Residue (fully-eroded, genuinely separate thin objects)...");
                await ClearBytes(s_residueMask, len);
                await UniTask.RunOnThreadPool(() =>
                {
                    for (int i = 0; i < len; i++)
                    {
                        if (s_origMask[i] == 255 && s_labels[i] == 0) s_residueMask[i] = 255;
                    }
                });

                var residueSizes = await LabelComponentsAsync(s_residueMask, s_labels, width, height, depth, nextLabel);

                foreach (var kv in residueSizes)
                {
                    thickSizes[kv.Key] = kv.Value;
                }
            }

            Debug.Log("[Morphology] 4.3/5: Sorting and remapping all labels...");
            var (stats, labelSizesById) = await RemapLabelsAsync(s_labels, thickSizes, thresholdHU);
            Debug.Log(stats);

            if (expandRadius > 0)
            {
                Debug.Log($"[Morphology] 4.5/5: Expanding labels into background (Radius: {expandRadius})...");
                await ExpandLabelsAsync(s_labels, s_dist, width, height, depth, expandRadius, pixelSpacing, sliceThickness);
            }

            Debug.Log("[Morphology] 5/5: Building Texture3D...");
            Texture3D tex = new Texture3D(width, height, depth, TextureFormat.R8, false)
            {
                // Nie zapisujemy do sceny — to maska policzona z danych pacjenta; patrz komentarz
                // przy _volumeTexture w LoadDicomData.
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };

            await UniTask.RunOnThreadPool(() =>
            {
                for (int i = 0; i < len; i++)
                {
                    outLabels[i] = (byte)Mathf.Clamp(s_labels[i], 0, 255);
                }
            });

            tex.SetPixelData(outLabels, 0);
            tex.Apply(false, true);

            Debug.Log($"[Morphology] Mask Generation Complete! Total: {swTotal.ElapsedMilliseconds} ms");
            return (tex, stats, labelSizesById);
            }
            finally
            {
                // Bufory robocze zwalniamy PRZED oddaniem semafora — inaczej kolejna operacja mogłaby
                // wejść i zacząć alokować w chwili, gdy tę pamięć dopiero zwalniamy, czyli dokładnie
                // w szczycie zużycia, którego chcemy uniknąć.
                ReleaseScratchIfRequested();
                s_gate.Release();
            }
        }

        /// <summary>
        /// Znajduje CAŁY topologicznie połączony komponent zawierający podany seedIndex.
        /// thresholdHU to WYŁĄCZNIE próg OBECNOŚCI materiału (co w ogóle jest "czymś" a nie powietrzem —
        /// wywołujący powinien tu podawać stałą blisko powietrza, NIE dobieraną per przypadek wartość).
        /// Cała topologiczna separacja "co dotyka czego przez samą skórę" jest robiona GEOMETRYCZNIE,
        /// odległością, nie progiem gęstości: parametr `separationRadius` (>0) ERODUJE maskę obecności o
        /// ten promień PRZED CCL — cienkie mostki (skóra między głową a maseczką/poduszką) znikają
        /// całkowicie i obiekty się rozdzielają, podczas gdy fizycznie grube obiekty (czaszka, sama
        /// poduszka, sam rdzeń maseczki) przetrwają erozję i zostaną jednym komponentem.
        ///
        /// WAŻNE: erozja służy WYŁĄCZNIE do jednej decyzji binarnej — "czy seed dotyka głównej struktury,
        /// czy nie" (isMainBody). Ta sama erozja, użyta do WYKROJENIA kształtu znalezionego obiektu,
        /// fałszywie rozdzielałaby jeden fizyczny, ale lokalnie CIENKI przedmiot (np. wygięty łuk
        /// poduszki, wąski fragment maseczki przy nosie) na kilka osobnych komponentów — usuwałoby się
        /// wtedy tylko kawałek trafiony klikiem, reszta zostawała jako "poświata". Dlatego po ustaleniu
        /// isMainBody metoda ODTWARZA pełne terytorium głównej struktury (DilateLabelsAsync, geodezyjnie,
        /// BEZ limitu odległości — bezpieczne, bo i tak nigdy nie przekroczy prawdziwej przerwy w masce
        /// obecności), a WSZYSTKO pozostałe (`s_origMask` minus terytorium głównej struktury) trafia do
        /// DRUGIEGO przebiegu CCL — tym razem BEZ ŻADNEJ erozji, na prawdziwej, nieuszkodzonej geometrii —
        /// więc jeden fizycznie ciągły akcesorium (choćby najcieńszy w niektórych miejscach) zawsze wraca
        /// jako JEDEN kompletny komponent, a różne, faktycznie nie stykające się ze sobą akcesoria (np.
        /// poduszka i maseczka) zostają poprawnie rozróżnione.
        /// isMainBody = true oznacza, że seed leży w głównej strukturze — wywołujący powinien wtedy
        /// odmówić usunięcia/izolacji (do przycinania głównej struktury służy Cut).
        /// </summary>
        /// <param name="pieceOwnerMask">
        /// Opcjonalny, trwały mask własności (patrz LoadDicomData.pieceOwnerMask/VolumeObjectManager) —
        /// gdy podany (IsCreated==true), maska obecności materiału dodatkowo wymaga
        /// pieceOwnerMask[i] == requiredOwnerId. Dzięki temu cięcie/pick na już wydzielonym obiekcie
        /// NIGDY nie "sięga" do wokseli innego obiektu (głównego wolumenu albo innego kawałka), nawet
        /// jeśli fizycznie stykają się we współrzędnych oryginalnego skanu — bez tego dwa wydzielone
        /// (i np. przesunięte tak, że znów się dotykają) obiekty mogłyby się przypadkiem "posklejać"
        /// przy dalszym cięciu. Domyślnie (mask nie podany) zachowanie identyczne jak dawniej — cała
        /// objętość liczy się jako jeden właściciel, tak jak przy głównym wolumenie.
        /// </param>
        public static async UniTask<(NativeArray<byte> mask, bool isMainBody)> FindComponentContainingSeedAsync(
            NativeArray<short> volumeHu, int width, int height, int depth, float thresholdHU, int seedIndex,
            int expandRadius = 0, float pixelSpacing = 1f, float sliceThickness = 1f, int separationRadius = 0,
            NativeArray<byte> pieceOwnerMask = default, byte requiredOwnerId = 0,
            System.Threading.CancellationToken ct = default)
        {
            // Anulowanie działa TAKŻE w kolejce na semaforze i to jest tu najważniejsze: operacja trwa
            // sekundy, więc kolejne kliknięcia Pickerem ustawiały się w kolejce i każde odrabiało pełną
            // robotę po kolei, choć liczył się już tylko wynik ostatniego. Rzut z WaitAsync następuje
            // PRZED wejściem w try, więc semafor nie zostaje wtedy zajęty ani (błędnie) zwolniony.
            await s_gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var swTotal = System.Diagnostics.Stopwatch.StartNew();
                int len = width * height * depth;
                EnsureArrays(len);

                await ClearBytes(s_origMask, len);
                await ClearInts(s_labels, len);

                bool hasOwnerFilter = pieceOwnerMask.IsCreated;

                var swPhase = System.Diagnostics.Stopwatch.StartNew();
                await UniTask.RunOnThreadPool(() =>
                {
                    for (int i = 0; i < len; i++)
                    {
                        bool ownerOk = !hasOwnerFilter || pieceOwnerMask[i] == requiredOwnerId;
                        s_origMask[i] = (ownerOk && volumeHu[i] >= thresholdHU) ? (byte)255 : (byte)0;
                    }
                });
                Debug.Log($"[Morphology] Threshold: {swPhase.ElapsedMilliseconds} ms");

                // s_origMask od teraz to OSTATECZNA maska "obecności materiału" — musi
                // przetrwać nietknięta aż do DilateLabelsAsync niżej, więc erozja pisze do s_erodedMask,
                // NIE nadpisuje s_origMask.
                NativeArray<byte> connectivityMask = s_origMask;
                if (separationRadius > 0)
                {
                    int srz = 0;
                    if (sliceThickness > 0.001f && pixelSpacing > 0.001f)
                        srz = Mathf.RoundToInt(separationRadius * (pixelSpacing / sliceThickness));
                    srz = Mathf.Clamp(srz, 1, separationRadius);

                    swPhase.Restart();
                    await ErodeSeparableAsync(s_origMask, s_erodedMask, s_temp1, s_temp2, width, height, depth, separationRadius, srz);
                    connectivityMask = s_erodedMask;
                    Debug.Log($"[Morphology] Separation erosion: {swPhase.ElapsedMilliseconds} ms");
                }

                // Kontrole między fazami — każda z nich to osobne sekundy pracy, więc sprawdzenie tutaj
                // przerywa nieaktualne kliknięcie najpóźniej po zakończeniu bieżącej fazy.
                ct.ThrowIfCancellationRequested();

                swPhase.Restart();
                var sizes = await LabelComponentsAsync(connectivityMask, s_labels, width, height, depth, 1);
                Debug.Log($"[Morphology] CCL: {swPhase.ElapsedMilliseconds} ms");
                ct.ThrowIfCancellationRequested();

                // Największy komponent (na ZEROWANEJ erozją masce) = "główna struktura" (czaszka +
                // przylegająca tkanka). Remove Island celowo NIE usuwa jej po kliknięciu — to narzędzie
                // do zdejmowania odstających fragmentów, nie do przycinania głównego obiektu (od tego jest Cut).
                int largestLabel = 0, largestSize = -1;
                foreach (var kv in sizes)
                    if (kv.Value > largestSize) { largestLabel = kv.Key; largestSize = kv.Value; }

                if (separationRadius > 0)
                {
                    // Odtwarzamy etykiety z powrotem do PEŁNEGO zasięgu sprzed erozji — geodezyjnie, w
                    // obrębie s_origMask, CELOWO BEZ limitu odległości (patrz komentarz przy metodzie):
                    // "unlimitedRadius" tylko po to, żeby DilateLabelsAsync w ogóle nie ucinał zasięgu —
                    // realną granicą i tak jest s_origMask (prawdziwa przerwa = powietrze).
                    int unlimitedRadius = (width + height + depth); // maxDist = *10 w DilateLabelsAsync, z zapasem ponad przekątną wolumenu
                    swPhase.Restart();
                    await DilateLabelsAsync(s_labels, s_origMask, s_dist, width, height, depth, unlimitedRadius, pixelSpacing, sliceThickness);
                    Debug.Log($"[Morphology] Dilation (restore): {swPhase.ElapsedMilliseconds} ms");
                }

                // s_labels teraz przypisuje KAŻDEMU obecnemu wokselowi etykietę geodezyjnie NAJBLIŻSZEGO
                // ocalałego po erozji rdzenia — to poprawnie wykrawa PEŁNE terytorium głównej struktury
                // (włącznie z jej słusznym udziałem w dotykającej skórze), ale separationRadius mógł
                // rozerwać JEDEN fizyczny akcesorium na kilka różnych etykiet, jeśli miał lokalnie cienką
                // część — dlatego kształtu obiektu NIE bierzemy stąd wprost, patrz niżej.
                bool seedInPresenceMask = seedIndex >= 0 && seedIndex < len && s_origMask[seedIndex] == 255;
                bool isMainBody = seedInPresenceMask && s_labels[seedIndex] == largestLabel;

                var result = new NativeArray<byte>(len, Allocator.Persistent);
                try
                {

                if (seedInPresenceMask && !isMainBody)
                {
                    ct.ThrowIfCancellationRequested();
                    // DRUGI przebieg CCL, TYM RAZEM BEZ ŻADNEJ EROZJI: bierzemy "wszystko oprócz
                    // terytorium głównej struktury" (s_origMask minus to, co właśnie odtworzyliśmy jako
                    // largestLabel) i liczymy spójne składowe na PRAWDZIWEJ, nieuszkodzonej geometrii.
                    // Fizycznie ciągły obiekt (choćby miejscami bardzo cienki — łuk poduszki, wąski
                    // fragment maseczki przy nosie) zawsze wraca jako JEDEN kompletny komponent, bo tu
                    // nic go już nie eroduje; różne, faktycznie nie stykające się akcesoria nadal
                    // wychodzą jako osobne komponenty, bo fizycznie nie są połączone.
                    await UniTask.RunOnThreadPool(() =>
                    {
                        for (int i = 0; i < len; i++)
                            s_erodedMask[i] = (s_origMask[i] == 255 && s_labels[i] != largestLabel) ? (byte)255 : (byte)0; // reużywamy jako bufor remainderMask
                    });

                    await ClearInts(s_labels, len); // reużywamy pod wynik drugiego CCL
                    var remainderSizes = await LabelComponentsAsync(s_erodedMask, s_labels, width, height, depth, 1);
                    ct.ThrowIfCancellationRequested();

                    int seedLabel = s_labels[seedIndex];
                    int seedSize = remainderSizes.TryGetValue(seedLabel, out int sz) ? sz : 0;
                    Debug.Log($"[Morphology] Połączony obiekt od klikniętego punktu (próg {thresholdHU} HU): {seedSize} wokseli (główna struktura: {largestSize} wokseli).");

                    await UniTask.RunOnThreadPool(() =>
                    {
                        for (int i = 0; i < len; i++)
                            result[i] = (s_labels[i] == seedLabel) ? (byte)255 : (byte)0;
                    });

                    // ROZSZERZENIE maski TEGO obiektu (jak Morph Expand Radius w głównym potoku) —
                    // zamiata cienki, niskogęstościowy "fringe" bezpośrednio wokół usuwanego obiektu,
                    // który nigdy nie przekracza nawet niskiego thresholdHU, więc CCL go nigdy nie złapie
                    // jako część tej samej wyspy, a mimo to fizycznie do niej przylega i renderuje się
                    // jako resztkowa poświata po wycięciu samego rdzenia.
                    if (expandRadius > 0)
                    {
                        int erz = 0;
                        if (sliceThickness > 0.001f && pixelSpacing > 0.001f)
                            erz = Mathf.RoundToInt(expandRadius * (pixelSpacing / sliceThickness));
                        // Dolna granica 1 — patrz komentarz przy analogicznym przeliczeniu w GenerateMaskAsync.
                        erz = Mathf.Clamp(erz, 1, expandRadius);

                        result.CopyTo(s_erodedMask);
                        await DilateSeparableAsync(s_erodedMask, s_origMask, s_temp1, s_temp2, width, height, depth, expandRadius, erz);

                        await UniTask.RunOnThreadPool(() =>
                        {
                            for (int i = 0; i < len; i++)
                                result[i] = s_origMask[i];
                        });
                    }
                }
                else if (isMainBody)
                {
                    Debug.LogWarning($"[Morphology] Kliknięty punkt należy do GŁÓWNEJ struktury ({largestSize} wokseli) — Remove Island jej nie usuwa. Użyj narzędzia Cut, jeśli chcesz przyciąć fragment głównego obiektu.");
                }
                else
                {
                    Debug.LogWarning($"[Morphology] Klikany punkt nie ma żadnej połączonej struktury przy progu {thresholdHU} HU.");
                }

                Debug.Log($"[Morphology] FindComponentContainingSeedAsync total: {swTotal.ElapsedMilliseconds} ms");
                return (result, isMainBody);

                }
                catch (System.OperationCanceledException)
                {
                    // Bufor wyniku jest alokowany PRZED tą sekcją, a jest to pamięć natywna, której GC
                    // nie posprząta — anulowanie w połowie drugiego CCL wyciekłoby cały wolumen bajtów.
                    result.Dispose();
                    throw;
                }
            }
            finally
            {
                // Bufory robocze zwalniamy PRZED oddaniem semafora — inaczej kolejna operacja mogłaby
                // wejść i zacząć alokować w chwili, gdy tę pamięć dopiero zwalniamy, czyli dokładnie
                // w szczycie zużycia, którego chcemy uniknąć.
                ReleaseScratchIfRequested();
                s_gate.Release();
            }
        }

        private static async UniTask ErodeSeparableAsync(NativeArray<byte> input, NativeArray<byte> output, NativeArray<byte> temp1, NativeArray<byte> temp2, int w, int h, int d, int radius, int rz)
        {
            await RunSeparableFilterAsync(input, output, temp1, temp2, w, h, d, radius, rz, isDilate: false);
        }

        /// <summary>
        /// Odbicie lustrzane ErodeSeparableAsync — filtr MAX zamiast MIN (separowalny, 3 przebiegi).
        /// Woksel poza brzegiem wolumenu NIE wymusza wyniku (w przeciwieństwie do erozji, gdzie
        /// brzeg = 0) — po prostu jest pomijany, więc dylatacja nie "obcina" brzegów wolumenu.
        /// </summary>
        private static async UniTask DilateSeparableAsync(NativeArray<byte> input, NativeArray<byte> output, NativeArray<byte> temp1, NativeArray<byte> temp2, int w, int h, int d, int radius, int rz)
        {
            await RunSeparableFilterAsync(input, output, temp1, temp2, w, h, d, radius, rz, isDilate: true);
        }

        /// <summary>
        /// 3 przebiegi (X, Y, Z) SeparableMinMaxJob — każdy zrównoleglony IJobParallelFor po wierszu,
        /// zamiast dawnego jednowątkowego Task.Run z zagnieżdżoną potrójną pętlą. Pierwsze dwa przebiegi
        /// (X, Y) używają izotropowego `radius`, trzeci (Z) używa `rz` (osobna skala dla anizotropowych
        /// skanów) — dokładnie jak w oryginale.
        /// </summary>
        private static async UniTask RunSeparableFilterAsync(NativeArray<byte> input, NativeArray<byte> output, NativeArray<byte> temp1, NativeArray<byte> temp2, int w, int h, int d, int radius, int rz, bool isDilate)
        {
            var passX = new SeparableMinMaxJob { input = input, output = temp1, w = w, h = h, d = d, radius = radius, axis = 0, isDilate = isDilate };
            await passX.Schedule(d * h, 64).ToUniTask(PlayerLoopTiming.Update);

            var passY = new SeparableMinMaxJob { input = temp1, output = temp2, w = w, h = h, d = d, radius = radius, axis = 1, isDilate = isDilate };
            await passY.Schedule(d * w, 64).ToUniTask(PlayerLoopTiming.Update);

            var passZ = new SeparableMinMaxJob { input = temp2, output = output, w = w, h = h, d = d, radius = rz, axis = 2, isDilate = isDilate };
            await passZ.Schedule(h * w, 64).ToUniTask(PlayerLoopTiming.Update);
        }

        /// <summary>
        /// CCL w dwóch przebiegach: PIERWSZY (UnionFindLabelJob, Burst, jednowątkowy — union-find ma
        /// sekwencyjne zależności, nie jest to trywialnie zrównoleglane) liczy raster-scan + union-find.
        /// DRUGI (zwykły C# poniżej, na wątku tła) rozwiązuje korzenie union-find do skompresowanych
        /// finalnych etykiet i buduje Dictionary rozmiarów wysp — Dictionary nie jest Burst-legalny,
        /// więc ten etap zostaje poza jobem (to i tak jeden tani liniowy przebieg, nie wąskie gardło).
        /// </summary>
        private static async UniTask<Dictionary<int, int>> LabelComponentsAsync(NativeArray<byte> mask, NativeArray<int> labels, int w, int h, int d, int startingLabel)
        {
            var job = new UnionFindLabelJob { mask = mask, labels = labels, uf = s_uf, w = w, h = h, d = d, startingLabel = startingLabel };
            await job.Schedule().ToUniTask(PlayerLoopTiming.Update);

            var ufLocal = s_uf; // lokalna kopia uchwytu NativeArray dla domknięcia poniżej (bezpieczne, to tylko wskaźnik+safety handle)
            return await UniTask.RunOnThreadPool(() =>
            {
                int len = w * h * d;

                int UFFind(int x)
                {
                    while (ufLocal[x] != x) { ufLocal[x] = ufLocal[ufLocal[x]]; x = ufLocal[x]; }
                    return x;
                }

                // UWAGA: Nigdy nie pre-alokuj słowników na 'nextProvisional'!
                // W skanach medycznych nextProvisional (lokalne minima przed złączeniem)
                // może osiągać 10-50 milionów. Pre-alokacja pochłonie natychmiast kilka gigabajtów RAM!
                var rootToFinal = new Dictionary<int, int>();
                var labelSizes = new Dictionary<int, int>();
                int finalNext = startingLabel;

                for (int i = 0; i < len; i++)
                {
                    if (mask[i] == 0 || labels[i] < startingLabel) continue;
                    int root = UFFind(labels[i]);
                    if (!rootToFinal.TryGetValue(root, out int fl))
                    {
                        fl = finalNext++;
                        rootToFinal[root] = fl;
                        labelSizes[fl] = 0;
                    }
                    labels[i] = fl;
                    labelSizes[fl]++;
                }

                return labelSizes;
            });
        }

        private static async UniTask<(string stats, int[] labelSizesById)> RemapLabelsAsync(NativeArray<int> labels, Dictionary<int, int> labelSizes, float thresholdHU)
        {
            return await UniTask.RunOnThreadPool(() =>
            {
                var finalSizes = new int[255]; // indeks = finalna etykieta (1..254), 0 = nieużywane
                if (labelSizes.Count == 0) return ("Brak wysp.", finalSizes);

                var sorted = labelSizes.OrderByDescending(kv => kv.Value).ToList();
                int maxKey = labelSizes.Keys.Max();
                int[] remap = new int[maxKey + 1];

                // Etykieta to pojedynczy bajt (1..254 realne wyspy, 0=tło, 255=zarezerwowane pod
                // AccessoryPreviewLabel) — gdy wysp jest więcej niż mieści się unikalnych etykiet, NIE
                // wolno przydzielać nadmiarowym etykiety 0 ("tło"): stają się wtedy niewidoczne dla
                // Pickera/RemoveIsland i wyglądają jak dziura w segmentacji. Zamiast tego łączymy
                // WSZYSTKIE nadmiarowe, najmniejsze wyspy we wspólną etykietę resztkową 254 — nadal
                // wyizolowalną (choć nie pojedynczo), zamiast znikać bez śladu.
                const int OverflowLabel = 254;
                int newLabel = 1;
                int overflowIslandCount = 0;
                foreach (var kv in sorted)
                {
                    if (newLabel < OverflowLabel)
                    {
                        remap[kv.Key] = newLabel;
                        finalSizes[newLabel] = kv.Value;
                        newLabel++;
                    }
                    else
                    {
                        remap[kv.Key] = OverflowLabel;
                        finalSizes[OverflowLabel] += kv.Value;
                        overflowIslandCount++;
                    }
                }

                int len = labels.Length;
                for (int i = 0; i < len; i++)
                {
                    if (labels[i] > 0 && labels[i] <= maxKey)
                    {
                        labels[i] = remap[labels[i]];
                    }
                }

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine($"Łącznie znaleziono {sorted.Count} wysp. Top 10 największych:");
                for (int i = 0; i < Mathf.Min(10, sorted.Count); i++)
                {
                    sb.AppendLine($"   -> ID {i + 1}: rozmiar {sorted[i].Value} wokseli");
                }

                // Setki/tysiące wysp niemal zawsze oznaczają, że Threshold HU jest ustawiony za nisko
                // (łapie tkankę miękką zamiast samej kości) — segmentacja staje się szumem, a Pick/
                // RemoveIsland trafiają w losowe drobne fragmenty zamiast w zamierzoną strukturę.
                // Kość zbita to zwykle 300-800+ HU — tkanka miękka/woda to 0-80 HU.
                if (overflowIslandCount > 0)
                {
                    sb.AppendLine($"UWAGA: {overflowIslandCount} najmniejszych wysp połączono we wspólną etykietę resztkową {OverflowLabel} " +
                        $"({finalSizes[OverflowLabel]} wokseli łącznie) — zabrakło unikalnych etykiet (limit {OverflowLabel - 1} + jedna resztkowa).");
                }

                if (sorted.Count > 500)
                {
                    Debug.LogWarning($"[Morphology] UWAGA: znaleziono {sorted.Count} wysp przy Threshold HU = {thresholdHU} — to bardzo dużo. " +
                        "Prawdopodobnie próg jest wciąż za nisko i segmentacja łapie tkankę miękką " +
                        "zamiast samej kości (kość zbita to zwykle 300-800+ HU). Przy tak dużej fragmentacji Pick/RemoveIsland " +
                        "będą trafiać w losowe drobne wyspy zamiast w zamierzoną strukturę. Spróbuj podnieść próg jeszcze wyżej (np. 300-350) " +
                        "i sprawdź czy liczba wysp spada do rzędu dziesiątek — kilka tysięcy nawet przy 'dobrym' progu bywa nienormalne, " +
                        "część fragmentacji (zęby, drobne kostki) jest naturalna, ale nie na taką skalę.");
                }

                return (sb.ToString(), finalSizes);
            });
        }

        /// <summary>
        /// Odbudowuje etykiety w wokselach usuniętych przez erozję, licząc dla każdego z nich
        /// GEODEZYJNIE najbliższe źródło (26-połączenie, koszt kroku ~proporcjonalny do fizycznej
        /// odległości, z osobną skalą dla osi Z przy anizotropowych skanach). BucketDilateJob (Burst,
        /// jednowątkowy — kolejka kubełkowa musi być przetwarzana w ściśle rosnącej kolejności odległości)
        /// zastępuje dawny zachłanny stos LIFO, który finalizował etykietę PIERWSZEJ ścieżki DFS
        /// niezależnie od jej realnej długości.
        /// </summary>
        private static async UniTask DilateLabelsAsync(NativeArray<int> labels, NativeArray<byte> originalThresholdMask, NativeArray<ushort> dist, int w, int h, int d, int radius, float px, float pz)
        {
            await RunBucketDilateAsync(labels, originalThresholdMask, dist, w, h, d, radius, px, pz, useMaskFilter: true, neighborCount: 26);
        }

        private static async UniTask ExpandLabelsAsync(NativeArray<int> labels, NativeArray<ushort> dist, int w, int h, int d, int radius, float px, float pz)
        {
            // Brak filtra po originalThresholdMask jest CELOWY — to narzędzie ma prawo "wjechać" w
            // dowolne tło (Morph Expand Radius rozszerza etykiety w głąb tła bez progu gęstości).
            // Dummy o rozmiarze 1: pole jest wymagane przez BucketDilateJob, ale nieużywane gdy useMaskFilter=false.
            var dummyMask = new NativeArray<byte>(1, Allocator.Persistent);
            try
            {
                await RunBucketDilateAsync(labels, dummyMask, dist, w, h, d, radius, px, pz, useMaskFilter: false, neighborCount: 6);
            }
            finally
            {
                dummyMask.Dispose();
            }
        }

        private static async UniTask RunBucketDilateAsync(NativeArray<int> labels, NativeArray<byte> originalThresholdMask, NativeArray<ushort> dist, int w, int h, int d, int radius, float px, float pz, bool useMaskFilter, int neighborCount)
        {
            float zRatio = (px > 0.001f && pz > 0.001f) ? (pz / px) : 1f;

            // Allocator.Persistent, nie TempJob: ten job jest await'owany przez UniTask przez wiele
            // klatek (Dilation potrafi trwać >1s), a TempJob ma twardy 4-klatkowy limit życia
            // egzekwowany przez system bezpieczeństwa Unity — po jego przekroczeniu silnik sam
            // wymusza usunięcie alokacji i zgłasza to jako "leak", mimo że Dispose() niżej i tak
            // zawsze się wykonuje (try/finally). Persistent nie ma takiego limitu.
            NativeArray<int> dx, dy, dz, costs;
            if (neighborCount == 26)
            {
                dx = new NativeArray<int>(26, Allocator.Persistent);
                dy = new NativeArray<int>(26, Allocator.Persistent);
                dz = new NativeArray<int>(26, Allocator.Persistent);
                costs = new NativeArray<int>(26, Allocator.Persistent);
                int idx26 = 0;
                for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                for (int k = -1; k <= 1; k++)
                {
                    if (i == 0 && j == 0 && k == 0) continue;
                    dx[idx26] = i; dy[idx26] = j; dz[idx26] = k;
                    // Min. 1: przy skrajnej anizotropii (bardzo cienkie warstwy) koszt kroku
                    // czysto w XY mógłby się zaokrąglić do 0, co dawałoby "darmowy" ruch.
                    costs[idx26] = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(i * i * 100 + j * j * 100 + k * k * 100 * zRatio * zRatio)));
                    idx26++;
                }
            }
            else
            {
                dx = new NativeArray<int>(new[] { 1, -1, 0, 0, 0, 0 }, Allocator.Persistent);
                dy = new NativeArray<int>(new[] { 0, 0, 1, -1, 0, 0 }, Allocator.Persistent);
                dz = new NativeArray<int>(new[] { 0, 0, 0, 0, 1, -1 }, Allocator.Persistent);
                // Min. 1 — patrz komentarz przy costs w gałęzi 26-połączenia (skrajna anizotropia mogłaby
                // dać "darmowy" krok w Z, co zaburzałoby geodezyjną kolejność w kolejce kubełkowej).
                int zCost = Mathf.Max(1, Mathf.RoundToInt(10 * zRatio));
                costs = new NativeArray<int>(new[] { 10, 10, 10, 10, zCost, zCost }, Allocator.Persistent);
            }

            int maxDist = radius * 10;
            var bucketHead = new NativeArray<int>(maxDist + 1, Allocator.Persistent);
            var bucketTail = new NativeArray<int>(maxDist + 1, Allocator.Persistent);
            var arenaVoxel = new NativeList<int>(Allocator.Persistent);
            var arenaNext = new NativeList<int>(Allocator.Persistent);

            try
            {
                var job = new BucketDilateJob
                {
                    labels = labels,
                    originalThresholdMask = originalThresholdMask,
                    dist = dist,
                    dx = dx, dy = dy, dz = dz, costs = costs,
                    neighborCount = neighborCount,
                    w = w, h = h, d = d,
                    maxDist = maxDist,
                    useMaskFilter = useMaskFilter,
                    bucketHead = bucketHead,
                    bucketTail = bucketTail,
                    arenaVoxel = arenaVoxel,
                    arenaNext = arenaNext
                };
                await job.Schedule().ToUniTask(PlayerLoopTiming.Update);
            }
            finally
            {
                dx.Dispose(); dy.Dispose(); dz.Dispose(); costs.Dispose();
                bucketHead.Dispose(); bucketTail.Dispose();
                arenaVoxel.Dispose(); arenaNext.Dispose();
            }
        }
    }
}
