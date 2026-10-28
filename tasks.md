# Plan Rozwoju: SkullXrRenderer (PC Streaming & Ultra Detail)

Ten dokument zawiera listę zadań niezbędnych do osiągnięcia chirurgicznej precyzji wizualizacji przy użyciu mocy obliczeniowej PC streamowanej na gogle.

## Faza 1: Streaming i Ultra-Detail Raymarching (PC Power)
Przeniesienie ciężaru na PC pozwala na renderowanie bez kompromisów jakościowych.

- [ ] **Zadanie 1.1: Implementacja Streamingu (Remoting over USB-C)**
  - [ ] Konfiguracja OpenXR: Włączenie "Holographic Remoting" w zakładce Features.
  - [ ] Priorytet: Połączenie po kablu USB (Wi-Fi zostawiamy na później).
  - [ ] Utworzenie skryptu `RemotingConnectionManager`: Automatyczne łączenie z 127.0.0.1 (lub innym IP USB).
  - [ ] Maksymalny Bitrate: Ustawienie parametrów przesyłu pod bezstratną jakość przez kabel.
- [ ] **Zadanie 1.2: Optymalizacja Shadera pod High Fidelity**
  - [ ] Zmniejszenie `_StepSize` do 0.0005 (ponad 1000 kroków na promień).
  - [ ] Implementacja jitteringu 3D dla eliminacji pasmowania (banding).
  - [ ] Ucięcie elementów mocno odstających od czaszki (np. o więcej niż 10% rozmiaru czaszki)
- [ ] **Zadanie 1.3: "Connectivity" - Łączenie Naczyń**
  - [ ] Przebudowa `NeighborVesselScore` na filtr 26-sąsiadów (sfera).
  - [ ] Dodanie algorytmu lokalnego zagęszczania (Density Accumulation) dla eliminacji "kropkowania" naczyń.
  - [ ] [OPCJONALNIE] Implementacja uproszczonego filtru Hessian (Frangi) do rozpoznawania struktur cylindrycznych.

## Faza 2: Medyczny Interfejs i Kontrola Warstw
Sterowanie przez MRTK3 gestami rąk.

- [ ] **Zadanie 2.1: Panel Kontroli Warstw (Dynamic Layering)**
  - [ ] Suwak "Vessel Connectivity" (płynne łączenie/izolowanie plam).
  - [ ] Suwak "Bone Opacity" (przezroczystość czaszki w raymarchingu).
  - [ ] Tryb "X-Ray" (naczynia widoczne zawsze przez kości).
- [ ] **Zadanie 2.2: Clipping Plane (MRTK3 Pinch Slider)**
  - [ ] Integracja z nowym systemem MRTK3 UX Components.

## Faza 3: Dynamiczne Zarządzanie Modelami
- [ ] **Zadanie 3.1: Manager Stosów Skanów**
  - [ ] Automatyczne listowanie folderów z `StreamingAssets/Scan` na PC.
- [ ] **Zadanie 3.2: Szybka zamiana pacjenta bez restartu aplikacji**

## Faza 4: Zaawansowane Cieniowanie (Visual Wow)
- [ ] **Zadanie 4.1: Local Ambient Occlusion (Wolumetryczny)**
  - [ ] Obliczanie stopnia zasłonięcia światła przez otaczające voksle (pociemnianie wgłębień w czaszce).
- [ ] **Zadanie 4.2: PBR Specular dla kości (Precyzyjne odblaski chirurgiczne)**
