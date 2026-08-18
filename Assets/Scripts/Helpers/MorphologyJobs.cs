using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Helpers
{
    /// <summary>
    /// Progowanie HU + opcjonalny filtr właściciela — pierwszy przebieg segmentacji, po jednym
    /// niezależnym wokselu na wywołanie, więc trywialnie równoległy. Był zwykłą pętlą `for` na puli
    /// wątków: jeden rdzeń, bez wektoryzacji, przy ~100 mln wokseli to setki milisekund samego
    /// przemiatania pamięci na starcie KAŻDEJ regeneracji maski.
    /// </summary>
    [BurstCompile]
    public struct ThresholdMaskJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<short> volumeHu;
        // Pusty, gdy filtr właściciela nieaktywny — Burst nie znosi pól NativeArray bez przypisania,
        // więc wywołujący podstawia atrapę o długości 1 i zeruje hasOwnerFilter.
        [ReadOnly] public NativeArray<byte> pieceOwnerMask;
        [WriteOnly] public NativeArray<byte> outMask;

        public float thresholdHU;
        public byte requiredOwnerId;
        public byte hasOwnerFilter;

        public void Execute(int i)
        {
            if (hasOwnerFilter != 0 && pieceOwnerMask[i] != requiredOwnerId)
            {
                outMask[i] = 0;
                return;
            }
            outMask[i] = volumeHu[i] >= thresholdHU ? (byte)255 : (byte)0;
        }
    }

    /// <summary>
    /// Zerowanie bufora. Osobne, konkretne typy zamiast generyka, bo Burst kompiluje wyłącznie
    /// domknięte typy jobów. Zastępuje ręczne pętle `for (i) array[i] = default` po całym wolumenie,
    /// wykonywane po dwa razy na każdą regenerację maski.
    /// </summary>
    [BurstCompile]
    public struct ClearByteJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<byte> target;
        public void Execute(int i) => target[i] = 0;
    }

    [BurstCompile]
    public struct ClearIntJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<int> target;
        public void Execute(int i) => target[i] = 0;
    }

    /// <summary>
    /// Separowalny filtr MIN (erozja, isDilate=false) / MAX (dylatacja, isDilate=true) — jeden przebieg
    /// (X, Y albo Z, wybrany przez `axis`) z 3-przebiegowego separowalnego filtra pudełkowego.
    /// Zrównoleglony PO WIERSZU (każdy wiersz wzdłuż skanowanej osi jest niezależny od pozostałych),
    /// dlatego `output` wymaga [NativeDisableParallelForRestriction] — piszemy do wielu indeksów na
    /// wywołanie Execute (rozproszonych po `output` wg `baseIdx + p*stride`), nie tylko pod indeksem
    /// `rowId`, ale zbiory zapisywanych indeksów między wierszami nigdy się nie pokrywają (rozłączne
    /// wiersze), więc jest to bezpieczne mimo wyłączonej domyślnej kontroli IJobParallelFor.
    /// </summary>
    [BurstCompile]
    public struct SeparableMinMaxJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> input;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> output;
        public int w, h, d;
        public int radius;
        public int axis; // 0 = przebieg X, 1 = przebieg Y, 2 = przebieg Z
        public bool isDilate;

        public void Execute(int rowId)
        {
            int wh = w * h;
            int baseIdx, stride, scanLen;

            if (axis == 0)
            {
                int z = rowId / h;
                int y = rowId % h;
                baseIdx = z * wh + y * w;
                stride = 1;
                scanLen = w;
            }
            else if (axis == 1)
            {
                int z = rowId / w;
                int x = rowId % w;
                baseIdx = z * wh + x;
                stride = w;
                scanLen = h;
            }
            else
            {
                int y = rowId / w;
                int x = rowId % w;
                baseIdx = y * w + x;
                stride = wh;
                scanLen = d;
            }

            byte fillVal = isDilate ? (byte)0 : (byte)255;
            for (int p = 0; p < scanLen; p++)
            {
                byte val = fillVal;
                for (int off = -radius; off <= radius; off++)
                {
                    int np = p + off;
                    if (np >= 0 && np < scanLen)
                    {
                        byte s = input[baseIdx + np * stride];
                        if (isDilate) { if (s == 255) { val = 255; break; } }
                        else { if (s == 0) { val = 0; break; } }
                    }
                    else
                    {
                        // Erozja: woksel poza brzegiem wymusza wynik 0 (brzeg = "nic"). Dylatacja: poza
                        // brzegiem jest po prostu pomijany (brak else) — patrz oryginalny komentarz przy
                        // DilateSeparableAsync w VolumeMorphology.cs.
                        if (!isDilate) { val = 0; break; }
                    }
                }
                output[baseIdx + p * stride] = val;
            }
        }
    }

    /// <summary>
    /// Raster-scan + union-find (kompresja ścieżek, iteracyjna) — PIERWSZY z dwóch przebiegów CCL.
    /// Przypisuje każdemu wokselowi maski TYMCZASOWĄ etykietę (root unii, nie zawsze jeszcze finalny
    /// skompresowany korzeń — dokładnie jak w oryginalnym jednowątkowym kodzie, stąd nadal potrzebny
    /// DRUGI przebieg, w zwykłym C# w VolumeMorphology.LabelComponentsAsync, budujący Dictionary
    /// rozmiarów wysp — Dictionary nie jest Burst-legalny, więc zostaje poza tym jobem).
    /// Jednowątkowy (IJob, nie IJobParallelFor) — union-find ma sekwencyjne zależności między
    /// sąsiadującymi wokselami, nie jest to trywialnie zrównoleglane bez zmiany algorytmu.
    /// </summary>
    [BurstCompile]
    public struct UnionFindLabelJob : IJob
    {
        [ReadOnly] public NativeArray<byte> mask;
        public NativeArray<int> labels;
        public NativeArray<int> uf;
        public int w, h, d;
        public int startingLabel;

        public void Execute()
        {
            int wh = w * h;

            // NativeArray jest uchwytem do pamięci natywnej — kopiowanie do lokalnej zmiennej nie
            // kopiuje danych, tylko pozwala lokalnej funkcji UFFind() uniknąć niedozwolonego dostępu
            // do 'this' wewnątrz struct.
            var ufLocal = uf;

            int UFFind(int x)
            {
                while (ufLocal[x] != x) { ufLocal[x] = ufLocal[ufLocal[x]]; x = ufLocal[x]; }
                return x;
            }

            int nextProvisional = startingLabel;

            // Inicjalizujemy tylko wymaganą początkową pulę wysp (indeksy < startingLabel — patrz
            // komentarz w oryginalnym LabelComponentsAsync o "self-mapping" niższych etykiet).
            for (int k = 0; k < nextProvisional; k++) ufLocal[k] = k;

            for (int z = 0; z < d; z++)
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = z * wh + y * w + x;
                if (mask[i] == 0 || labels[i] != 0) continue;

                int minLabel = 0;

                if (x > 0)
                {
                    int ni = i - 1;
                    if (mask[ni] != 0 && labels[ni] >= startingLabel)
                        minLabel = UFFind(labels[ni]);
                }
                if (y > 0)
                {
                    int ni = i - w;
                    if (mask[ni] != 0 && labels[ni] >= startingLabel)
                    {
                        int nl = UFFind(labels[ni]);
                        if (minLabel == 0) minLabel = nl;
                        else { int ml = UFFind(minLabel); if (ml != nl) ufLocal[ml] = nl; minLabel = nl; }
                    }
                }
                if (z > 0)
                {
                    int ni = i - wh;
                    if (mask[ni] != 0 && labels[ni] >= startingLabel)
                    {
                        int nl = UFFind(labels[ni]);
                        if (minLabel == 0) minLabel = nl;
                        else { int ml = UFFind(minLabel); if (ml != nl) ufLocal[ml] = nl; minLabel = nl; }
                    }
                }

                if (minLabel == 0)
                {
                    ufLocal[nextProvisional] = nextProvisional;
                    labels[i] = nextProvisional++;
                }
                else
                {
                    labels[i] = minLabel;
                }
            }
        }
    }

    /// <summary>
    /// Kolejka kubełkowa ("dial" Dijkstra) — geodezyjne odtwarzanie etykiet (DilateLabelsAsync,
    /// useMaskFilter=true, 26-połączenie) albo rozszerzanie w tło bez progu (ExpandLabelsAsync,
    /// useMaskFilter=false, 6-połączenie). Jednowątkowy IJob — buckety muszą być przetwarzane w ściśle
    /// rosnącej kolejności odległości, to nie jest trywialnie zrównoleglane.
    ///
    /// WAŻNE — dlaczego arena (NativeList) zamiast jednego wskaźnika `next` na woksel: TEN SAM woksel
    /// może zostać wypchnięty do kolejki WIELOKROTNIE (relaksacja Dijkstry — pierwsze wepchnięcie do
    /// dalekiego kubełka, później krótsza ścieżka wpycha go do bliższego), a stary wpis zostaje w swoim
    /// kubełku jako "duch" (pomijany przez `dist[idx]!=b`). Jeden wskaźnik `next` PER WOKSEL nie mógłby
    /// tego wyrazić — drugie wepchnięcie nadpisałoby wskaźnik używany przez łańcuch PIERWSZEGO kubełka,
    /// urywając go w połowie. Arena (dwie równoległe, rosnące NativeList: który-woksel + wskaźnik-na-
    /// -następny-slot-W-TEJ-ARENIE) daje każdemu wepchnięciu WŁASNY, nigdy niekolidujący slot — dokładnie
    /// odwzorowuje semantykę oryginalnego `List&lt;int&gt;[] buckets` (gdzie duplikaty są zwykłymi,
    /// osobnymi wpisami listy).
    /// </summary>
    [BurstCompile]
    public struct BucketDilateJob : IJob
    {
        public NativeArray<int> labels;
        [ReadOnly] public NativeArray<byte> originalThresholdMask; // nieużywane gdy useMaskFilter=false (przekazać dowolną istniejącą tablicę)
        public NativeArray<ushort> dist;
        [ReadOnly] public NativeArray<int> dx;
        [ReadOnly] public NativeArray<int> dy;
        [ReadOnly] public NativeArray<int> dz;
        [ReadOnly] public NativeArray<int> costs;
        public int neighborCount;
        public int w, h, d;
        public int maxDist;
        public bool useMaskFilter;

        // Bucket head/tail przechowują INDEKS W ARENIE (nie indeks woksela) — patrz komentarz przy typie.
        public NativeArray<int> bucketHead;
        public NativeArray<int> bucketTail;
        public NativeList<int> arenaVoxel;
        public NativeList<int> arenaNext;

        public void Execute()
        {
            int len = w * h * d;
            int wh = w * h;

            // NativeArray/NativeList są uchwytami do pamięci natywnej — kopiowanie do lokalnych
            // zmiennych nie kopiuje danych, tylko pozwala lokalnej funkcji Push() uniknąć
            // niedozwolonego dostępu do 'this' wewnątrz struct.
            var bucketHeadLocal = bucketHead;
            var bucketTailLocal = bucketTail;
            var arenaVoxelLocal = arenaVoxel;
            var arenaNextLocal = arenaNext;

            for (int i = 0; i <= maxDist; i++) { bucketHeadLocal[i] = -1; bucketTailLocal[i] = -1; }

            void Push(int voxelIdx, int bucket)
            {
                int slot = arenaVoxelLocal.Length;
                arenaVoxelLocal.Add(voxelIdx);
                arenaNextLocal.Add(-1);
                if (bucketHeadLocal[bucket] == -1) { bucketHeadLocal[bucket] = slot; bucketTailLocal[bucket] = slot; }
                else { arenaNextLocal[bucketTailLocal[bucket]] = slot; bucketTailLocal[bucket] = slot; }
            }

            for (int i = 0; i < len; i++)
            {
                if (labels[i] > 0)
                {
                    dist[i] = 0;
                    int z = i / wh, rem = i % wh, y = rem / w, x = rem % w;
                    bool boundary;
                    if (useMaskFilter)
                    {
                        boundary =
                            (x > 0   && labels[i-1]  == 0 && originalThresholdMask[i-1]  == 255) ||
                            (x < w-1 && labels[i+1]  == 0 && originalThresholdMask[i+1]  == 255) ||
                            (y > 0   && labels[i-w]  == 0 && originalThresholdMask[i-w]  == 255) ||
                            (y < h-1 && labels[i+w]  == 0 && originalThresholdMask[i+w]  == 255) ||
                            (z > 0   && labels[i-wh] == 0 && originalThresholdMask[i-wh] == 255) ||
                            (z < d-1 && labels[i+wh] == 0 && originalThresholdMask[i+wh] == 255);
                    }
                    else
                    {
                        boundary =
                            (x > 0   && labels[i-1]  == 0) ||
                            (x < w-1 && labels[i+1]  == 0) ||
                            (y > 0   && labels[i-w]  == 0) ||
                            (y < h-1 && labels[i+w]  == 0) ||
                            (z > 0   && labels[i-wh] == 0) ||
                            (z < d-1 && labels[i+wh] == 0);
                    }
                    if (boundary) Push(i, 0);
                }
                else
                {
                    dist[i] = ushort.MaxValue;
                }
            }

            for (int b = 0; b <= maxDist; b++)
            {
                int slot = bucketHeadLocal[b];
                while (slot != -1)
                {
                    int idx = arenaVoxelLocal[slot];
                    // Wpis "duch" — patrz komentarz przy typie i oryginalny komentarz w VolumeMorphology.cs.
                    if (dist[idx] == b)
                    {
                        int currentLabel = labels[idx];
                        int iz = idx / wh, irem = idx % wh, iy = irem / w, ix = irem % w;

                        for (int dir = 0; dir < neighborCount; dir++)
                        {
                            int nx = ix + dx[dir];
                            int ny = iy + dy[dir];
                            int nz = iz + dz[dir];

                            if (nx >= 0 && nx < w && ny >= 0 && ny < h && nz >= 0 && nz < d)
                            {
                                int nIdx = nz * wh + ny * w + nx;
                                if (!useMaskFilter || originalThresholdMask[nIdx] == 255)
                                {
                                    int newDist = math.min((int)ushort.MaxValue, b + costs[dir]);
                                    if (newDist <= maxDist && newDist < dist[nIdx])
                                    {
                                        labels[nIdx] = currentLabel;
                                        dist[nIdx] = (ushort)newDist;
                                        Push(nIdx, newDist);
                                    }
                                }
                            }
                        }
                    }
                    slot = arenaNextLocal[slot];
                }
            }
        }
    }
}
