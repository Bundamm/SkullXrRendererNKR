using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using FellowOakDicom;
using UnityEngine;

namespace SkullXrRendererNKR.App
{
    /// <summary>
    /// Wynik obejrzenia folderu przed wczytaniem — tyle, ile da się powiedzieć z samych nagłówków,
    /// bez czytania pikseli (czyli w ułamku sekundy zamiast w dziesiątkach).
    /// </summary>
    public class ScanInfo
    {
        public string FolderPath;
        public int SliceCount;
        public string PatientName = "";
        public string StudyDescription = "";
        public string SeriesDescription = "";
        public string Modality = "";
        public int Width;
        public int Height;
        /// <summary>Powód odrzucenia folderu, gdy nie da się go wczytać.</summary>
        public string Error;

        /// <summary>
        /// Serie znalezione w PODFOLDERACH wskazanego folderu. Wypełniane, gdy sam folder nie zawiera
        /// plastrów — typowo bo wskazano folder studium (STU…), w którym leżą dopiero foldery serii
        /// (SER…). Taki folder nie jest błędem, tylko krokiem pośrednim: użytkownik ma wtedy wybrać
        /// jedną z serii, zamiast dostać komunikat, że „nie ma plików DICOM” w miejscu, w którym
        /// widzi je na wyciągnięcie ręki.
        /// </summary>
        public List<ScanInfo> NestedSeries = new List<ScanInfo>();

        public bool IsValid => SliceCount > 0 && string.IsNullOrEmpty(Error);

        /// <summary>Folder sam w sobie nie jest serią, ale zawiera serie do wyboru.</summary>
        public bool HasNestedSeries => !IsValid && NestedSeries.Count > 0;

        public string FolderName => string.IsNullOrEmpty(FolderPath) ? "" : new DirectoryInfo(FolderPath).Name;

        /// <summary>Jednolinijkowy opis do listy ostatnio używanych i do podglądu przed wczytaniem.</summary>
        public string Summary
        {
            get
            {
                if (HasNestedSeries)
                    return NestedSeries.Count == 1
                        ? "Zawiera 1 serię — wybierz ją poniżej."
                        : $"Zawiera {NestedSeries.Count} serie do wyboru — wskaż jedną poniżej.";

                if (!IsValid) return Error ?? "Brak plików DICOM w tym folderze.";

                var parts = new List<string> { $"{SliceCount} plastrów", $"{Width}×{Height}" };
                if (!string.IsNullOrWhiteSpace(Modality)) parts.Add(Modality);
                if (!string.IsNullOrWhiteSpace(SeriesDescription)) parts.Add(SeriesDescription);
                else if (!string.IsNullOrWhiteSpace(StudyDescription)) parts.Add(StudyDescription);
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Obsługa wyboru skanu przed wejściem w analizę: sprawdzenie, czy wskazany folder w ogóle zawiera
    /// serię DICOM, wyciągnięcie z niej opisu do pokazania użytkownikowi oraz pamięć ostatnio
    /// otwieranych folderów. Świadomie NIE dotyka renderowania — to warstwa czysto "bibliotekarska",
    /// żeby ekran startowy mógł powiedzieć „w tym folderze nic nie ma” bez uruchamiania całego
    /// kilkudziesięciosekundowego wczytywania (patrz LoadDicomData.LoadSeriesAsync).
    /// </summary>
    public static class ScanLibrary
    {
        private const string RecentKey = "SkullXr.RecentScanFolders";
        private const int MaxRecent = 8;
        private const char Separator = '\n'; // ścieżka Windows nie może zawierać znaku nowej linii

        /// <summary>
        /// Czyta same nagłówki plików w folderze i opisuje znalezioną serię. Nigdy nie rzuca — folder
        /// bez uprawnień albo bez DICOM-ów wraca jako ScanInfo z ustawionym Error.
        /// </summary>
        public static async UniTask<ScanInfo> InspectFolderAsync(string folderPath, CancellationToken ct = default)
        {
            var info = new ScanInfo { FolderPath = folderPath };

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                info.Error = "Taki folder nie istnieje.";
                return info;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folderPath);
            }
            catch (System.Exception ex)
            {
                info.Error = "Nie udało się odczytać folderu: " + ex.Message;
                return info;
            }

            if (files.Length == 0)
            {
                info.Error = "Folder jest pusty.";
                return info;
            }

            int validCount = 0;
            string firstDicomPath = null;

            await UniTask.RunOnThreadPool(() =>
            {
                object gate = new object();

                // Ten sam limit równoległości co przy właściwym wczytywaniu — fo-dicom alokuje bufory
                // per plik i przy kilkuset naraz potrafi rozjechać pamięć.
                Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                {
                    string file = files[i];
                    if (file.EndsWith(".meta")) return;

                    bool valid;
                    try { valid = DicomFile.HasValidHeader(file); }
                    catch { valid = false; }
                    if (!valid) return;

                    lock (gate)
                    {
                        validCount++;
                        // Kolejność z Directory.GetFiles wystarcza do wyciągnięcia opisu serii — właściwe
                        // sortowanie wzdłuż osi Z robi dopiero loader.
                        if (firstDicomPath == null || string.CompareOrdinal(file, firstDicomPath) < 0)
                            firstDicomPath = file;
                    }
                });
            }, cancellationToken: ct);

            ct.ThrowIfCancellationRequested();

            info.SliceCount = validCount;
            if (validCount == 0)
            {
                // Zanim odrzucimy folder, sprawdź, czy serie nie leżą o poziom niżej — wskazanie
                // folderu studium zamiast konkretnej serii jest naturalnym odruchem, bo to na tym
                // poziomie widać, z czego w ogóle jest wybór.
                info.NestedSeries = await FindSeriesInSubfoldersAsync(folderPath, ct);
                if (info.NestedSeries.Count == 0)
                    info.Error = "W tym folderze ani w jego podfolderach nie ma plików DICOM.";
                return info;
            }

            try
            {
                var ds = DicomFile.Open(firstDicomPath, FileReadOption.SkipLargeTags).Dataset;
                info.PatientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, "");
                info.StudyDescription = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, "");
                info.SeriesDescription = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, "");
                info.Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "");
                info.Width = ds.GetSingleValueOrDefault(DicomTag.Columns, 0);
                info.Height = ds.GetSingleValueOrDefault(DicomTag.Rows, 0);
            }
            catch (System.Exception ex)
            {
                // Pliki są, ale nie dają się odczytać — lepiej powiedzieć to teraz niż w połowie
                // wczytywania wolumenu.
                info.Error = "Nie udało się odczytać nagłówka serii: " + ex.Message;
            }

            return info;
        }

        /// <summary>
        /// Przegląda podfoldery (JEDEN poziom w dół) i zwraca te, które wyglądają na serie DICOM.
        /// Świadomie tylko jeden poziom: głębsze drzewo to już przeglądarka plików, a od tego jest
        /// systemowe okno wyboru folderu.
        ///
        /// Sonda jest celowo pobieżna — sprawdza nagłówek kilku pierwszych plików zamiast wszystkich.
        /// Studium potrafi mieć kilka serii po kilkaset plastrów każda, a pełne sprawdzenie
        /// kilku tysięcy plików tylko po to, żeby narysować listę do kliknięcia, zauważalnie
        /// zawiesiłoby ekran. Dokładne dane i tak policzy InspectFolderAsync po wybraniu serii.
        /// </summary>
        private static async UniTask<List<ScanInfo>> FindSeriesInSubfoldersAsync(string folderPath, CancellationToken ct)
        {
            var found = new List<ScanInfo>();

            string[] subfolders;
            try { subfolders = Directory.GetDirectories(folderPath); }
            catch { return found; }

            if (subfolders.Length == 0) return found;

            await UniTask.RunOnThreadPool(() =>
            {
                foreach (string sub in subfolders)
                {
                    ct.ThrowIfCancellationRequested();

                    string[] files;
                    try { files = Directory.GetFiles(sub); }
                    catch { continue; }

                    var candidates = files.Where(f => !f.EndsWith(".meta")).ToArray();
                    if (candidates.Length == 0) continue;

                    string probe = null;
                    for (int i = 0; i < candidates.Length && i < ProbeFileCount; i++)
                    {
                        try
                        {
                            if (!DicomFile.HasValidHeader(candidates[i])) continue;
                            probe = candidates[i];
                            break;
                        }
                        catch { /* nieczytelny plik nie przesądza o całym folderze */ }
                    }

                    if (probe == null) continue;

                    var entry = new ScanInfo
                    {
                        FolderPath = sub,
                        // Przybliżenie: liczba plików innych niż .meta. Wystarcza do wyboru serii,
                        // a jest darmowe w porównaniu z czytaniem każdego nagłówka.
                        SliceCount = candidates.Length
                    };

                    try
                    {
                        var ds = DicomFile.Open(probe, FileReadOption.SkipLargeTags).Dataset;
                        entry.PatientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, "");
                        entry.SeriesDescription = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, "");
                        entry.StudyDescription = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, "");
                        entry.Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "");
                        entry.Width = ds.GetSingleValueOrDefault(DicomTag.Columns, 0);
                        entry.Height = ds.GetSingleValueOrDefault(DicomTag.Rows, 0);
                    }
                    catch { /* seria jest, opisu nie ma — nadal da się ją wybrać */ }

                    found.Add(entry);
                }
            }, cancellationToken: ct);

            found.Sort((a, b) => string.CompareOrdinal(a.FolderPath, b.FolderPath));
            return found;
        }

        private const int ProbeFileCount = 5;

        // ------------------------------------------------------------------
        #region Ostatnio używane

        public static IReadOnlyList<string> GetRecentFolders()
        {
            string raw = PlayerPrefs.GetString(RecentKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return System.Array.Empty<string>();

            // Foldery, które w międzyczasie zniknęły (przepięty dysk sieciowy, przeniesiony pacjent),
            // odsiewamy przy odczycie — lista ma pokazywać to, co faktycznie da się otworzyć.
            return raw.Split(Separator)
                      .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                      .ToList();
        }

        public static string LastUsedFolder => GetRecentFolders().FirstOrDefault();

        /// <summary>
        /// Folder, od którego zaczyna przeglądanie systemowe okno wyboru, gdy nie ma jeszcze historii.
        /// Bez tego Windows otwiera dialog w katalogu roboczym procesu — czyli gdzieś w instalacji
        /// edytora, w miejscu zupełnie niezwiązanym ze skanami.
        ///
        /// Celujemy w folder STUDIUM, nie w samą serię: wybiera się folder, a w środku serii są już
        /// tylko pliki plastrów, więc lista podfolderów studium jest tym, co użytkownik chce zobaczyć.
        /// </summary>
        public static string DefaultBrowseFolder(LoadDicomData source)
        {
            string streaming = Application.streamingAssetsPath;

            if (source != null && !string.IsNullOrWhiteSpace(source.studyFolder))
            {
                string study = Path.Combine(streaming, source.studyFolder);
                if (Directory.Exists(study)) return study;

                // studyFolder bywa ścieżką wielopoziomową ("Scan/STU00001") — jeśli całość nie
                // istnieje, spróbuj chociaż jej pierwszego członu.
                string firstSegment = source.studyFolder.Replace('\\', '/').Split('/')[0];
                string partial = Path.Combine(streaming, firstSegment);
                if (Directory.Exists(partial)) return partial;
            }

            return Directory.Exists(streaming) ? streaming : null;
        }

        public static void RememberFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var list = GetRecentFolders().ToList();
            list.RemoveAll(p => string.Equals(p, path, System.StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            if (list.Count > MaxRecent) list.RemoveRange(MaxRecent, list.Count - MaxRecent);

            PlayerPrefs.SetString(RecentKey, string.Join(Separator.ToString(), list));
            PlayerPrefs.Save();
        }

        public static void ForgetFolder(string path)
        {
            var list = GetRecentFolders().ToList();
            if (list.RemoveAll(p => string.Equals(p, path, System.StringComparison.OrdinalIgnoreCase)) == 0) return;

            PlayerPrefs.SetString(RecentKey, string.Join(Separator.ToString(), list));
            PlayerPrefs.Save();
        }

        #endregion
    }
}
