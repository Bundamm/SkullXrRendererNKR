using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LoadDicomData))]
public class LoadDicomDataEditor : Editor
{
    // Długie wyjaśnienia (progi HU, workflow) są zwinięte domyślnie — nie zaśmiecają widoku,
    // ale są jedno kliknięcie od użytkownika. Pełne opisy narzędzi są w tooltipach przycisków
    // (najedź myszką), nie w stałych blokach tekstu pod spodem.
    private bool _showDetails = false;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        LoadDicomData script = (LoadDicomData)target;

        GUILayout.Space(12);
        EditorGUILayout.LabelField("Narzędzia", EditorStyles.boldLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Dostępne tylko w Play Mode.", MessageType.Info);
            return;
        }

        var objManager = script.volumeObjectManager;
        if (objManager != null)
        {
            EditorGUILayout.LabelField("0. Kosze (wycięte, ale NIE skasowane)", EditorStyles.miniBoldLabel);

            // KAŻDY obiekt ma własny kosz, tworzony przy pierwszym cięciu z tego obiektu — więc lista
            // rośnie w trakcie pracy i nie da się jej zredukować do jednego, stałego przycisku.
            int binCount = 0;
            var targets = objManager.Targets;
            for (int i = 0; i < targets.Count; i++)
            {
                var bin = targets[i];
                if (!bin.IsCutBin) continue;
                binCount++;

                EditorGUILayout.BeginHorizontal();

                GUI.backgroundColor = bin.Visible ? new Color(0.9f, 0.75f, 0.3f) : new Color(0.5f, 0.5f, 0.5f);
                if (GUILayout.Button(new GUIContent(
                        (bin.Visible ? "Ukryj — " : "Pokaż — ") + bin.DisplayName,
                        "Do tego kosza trafia WSZYSTKO wycięte/usunięte z JEGO obiektu źródłowego (pędzel Cut, " +
                        "TunnelCut, Usuń wyspę) — nic nie jest kasowane na trwałe. Pokaż go, żeby " +
                        "zobaczyć co w nim jest; fizycznie odrębne rzeczy w środku da się dalej rozróżnić Pickerem i " +
                        "wydzielić pojedynczo (Wydziel jako obiekt), a także ciąć dalej — to, co utniesz z kosza, " +
                        "trafi do kosza TEGO kosza. Stoi OBOK swojego źródła, żeby nic się nie nakładało."),
                    GUILayout.Height(26)))
                {
                    objManager.SetVisible(bin, !bin.Visible);
                }

                bool aligned = objManager.IsBinAligned(bin);
                GUI.backgroundColor = aligned ? new Color(0.9f, 0.75f, 0.3f) : new Color(0.5f, 0.5f, 0.5f);
                if (GUILayout.Button(new GUIContent(
                        aligned ? "Odsuń" : "Nałóż",
                        "Nakłada kosz DOKŁADNIE na jego obiekt źródłowy (parentuje go pod nim), żeby zobaczyć SKĄD " +
                        "dokładnie co zostało wycięte — odtąd kosz CIĄGLE podąża za źródłem (obrót/przesunięcie tym " +
                        "samym uchwytem), a jego WŁASNY uchwyt jest na ten czas wyłączony. Drugie kliknięcie odsuwa " +
                        "go z powrotem obok i przywraca mu własny, niezależny uchwyt."),
                    GUILayout.Width(70), GUILayout.Height(26)))
                {
                    objManager.SetBinAligned(bin, !aligned);
                }

                EditorGUILayout.EndHorizontal();
            }
            GUI.backgroundColor = Color.white;

            if (binCount == 0)
            {
                EditorGUILayout.HelpBox("Nie ma jeszcze żadnego kosza — powstaje automatycznie przy pierwszym cięciu z danego obiektu.",
                    MessageType.None);
            }

            GUILayout.Space(8);
        }

        // Przełącznik diagnostyczny — pozwala odizolować, czy artefakt graficzny pochodzi z mapy
        // zajętości, czy z czegoś innego, bez zgadywania i bez ponownego uruchamiania sceny.
        EditorGUILayout.LabelField("Diagnostyka wydajności", EditorStyles.miniBoldLabel);
        bool skipping = script.enableEmptySkipping;
        GUI.backgroundColor = skipping ? new Color(0.4f, 0.7f, 0.4f) : new Color(0.85f, 0.5f, 0.3f);
        if (GUILayout.Button(new GUIContent(
                skipping ? "Przeskakiwanie pustki: WŁĄCZONE (kliknij, by wyłączyć)" : "Przeskakiwanie pustki: WYŁĄCZONE (kliknij, by włączyć)",
                "Wyłączenie sprawia, że raymarching maszeruje krok po kroku przez cały wolumen, zupełnie pomijając mapę " +
                "zajętości — wolniej, ale bez jej udziału. Jeśli dziury/paski ZNIKAJĄ po wyłączeniu, przyczyna jest w mapie " +
                "zajętości. Jeśli ZOSTAJĄ, winne jest coś innego (np. limit kroków promienia albo format tekstury)."),
            GUILayout.Height(26)))
        {
            script.enableEmptySkipping = !skipping;
            script.RefreshRenderingSettings();
        }
        GUI.backgroundColor = Color.white;

        GUILayout.Space(8);

        EditorGUILayout.LabelField("1. Segmentacja", EditorStyles.miniBoldLabel);
        GUI.backgroundColor = new Color(0.3f, 0.6f, 0.9f);
        if (GUILayout.Button(new GUIContent(
                "Wygeneruj Maskę",
                "Przelicza segmentację od zera przy aktualnych Morph Threshold HU / Erosion Radius / Expand Radius. " +
                "Kliknij po KAŻDEJ zmianie tych trzech parametrów."),
            GUILayout.Height(32)))
        {
            script.GenerateMorphologyMask();
        }
        GUI.backgroundColor = Color.white;

        GUILayout.Space(8);

        EditorGUILayout.LabelField("2. Picker → usuń spickowaną wyspę", EditorStyles.miniBoldLabel);
        using (new EditorGUI.DisabledScope(!script.morphPickedVoxel.HasValue))
        {
            GUI.backgroundColor = new Color(0.85f, 0.35f, 0.35f);
            if (GUILayout.Button(new GUIContent(
                    script.morphPickedVoxel.HasValue
                        ? "Usuń spickowaną wyspę"
                        : "Usuń spickowaną wyspę (najpierw użyj Pickera)",
                    "Usuwa dokładnie to, co Picker właśnie wyizolował, bez ponownego trafiania weń promieniem — " +
                    "obie ścieżki czysto topologiczne (bez pasma gęstości), różnią się tylko progiem:\n" +
                    "• Etykieta kostna (fragment kości) → próg Visible Material Threshold HU (łapie też gąbczaste wnętrze) — " +
                    "jeśli to fizycznie główna struktura (czaszka), nic się nie usunie, do tego służy Cut.\n" +
                    "• Akcesorium bez etykiety kostnej (np. maseczka/korek, łóżko skanera) → próg Skin Exclude " +
                    "Threshold HU (skóra nie liczy się jako połączenie) → usuwa DOKŁADNIE to, co właśnie widać " +
                    "wyizolowane, ignorując stykającą się skórę."),
                GUILayout.Height(28)))
            {
                script.DeletePickedIsland();
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(4);

            GUI.backgroundColor = new Color(0.3f, 0.75f, 0.55f);
            if (GUILayout.Button(new GUIContent(
                    script.morphPickedVoxel.HasValue
                        ? "Wydziel jako obiekt"
                        : "Wydziel jako obiekt (najpierw użyj Pickera)",
                    "Zamiast trwale kasować, wydziela dokładnie to, co Picker właśnie wyizolował, jako NOWY, " +
                    "niezależnie chwytalny i dalej-cięty obiekt na scenie (te same uchwyty co czaszka) — " +
                    "patrz VolumeObjectManager. Wymaga VolumeObjectManager gdzieś w scenie."),
                GUILayout.Height(28)))
            {
                script.ExtractPickedIslandAsObject();
            }
            GUI.backgroundColor = Color.white;
        }

        GUILayout.Space(8);


        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.3f);
        if (GUILayout.Button(new GUIContent(
                "Reset Cuts — cofnij wszystko",
                "Przywraca model do stanu sprzed WSZYSTKICH cięć: pędzla, gumki i tunelu."),
            GUILayout.Height(28)))
        {
            script.ResetCuts();
        }
        GUI.backgroundColor = Color.white;

        GUILayout.Space(8);

        // Tylko jedno, faktycznie istotne w danym momencie ostrzeżenie — nie wszystkie na raz.
        if (script.morphMaskToKeep == 0)
        {
            EditorGUILayout.HelpBox("Morph Mask To Keep = 0 → maskowanie wyłączone, widać wszystko.", MessageType.Info);
        }

        _showDetails = EditorGUILayout.Foldout(_showDetails, "Szczegóły: kolejność progów HU", true);
        if (_showDetails)
        {
            EditorGUILayout.HelpBox(
                "Trzy progi HU są CELOWO niezależne — zmiana jednego nie wpływa na pozostałe:\n\n" +
                "• Morph Threshold HU (tutaj) — segmentacja dla Pick/RemoveIsland. Wysoko (~250-350), gęstość kości.\n" +
                "• Cut Threshold HU (na VolumePicker) — co może trafić Cut/TunnelCut. Nisko (np. -100), łapie wszystko widoczne.\n" +
                "• Visible Material Threshold HU (tutaj) — od jakiej gęstości shader barwi naczynia i w co trafia Picker. Nisko (~25-80).",
                MessageType.None);
        }
    }
}
