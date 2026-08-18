# Konfiguracja UI w Unity (MRTK3)

Teraz, gdy skrypt z logiką jest gotowy (`DynamicUIManager.cs`), trzeba złożyć interfejs. Na podstawie Twojego zrzutu ekranu widać, że używasz **MRTK3 w trybie Canvas**. Wbudowany system MRTK bardzo ładnie współpracuje ze standardowymi kontrolkami ułatwionymi na płótnie!

Oto dokładne zestawienie, **z jakich elementów widocznych w Twoim menu** masz skorzystać i do czego je podpiąć:

## 1. Suwaki (Płynna zmiana wartości)
Aby kontrolować wartości w przedziale (np. jasność, grubość, rotacja), musisz dodać Slider. W MRTK3 opartym o Canvas standardowo używamy wbudowanego Slidera z menu Unity.

**Co kliknąć?**
Z rozwijanego menu wybierz: `UI -> Slider`

| Element na Scenie | Metoda w `DynamicUIManager` (Event: `OnValueChanged`) |
| :--- | :--- |
| **Slider (Window Center)** | `DynamicUIManager.OnWindowCenterSliderChanged` |
| **Slider (Window Width)** | `DynamicUIManager.OnWindowWidthSliderChanged` |
| **Slider (Cut Plane)** | `DynamicUIManager.OnCutHeightSliderChanged` |
| **Slider (Connectivity)**| `DynamicUIManager.OnVesselContinuitySliderChanged` |
| **Slider (Bone Opacity)**| `DynamicUIManager.OnBoneOpacitySliderChanged`|
| **Slider (Rotacja X/Y/Z)**| `DynamicUIManager.OnShaderRotationXSliderChanged` *(odpowiednio dla osi)* |

> [!WARNING]
> Kiedy podpinasz wydarzenie (On Value Changed) upewnij się, że wybierasz z listy w górnej sekcji **Dynamic float**. Dzięki temu Unity wyśle aktualną wartość suwaka bezpośrednio do funkcji.

## 2. Tryby (Włącz / Wyłącz)
Aby aktywować specjalne tryby (np. tryb samych naczyń, ukrywający kości), używamy przycisków typu Checkbox.

**Co kliknąć?**
Z rozwijanego menu wybierz: `UI -> MRTK -> Action Button Checkbox` (lub zastępczo z głównego: `UI -> Toggle`).

| Element na Scenie | Metoda w `DynamicUIManager` (Event: `OnValueChanged`) |
| :--- | :--- |
| **Checkbox (Solo Naczyń)** | `DynamicUIManager.OnVesselSoloToggleChanged` |

> [!NOTE]
> W przypadku Toggle/Checkbox upewnij się, że podpinasz akcję pod wydarzenie wysyłające zmienną `bool` (prawda/fałsz). MRTK Checkbox posiada dedykowane zdarzenia ToggleEvent.

## 3. Akcje Natychmiastowe (Obróć o 90 stopni)
Zamiast bawić się sliderem, czasami chirurg chce szybko "przerzucić" czaszkę o równy kąt prosty w lewo lub w prawo.

**Co kliknąć?**
Z rozwijanego menu wybierz: `UI -> MRTK -> Action Button` (ewidentnie sprawdzi się tutejszy standardowy guzik).

| Element na Scenie | Metoda w `DynamicUIManager` (Event: `OnClick / OnButtonPressed`) |
| :--- | :--- |
| **Button "W Lewo"** | `DynamicUIManager.RotateObjectLeft90()` |
| **Button "W Prawo"** | `DynamicUIManager.RotateObjectRight90()` |
| **Button "W Górę"** | `DynamicUIManager.RotateObjectUp90()` |
| **Button "W Dół"**| `DynamicUIManager.RotateObjectDown90()` |

## Podsumowanie i Struktura Sceny
Odpowiadając na Twoje "jakie elementy dodać?":
Twoje płótno (`Canvas` oparte o nową metodę renderingu), na którym się znajdujesz (pokazane na zrzucie ekranu), zostało prawidłowo nadpisane przez MRTK. 

Wszystkie kontrolki wrzucaj jako elementy podrzędne (dzieci) podążając za hierarchią:
1. `UIManager` (tutaj najlepiej zostawić skrypt `DynamicUIManager.cs`)
2. `Canvas` 
   - `Slider - Jasnosc`
   - `Slider - Opacity`
   - `Action Button - Obrót L`
   - `Action Button Checkbox - Tryb Naczyń`

Podłącz wszystko w Inspektorze (przeciągając odpowiednio i wybierając funkcje) i wypróbuj w trybie Play. MRTK3 z Canvas automatycznie przekaże dotyk do tych elementów z użyciem rąk czy wskaźnika.

## 4. Latające okno (Przesuwalny Panel Pływający)

Aby cały Twój Canvas stał się panelem, który użytkownik w goglach może ucieleśniać i dowolnie przesuwać wokół siebie ("latające okno"), postępuj zgodnie z poniższymi krokami:

1. Zaznacz w hierarchii swój obiekt **`Canvas`**.
2. W oknie Inspektora upewnij się, że tryb renderowania masz ustawiony na **"World Space"** (na screenie jest to zrobione prawidłowo, Event Camera to Main Camera).
3. Kliknij na dole `Add Component` i dodaj **`Box Collider`**. 
   - *Uwaga:* Pamiętaj by zeskalować go ręcznie do rozmiarów płótna okna lub wstawić w nim odpowiedni `Size` i wcisnąć `Edit Collider`. To pole powstrzyma palce przed "przelatywaniem" przez puste pole ułatwiając chwyt i nada fizyczne wymiary oknu.
4. Kliknij `Add Component` ponownie i wyszukaj **`Object Manipulator`** (to wbudowany skrypt od MRTK3).
   - Skrypt ten automatycznie umożliwi złapanie okna dłonią (Near Interaction) lub promieniem z palca (Far Interaction) i przesunięcie w przestrzeni wolumetrycznej.  
   - W sekcji *Allowed Manipulations* skryptu `ObjectManipulator` domyślnie włączone są: `Spacja, Skala, Rotacja`. Wyłączenie opcji Scale zabroni użytkownikowi na przypadkowe powiększanie panelu obiema dłońmi.

> [!TIP]
> Jeśli obawiasz się, że przesuwając suwaki użytkownik przemieści cały panel (chwyt zaledwie kilkadziesiąt pikseli obok slidera), możesz zastosować inną metodę.  
> Stwórz u góry swojego Canvasa pusty klasyczny przycisk / półkę jako **"Pasek Tytułowy / Uchwyt"**. Przydziel _wyłącznie_ do niego `Box Collider` i `Object Manipulator`. W `Object Manipulator` istnieje wówczas pole `Host Transform` - przeciągnij do niego cały Canvas. Dzięki temu chwytając za "górny pasek" przesuniesz całe okno, a dolna przestrzeń posłuży w 100% twardemu operowaniu elementami suwaków.
