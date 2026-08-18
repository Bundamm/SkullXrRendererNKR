# Plan Rozwoju: SkullXrRenderer (PC Streaming & Ultra Detail)

Ten dokument zawiera listę zadań niezbędnych do osiągnięcia chirurgicznej precyzji wizualizacji przy użyciu mocy obliczeniowej PC streamowanej na gogle.

## Faza 1: Streaming i Ultra-Detail Raymarching (PC Power)
Przeniesienie ciężaru na PC pozwala na renderowanie bez kompromisów jakościowych.

- [x] **Zadanie 1.1: Implementacja Streamingu (Remoting over USB-C)**
  - [x] Konfiguracja OpenXR: Włączenie "Holographic Remoting" w zakładce Features.
  - [x] Priorytet: Połączenie po kablu USB (Wi-Fi zostawiamy na później).
  - [x] Utworzenie skryptu `RemotingConnectionManager`: Podstawowe połączenie z goglami.
  - [x] Stabilizacja skali i eliminacja błędów `NaN` (LoadDicomData.cs).
- [x] **Zadanie 1.2: Optymalizacja Shadera pod High Fidelity**
  - [x] Poprawka renderowania obuocznego (Stereo SPI w Shaderze).
  - [x] Zmniejszenie `_StepSize` do 0.0005 (ponad 1000 kroków na promień).
  - [x] Implementacja jitteringu 3D dla eliminacji pasmowania (banding).
  - [x] Ucięcie elementów mocno odstających od czaszki (Noise Threshold & Distance Cutoff).
  - [x] **[NOWE] Vessel Solo Mode**: Specjalny tryb widoczności samych naczyń do kalibracji HU.
- [x] **Zadanie 1.3: "Connectivity" - Łączenie Naczyń**
  - [x] Przebudowa `NeighborVesselScore` na filtr 26-sąsiadów (adaptacyjny szachownicowy).
  - [x] Dodanie algorytmu lokalnego zagęszczania (Connectivity Boost) dla eliminacji "kropkowania" naczyń.
  - [x] Implementacja suwaka `_VesselContinuity` dla kontroli płynności.
- [x] **Zadanie 1.5: [NOWE] Vessel Thickness Coloring**
  - [x] Mapowanie kolorów naczyń na podstawie `NeighborScore` (grube = jasne, cienkie = ciemne).
  - [x] Implementacja `_VesselColors` (Gradient/Lerp).

## Faza 2: Medyczny Interfejs i Kontrola Warstw
Sterowanie przez MRTK3 gestami rąk.

- [/] **Zadanie 2.1: Panel Kontroli Warstw (Dynamic Layering)**
  - [ ] Suwak "Vessel Connectivity" (płynne łączenie/izolowanie plam).
  - [x] Suwak "Bone Opacity" (płynne znikanie kości - Vessel Solo Blend).
  - [ ] Tryb "X-Ray" (naczynia widoczne zawsze przez kości).
- [ ] **Zadanie 2.2: Clipping Plane (MRTK3 Pinch Slider)**
  - [ ] Integracja z nowym systemem MRTK3 UX Components.
- [ ] **Zadanie 2.3: [NOWE] Interaction 3.0 (Rotation Handles)**
  - [ ] Implementacja `Bounds Control` (uchwyty w rogach do rotacji przód-tył).
  - [ ] Przycisk "Reset Orientation" na panelu.
- [ ] **Zadanie 2.4: [NOWE] Dynamiczna Zmiana Wartości UI (Bez Resetu)**
  - [ ] Dodanie przycisków i suwaków (MRTK/Unity UI) do zmiany parametrów renderera podczas działania aplikacji bez konieczności restartu (Live Update parametrow w material/shader).

## Faza 3: Dynamiczne Zarządzanie Modelami
- [ ] **Zadanie 3.1: Manager Stosów Skanów**
  - [ ] Automatyczne listowanie folderów z `StreamingAssets/Scan` na PC.
- [ ] **Zadanie 3.2: Szybka zamiana pacjenta bez restartu aplikacji**

## Faza 4: Zaawansowane Cieniowanie (Visual Wow)
- [ ] **Zadanie 4.1: Local Ambient Occlusion (Wolumetryczny)**
  - [ ] Obliczanie stopnia zasłonięcia światła przez otaczające voksle (pociemnianie wgłębień w czaszce).
- [ ] **Zadanie 4.2: PBR Specular dla kości (Precyzyjne odblaski chirurgiczne)**
