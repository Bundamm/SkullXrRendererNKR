using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace SkullXrRendererNKR.App
{
    public enum AppState
    {
        /// <summary>Wybór skanu — nic nie jest wczytane, warstwy robocze schowane.</summary>
        Launcher,
        /// <summary>Trwa wczytywanie serii; widać wyłącznie postęp.</summary>
        Loading,
        /// <summary>Skan wczytany — obie warstwy robocze (monitor + gogle) aktywne.</summary>
        Analysis
    }

    /// <summary>
    /// Przełącza aplikację między ekranem startowym a właściwą analizą i decyduje, które warstwy
    /// interfejsu są w danym momencie widoczne. Warstwa na monitorze i menu na dłoni w goglach
    /// pojawiają się RAZEM, dopiero gdy jest co pokazywać — dopóki nie ma wczytanego wolumenu, oba
    /// sterowałyby pustką (a menu na dłoni potrafiłoby wyskoczyć w goglach nad ekranem wyboru pliku).
    /// </summary>
    public class AppFlow : MonoBehaviour
    {
        public static AppFlow Instance { get; private set; }

        [Header("Referencje")]
        public VolumeSession session;

        [Tooltip("Root warstwy roboczej na monitorze (panel operatora). Chowany na czas ekranu startowego.")]
        public GameObject operatorPanelRoot;

        [Tooltip("Root menu na dłoni w goglach (np. HandMenuBase ze sceny). Chowany, dopóki nie ma wczytanego skanu.")]
        public GameObject handMenuRoot;

        [Tooltip("Obiekty widoczne wyłącznie po wczytaniu skanu (np. VolumeCube). Puste = nic dodatkowego.")]
        public GameObject[] analysisOnlyObjects = Array.Empty<GameObject>();

        public AppState State { get; private set; } = AppState.Launcher;
        public event Action<AppState> OnStateChanged;

        /// <summary>Postęp bieżącego wczytywania — ekran startowy podpina się tutaj.</summary>
        public event Action<LoadDicomData.LoadProgress> OnLoadProgress;

        private CancellationTokenSource _loadCts;

        private void Awake()
        {
            Instance = this;
            if (session == null) session = FindObjectOfType<VolumeSession>();
            if (session == null)
                Debug.LogError("[AppFlow] Brak VolumeSession w scenie — ekran startowy nie będzie miał czego wczytać.");
        }

        private void Start()
        {
            // Jeśli LoadDicomData wczytuje serię domyślną sam (autoLoadOnStart, tryb pracy w Edytorze),
            // ekran startowy tylko przeszkadzałby — od razu wchodzimy w analizę.
            bool autoLoading = session != null && session.dicomData != null && session.dicomData.autoLoadOnStart;
            SetState(autoLoading ? AppState.Analysis : AppState.Launcher);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
        }

        /// <summary>
        /// Wczytuje wskazaną serię i — jeśli się udało — przechodzi do analizy. Zwraca false także
        /// przy anulowaniu, wtedy zostajemy na ekranie startowym.
        /// </summary>
        public async UniTask<bool> LoadScanAsync(string folderPath)
        {
            if (session == null) return false;
            if (State == AppState.Loading)
            {
                Debug.LogWarning("[AppFlow] Wczytywanie już trwa.");
                return false;
            }

            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();

            SetState(AppState.Loading);

            var progress = new Progress<LoadDicomData.LoadProgress>(p => OnLoadProgress?.Invoke(p));
            bool ok = await session.LoadScanAsync(folderPath, progress, _loadCts.Token);

            if (ok)
            {
                ScanLibrary.RememberFolder(folderPath);
                SetState(AppState.Analysis);
            }
            else
            {
                // Nieudane wczytanie zostawia aplikację bez wolumenu — wracamy tam, gdzie da się
                // wskazać inny folder, zamiast pokazywać puste narzędzia.
                SetState(AppState.Launcher);
            }

            return ok;
        }

        /// <summary>Przerywa trwające wczytywanie (przycisk Anuluj na ekranie postępu).</summary>
        public void CancelLoading()
        {
            if (State != AppState.Loading) return;
            _loadCts?.Cancel();
        }

        /// <summary>Zwalnia bieżący skan i wraca do ekranu startowego (przycisk „Wczytaj inny skan”).</summary>
        public void ReturnToLauncher()
        {
            if (State == AppState.Loading) CancelLoading();
            session?.UnloadScan();
            SetState(AppState.Launcher);
        }

        private void SetState(AppState next)
        {
            State = next;
            bool inAnalysis = next == AppState.Analysis;

            if (operatorPanelRoot != null) operatorPanelRoot.SetActive(inAnalysis);
            if (handMenuRoot != null) handMenuRoot.SetActive(inAnalysis);

            for (int i = 0; i < analysisOnlyObjects.Length; i++)
                if (analysisOnlyObjects[i] != null) analysisOnlyObjects[i].SetActive(inAnalysis);

            OnStateChanged?.Invoke(next);
        }
    }
}
