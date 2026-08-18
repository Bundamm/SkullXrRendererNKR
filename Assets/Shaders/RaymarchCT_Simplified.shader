Shader "Unlit/Volume/RaymarchCT_Surface"
{
    Properties
    {
        _VolumeTex      ("Volume Texture", 3D) = "" {}
        _StepSize       ("Step Size", Range(0.0002, 0.01)) = 0.002
        
        _HUMin          ("HU Min (normalized base)", Float) = -1000
        _HUMax          ("HU Max (normalized base)", Float) = 3000
        
        // Window / Level – przestawiasz to jak w oprogramowaniu CT
        // Skin:   WL=-151 WW=366  => Center=-151 Width=366
        // Body:   WL=191  WW=353  => Center=191  Width=353
        // Bone:   WL=385  WW=340  => Center=385  Width=340
        _WindowCenter   ("Window Center (HU)", Float) = 191
        _WindowWidth    ("Window Width (HU)",  Float) = 353
        

        
        // Próg alpha przy którym traktujemy punkt jako "powierzchnię"
        _SurfaceThreshold ("Surface Hit Threshold", Range(0.01, 0.99)) = 0.25
        
        // Oświetlenie (Złagodzone dla mniejszych odblasków blokujących użyteczne dane)
        _LightDir        ("Light Direction",  Vector) = (0.5, 0.7, 1.0, 0)
        _AmbientStrength ("Ambient Strength", Range(0.0, 1.0)) = 0.35
        _DiffuseStrength ("Diffuse Strength", Range(0.0, 2.0)) = 0.85
        _SpecularStrength("Specular Strength",Range(0.0, 2.0)) = 0.15
        _SpecularPower   ("Specular Power",   Range(1.0, 128.0)) = 28.0
        
        // Naczynia – dodatkowy cienki accumulation pass przed surface hitem
        _VesselAccumStrength ("Vessel Accumulation", Range(0.0, 1.0)) = 0.85

        // Dolna granica (znormalizowana 0..1, ta sama skala co _VolumeTex) poniżej której materiał
        // NIE wchodzi do warstwy naczyniowej. Bez tego progu warstwa akumulacyjna renderowała widoczną
        // "poświatę" z materiału o gęstości bliskiej powietrzu (np. piankowa poduszka skanera) — czegoś,
        // co żadne narzędzie CPU (Inspect/Cut/RemoveIsland, wszystkie oparte o twarde progi HU) nigdy
        // nie uznawało za "coś tam jest", bo indywidualnie żadna próbka nie przekraczała ich progu.
        // Ustawiane z C# na tę samą znormalizowaną wartość co Auto Strip Threshold HU (LoadDicomData.
        // UpdateMorphologyMaskID), żeby to, co widać, i to, co da się automatycznie usunąć, było spójne.
        _VesselMinNorm ("Vessel Accumulation Min (Normalized)", Range(0.0, 1.0)) = 0.0
        
        _ClipPlane      ("Clip Plane (World)", Vector) = (0, 1, 0, 0)
        _TransferTex    ("Transfer Function (1D)", 2D) = "black" {}
        
        _MinBounds      ("Min Bounds", Vector) = (-0.5, -0.5, -0.5, 0)
        _MaxBounds      ("Max Bounds", Vector) = (0.5, 0.5, 0.5, 0)
        
        _RotationOffset ("Rotation Euler Offset (XYZ)", Vector) = (0,0,0,0)
        
        [Header(Morphology Mask)]
        _MaskTex        ("Mask Texture", 3D) = "black" {}
        _MaskIDToKeep   ("Mask ID To Keep (0=Off)", Float) = 0
        _MaskKeepBackground ("Keep Background Tissue", Float) = 1
        _MaskNegate     ("Mask Negate (Hide Selected)", Float) = 0
        _MaskExtraHide1 ("Extra Mask Hide 1", Float) = 0
        _MaskExtraHide2 ("Extra Mask Hide 2", Float) = 0
        _MaskExtraHide3 ("Extra Mask Hide 3", Float) = 0

        [Header(Piece Ownership)]
        // Trwały (NIE przeliczany przez segmentację) mask własności — 0 = główny wolumen,
        // N = wydzielony obiekt nr N. Patrz RaymarchCT.shader dla pełnego opisu.
        _OwnerTex       ("Piece Owner Mask", 3D) = "black" {}
        _OwnerFilterID  ("Owner Filter ID (0=main volume)", Float) = 0
        _SubLocalCenter ("Sub-Volume Local Center", Vector) = (0,0,0,0)
        _SubLocalSize   ("Sub-Volume Local Size", Vector) = (1,1,1,0)

        [Header(Empty Space Skipping)]
        // Zgrubna mapa zajętości: 1 teksel = maksymalna gęstość w bloku 8^3 wokseli
        // (patrz VolumeOccupancy.compute / LoadDicomData.BuildOccupancyMap). Pozwala przeskoczyć
        // całe puste bloki jednym krokiem zamiast maszerować przez powietrze po _StepSize.
        _OccupancyTex     ("Occupancy (coarse, per-owner)", 3D) = "white" {}
        // Poniżej tej wartości blok na pewno nic nie wnosi do obrazu. Ujemna = mapa niedostępna,
        // przeskakiwanie wyłączone (bezpieczny fallback do starego zachowania).
        _EmptySkipDensity ("Empty Skip Threshold (neg = off)", Float) = -1
        // Twardy limit iteracji pętli. MUSI wystarczyć na przejście bryły na wylot po przekątnej
        // (sqrt(3) / _StepSize), inaczej promień kończy się w środku modelu — patrz komentarz w pętli.
        _MaxRaySteps      ("Max Ray Steps", Float) = 4096
        // Rozmiar komórki mapy w przestrzeni UVW — potrzebny do WYLICZENIA wyjścia z bieżącej
        // komórki zamiast skakania o stałą długość (patrz komentarz przy pętli).
        _OccupancyCellUVW ("Occupancy Cell Size (UVW)", Vector) = (1,1,1,0)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler3D _VolumeTex;
            float4 _VolumeTex_TexelSize;
            float _StepSize;

            float _HUMin;
            float _HUMax;
            float _WindowCenter;
            float _WindowWidth;

            float _SurfaceThreshold;

            sampler2D _TransferTex;

            float4 _ClipPlane;

            float4 _LightDir;
            float  _AmbientStrength;
            float  _DiffuseStrength;
            float  _SpecularStrength;
            float  _SpecularPower;
            float  _VesselAccumStrength;
            float  _VesselMinNorm;

            float4 _MinBounds;
            float4 _MaxBounds;
            float4 _RotationOffset;

            sampler3D _MaskTex;
            float _MaskIDToKeep;
            float _MaskKeepBackground;
            float _MaskNegate;
            float _MaskExtraHide1;
            float _MaskExtraHide2;
            float _MaskExtraHide3;

            sampler3D _OwnerTex;
            float _OwnerFilterID;
            sampler3D _OccupancyTex;
            float _EmptySkipDensity;
            float4 _OccupancyCellUVW;
            float _MaxRaySteps;
            float4 _SubLocalCenter;
            float4 _SubLocalSize;

            // -------------------------------------------------------
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 objPos : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos    = UnityObjectToClipPos(v.vertex);
                o.objPos = v.vertex.xyz;
                return o;
            }

            // -------------------------------------------------------
            bool RayBoxIntersect(float3 ro, float3 rd, out float tMin, out float tMax)
            {
                float3 invDir = 1.0 / rd;
                float3 t0 = (-0.5 - ro) * invDir;
                float3 t1 = ( 0.5 - ro) * invDir;
                float3 tS = min(t0, t1);
                float3 tB = max(t0, t1);
                tMin = max(max(tS.x, tS.y), tS.z);
                tMax = min(min(tB.x, tB.y), tB.z);
                return tMax >= max(tMin, 0.0);
            }

            // Tani gradient dla ŚCIEŻKI AKUMULACJI (naczynia/tkanki miękkie) — 3 pobrania zamiast 6.
            // Różnice PRZEDNIE zamiast centralnych, z ponownym użyciem próbki środkowej, którą pętla
            // i tak już pobrała. To jest gorąca ścieżka: wykonuje się wielokrotnie na KAŻDYM promieniu
            // (w przeciwieństwie do trafienia w powierzchnię, które kończy promień), więc połowa
            // pobrań mniej przekłada się wprost na klatki. Kosztem jest przesunięcie normalnej o pół
            // woksela — niewidoczne tutaj, bo normalna służy wyłącznie do miękkiego ndotl
            // wymieszanego przez lerp(0.5, ndotl, edgeF), a nie do ostrego rozbłysku.
            float3 GetNormalAndMagFast(float3 uvw, float centerSample, out float mag)
            {
                float3 d = _VolumeTex_TexelSize.xyz;
                float3 n;
                n.x = tex3Dlod(_VolumeTex, float4(uvw + float3(d.x,0,0),0)).r - centerSample;
                n.y = tex3Dlod(_VolumeTex, float4(uvw + float3(0,d.y,0),0)).r - centerSample;
                n.z = tex3Dlod(_VolumeTex, float4(uvw + float3(0,0,d.z),0)).r - centerSample;
                // Różnica przednia ma o połowę mniejszą bazę niż centralna, więc skalujemy magnitudę
                // x2, żeby próg krawędzi (gradMag * 12) zachowywał się tak samo jak dotąd.
                mag = length(n) * 2.0;
                return (mag < 0.0001) ? float3(0,1,0) : -normalize(n);
            }

            // Gradient (normalna) + magnituuda w jednym przelocie — różnice CENTRALNE, pełna jakość.
            // Zostaje na trafieniu w powierzchnię: tam promień się kończy, więc koszt ponosimy raz na
            // piksel, a normalna steruje pełnym Phongiem ze specularem, gdzie jakość widać.
            float3 GetNormalAndMag(float3 uvw, out float mag)
            {
                float3 d = _VolumeTex_TexelSize.xyz;
                float3 n;
                n.x = tex3Dlod(_VolumeTex, float4(uvw + float3(d.x,0,0),0)).r
                    - tex3Dlod(_VolumeTex, float4(uvw - float3(d.x,0,0),0)).r;
                n.y = tex3Dlod(_VolumeTex, float4(uvw + float3(0,d.y,0),0)).r
                    - tex3Dlod(_VolumeTex, float4(uvw - float3(0,d.y,0),0)).r;
                n.z = tex3Dlod(_VolumeTex, float4(uvw + float3(0,0,d.z),0)).r
                    - tex3Dlod(_VolumeTex, float4(uvw - float3(0,0,d.z),0)).r;
                mag = length(n);
                return (mag < 0.0001) ? float3(0,1,0) : -n / mag;
            }

            // -------------------------------------------------------
            // Phong shading w przestrzeni obiektu
            float3 PhongShading(float3 normal, float3 rayDir, float3 baseColor, float gradMag)
            {
                float3 L = normalize(_LightDir.xyz);
                float3 V = -rayDir;
                float3 R = reflect(-L, normal);

                float  ndotl    = max(0.0, dot(normal, L));
                float  specular = pow(max(0.0, dot(R, V)), _SpecularPower);

                // Gradient magnitude wzmacnia diffuse + specular (krawędzie wyraźniejsze)
                float edgeFactor = saturate(gradMag * 12.0);

                float3 ambient  = _AmbientStrength * baseColor;
                float3 diffuse  = _DiffuseStrength * ndotl * baseColor * edgeFactor;
                float3 spec     = _SpecularStrength * specular * float3(1,1,1) * edgeFactor;

                // W środku tkanki (brak krawędzi) – mocny ambient żeby nie czarniało
                float3 flatFill = baseColor * (1.0 - edgeFactor) * _DiffuseStrength * 0.6;

                return ambient + diffuse + flatFill + spec;
            }

            // -------------------------------------------------------
            // Remapowanie HU surowego (znormalizowanego 0-1) przez Window/Level
            float ApplyWindowLevel(float rawNorm)
            {
                float huVal  = rawNorm * (_HUMax - _HUMin) + _HUMin;
                float wMin   = _WindowCenter - _WindowWidth * 0.5;
                float wMax   = _WindowCenter + _WindowWidth * 0.5;
                return saturate((huVal - wMin) / (wMax - wMin));
            }

            // -------------------------------------------------------
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // Ray setup
                float3 rayOrigin = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos,1)).xyz;

                // Rotacja wewnętrzna (shader-level)
                float3 angles = _RotationOffset.xyz * 0.0174532925;
                float3 s, c;
                sincos(angles.x, s.x, c.x);
                sincos(angles.y, s.y, c.y);
                sincos(angles.z, s.z, c.z);
                float3x3 rotX = float3x3(1,0,0, 0,c.x,-s.x, 0,s.x,c.x);
                float3x3 rotY = float3x3(c.y,0,s.y, 0,1,0, -s.y,0,c.y);
                float3x3 rotZ = float3x3(c.z,-s.z,0, s.z,c.z,0, 0,0,1);
                float3x3 rotFinal = mul(rotZ, mul(rotY, rotX));

                float3 rayDir = normalize(i.objPos - rayOrigin);

                float tEnter, tExit;
                if (!RayBoxIntersect(rayOrigin, rayDir, tEnter, tExit))
                    discard;

                tEnter = max(tEnter, 0.0);

                // Jitter anty-banding (z powrotem z Time na prośbę)
                float2 screenUV = i.pos.xy / _ScreenParams.xy;
                float3 seed     = float3(screenUV, _Time.y);
                float  jitter   = frac(sin(dot(seed, float3(12.9898,78.233,45.164))) * 43758.5453);
                float  t        = tEnter + jitter * _StepSize;

                float rayLen  = tExit - tEnter;
                // Limit iteracji podaje C# (LoadDicomData.ApplyRaymarchQuality) i jest wyliczony tak,
                // żeby ZAWSZE starczyło na przejście bryły po przekątnej przy aktualnym _StepSize.
                // Zakuta wcześniej wartość 2048 nie starczała: przekątna to sqrt(3) w przestrzeni
                // lokalnej, więc przy kroku 0.0005 przejście na wylot wymaga ~3464 iteracji. Promień
                // urywał się w środku modelu — i tylko pod kątami, gdzie droga przez bryłę jest
                // najdłuższa (widok z profilu), co wyglądało jak dziury w konkretnych ścianach.
                int maxSteps  = min((int)ceil(rayLen / _StepSize), (int)_MaxRaySteps);

                // Accumulation vessel layer (semi-transparent przed surface)
                float3 vesselColor = float3(0,0,0);
                float  vesselAlpha = 0.0;

                float3 L = normalize(_LightDir.xyz);

                // --- PRZESKAKIWANIE PUSTKI: kierunek promienia w przestrzeni UVW ---
                // uvw jest funkcją AFINICZNĄ parametru t (obrót * skala sub-regionu + przesunięcie),
                // więc pochodna uvw po t jest stała i można wyliczyć DOKŁADNĄ długość wyjścia z
                // bieżącej komórki mapy zajętości. Wcześniejsza wersja skakała o stałą długość: nie
                // pomijała bloku Z materiałem, ale potrafiła WLĄDOWAĆ głęboko w następny, zajęty blok,
                // mijając front powierzchni — stąd paski na cienkich strukturach (maseczka, blat skanera).
                float3 uvwDir = mul(rotFinal, rayDir * _SubLocalSize.xyz);
                // Zabezpieczenie przed dzieleniem przez zero dla promienia równoległego do osi,
                // z zachowaniem znaku (inaczej wyszłoby NaN zamiast +nieskończoności).
                float3 absDir  = max(abs(uvwDir), 1e-8);
                float3 safeDir = (uvwDir >= 0.0) ? absDir : -absDir;
                float3 cellUVW = max(_OccupancyCellUVW.xyz, 1e-6);
                bool   skipEnabled = _EmptySkipDensity >= 0.0;

                [loop]
                for (int step = 0; step < maxSteps; step++)
                {
                    if (t > tExit) break;

                    float3 samplePos = rayOrigin + rayDir * t;

                    // AABB bounds
                    if (any(samplePos < _MinBounds.xyz) || any(samplePos > _MaxBounds.xyz))
                        { t += _StepSize; continue; }

                    // Clip plane
                    float3 worldPos = mul(unity_ObjectToWorld, float4(samplePos,1)).xyz;
                    if (dot(_ClipPlane.xyz, worldPos) + _ClipPlane.w > 0)
                        { t += _StepSize; continue; }

                    // --- SUB-VOLUME REMAP (Piece Ownership) ---
                    // Mapuje lokalną -0.5..0.5 przestrzeń TEGO obiektu na odpowiedni podzbiór
                    // lokalnej przestrzeni oryginalnego VolumeCube — identyczność dla głównego
                    // wolumenu (_SubLocalCenter=0, _SubLocalSize=1). Patrz RaymarchCT.shader.
                    float3 origLocalPos = _SubLocalCenter.xyz + samplePos * _SubLocalSize.xyz;
                    float3 uvw      = mul(rotFinal, origLocalPos) + 0.5;

                    // --- PRZESKAKIWANIE PUSTKI (empty-space skipping) ---
                    // Pierwszy test w pętli, bo najtańszy i najczęściej trafiony: skan CT to w
                    // ogromnej większości powietrze. Jedno pobranie ze zgrubnej mapy zastępuje komplet
                    // pobrań (własność + maska + gęstość + transfer) i od razu przeskakuje CAŁY pusty
                    // blok 8^3 zamiast maszerować przez niego po _StepSize. Mapa trzyma MAKSIMUM
                    // gęstości bloku, więc pominięcie jest bezpieczne: skoro maksimum nic nie wnosi,
                    // nie wnosi też nic pojedynczy woksel w środku.
                    if (skipEnabled)
                    {
                        float blockMax = tex3Dlod(_OccupancyTex, float4(uvw, 0)).r;
                        if (blockMax < _EmptySkipDensity)
                        {
                            // Przesuwamy się DOKŁADNIE do granicy bieżącej pustej komórki, nie o stałą
                            // długość. Dzięki temu następna próbka wypada tuż ZA granicą — na samym
                            // początku kolejnego bloku — więc jeśli tam jest materiał, trafiamy w jego
                            // front, a nie w środek. To jednocześnie usuwa paski i daje większe skoki
                            // przez długie pustki niż stały krok.
                            float3 cellMin = floor(uvw / cellUVW) * cellUVW;
                            // Ternarny wybór zamiast wbudowanego step(): licznik pętli nazywa się
                            // "step" i przesłania funkcję o tej samej nazwie w całym jej ciele.
                            float3 planes  = cellMin + (uvwDir >= 0.0 ? cellUVW : float3(0, 0, 0));
                            float3 dts     = (planes - uvw) / safeDir;
                            float  dt      = min(min(dts.x, dts.y), dts.z);
                            // Minimum _StepSize gwarantuje postęp, gdy stoimy dokładnie na granicy
                            // (dt≈0) — bez tego pętla mieliłaby w miejscu aż do maxSteps.
                            t += max(dt + 1e-6, _StepSize);
                            continue;
                        }
                    }

                    // --- PIECE OWNERSHIP (wydzielone obiekty, w tym Kosz) ---
                    // Sprawdzane PRZED drogą maską morfologiczną i próbkowaniem gęstości, żeby
                    // schowane woksele kosztowały tylko 1 dodatkowy fetch, a nie całą resztę pętli.
                    {
                        float ownerVal = tex3Dlod(_OwnerTex, float4(uvw, 0)).r * 255.0;
                        if ((int)(ownerVal + 0.5) != (int)(_OwnerFilterID + 0.5))
                        {
                            t += _StepSize;
                            continue;
                        }
                    }

                    // --- MORPHOLOGY MASK ---
                    if (_MaskIDToKeep > 0.5)
                    {
                        float maskVal = tex3Dlod(_MaskTex, float4(uvw, 0)).r * 255.0;
                        int maskID = (int)(maskVal + 0.5);
                        int targetID = (int)(_MaskIDToKeep + 0.5);
                        
                        if (_MaskNegate > 0.5)
                        {
                            // Tryb ukrywania wybranej maski
                            if (maskID == targetID ||
                               (maskID > 0 && maskID == (int)(_MaskExtraHide1 + 0.5)) ||
                               (maskID > 0 && maskID == (int)(_MaskExtraHide2 + 0.5)) ||
                               (maskID > 0 && maskID == (int)(_MaskExtraHide3 + 0.5)))
                            {
                                t += _StepSize;
                                continue;
                            }
                        }
                        else
                        {
                            // Tryb zachowania wybranej maski (standardowy)
                            if (_MaskKeepBackground > 0.5)
                            {
                                // Usuwamy inne maski, tło (0) zostaje
                                if (maskID > 0 && maskID != targetID)
                                {
                                    t += _StepSize;
                                    continue;
                                }
                            }
                            else
                            {
                                // Izolacja całkowita - usuwamy wszystko oprócz wybranej maski
                                if (maskID != targetID)
                                {
                                    t += _StepSize;
                                    continue;
                                }
                            }
                        }
                    }

                    float  rawSamp  = tex3Dlod(_VolumeTex, float4(uvw, 0)).r;

                    // Omijamy remapping (ApplyWindowLevel), ponieważ Transfer Function w C# generowany jest z absolutnych wartości HU.
                    float4 tfSample = tex2Dlod(_TransferTex, float4(rawSamp, 0.5, 0, 0));

                    float sampleAlpha = tfSample.a;

                    // --------------------------------------------------
                    // SURFACE HIT – zatrzymaj się tutaj i zastosuj Phong
                    // --------------------------------------------------
                    if (sampleAlpha >= _SurfaceThreshold)
                    {
                        float  gradMag;
                        float3 normal  = GetNormalAndMag(uvw, gradMag);
                        gradMag        = saturate(gradMag * 12.0);

                        float3 shaded  = PhongShading(normal, rayDir, tfSample.rgb, gradMag);

                        // Blend warstwy naczyniowej pod powierzchnię
                        float3 finalRGB = lerp(shaded, vesselColor, vesselAlpha * _VesselAccumStrength);
                        float  finalA   = 1.0;

                        return float4(finalRGB, finalA);
                    }

                    // --------------------------------------------------
                    // PRE-SURFACE ACCUMULATION – naczynia i tkanki miękkie
                    // Akumulujemy tylko materiał poniżej progu surface
                    // żeby naczynia "przeświecały" zanim trafimy w kość/skórę
                    // --------------------------------------------------
                    if (rawSamp >= _VesselMinNorm && sampleAlpha > 0.005 && vesselAlpha < 0.92)
                    {
                        float  gradMag;
                        float3 normal  = GetNormalAndMagFast(uvw, rawSamp, gradMag);
                        float  ndotl   = max(0.0, dot(normal, L));
                        float  edgeF   = saturate(gradMag * 12.0);
                        float  shading = _AmbientStrength + _DiffuseStrength * lerp(0.5, ndotl, edgeF);

                        float3 litCol  = tfSample.rgb * shading;
                        float  a       = sampleAlpha * _StepSize * 80.0;
                        a              = saturate(a);

                        vesselColor += (1.0 - vesselAlpha) * a * litCol;
                        vesselAlpha += (1.0 - vesselAlpha) * a;
                    }

                    t += _StepSize;
                }

                // Żaden surface hit – pokaż tylko akumulację naczyniową (jeśli jest)
                if (vesselAlpha > 0.01)
                    return float4(vesselColor, vesselAlpha * _VesselAccumStrength);

                discard;
                return float4(0,0,0,0);
            }
            ENDHLSL
        }
    }
}
