Shader "Unlit/Volume/RaymarchCT"
{
    Properties
    {
        _VolumeTex      ("Volume Texture", 3D) = "" {}
        _StepSize       ("Step Size", Range(0.0005, 0.1)) = 0.0005
        
        _BoneDensity    ("Bone Density", Range(0, 100)) = 10.0
        _VesselDensity  ("Vessel Density", Range(0, 500)) = 100.0
        _VesselBoost ("Vessel Alpha Boost", Range(1, 50)) = 10.0

        // Ile sąsiadów musi być w zakresie HU żeby woksel był naczyniem (0.0-1.0)
        // 0.0 = brak filtrowania, 0.3 = min 2/6 sąsiadów, 0.5 = min 3/6 sąsiadów
        _VesselNeighborThreshold ("Vessel Neighbor Threshold", Range(0.0, 1.0)) = 0.15
        _VesselContinuity ("Vessel Continuity Boost", Range(0.0, 1.0)) = 0.5

        _WindowCenter   ("Window Center (HU)", Float) = 40
        _WindowWidth    ("Window Width (HU)", Float) = 400
        
        _HUMin          ("HU Min", Float) = -1000
        _HUMax          ("HU Max", Float) = 3000
        
        _VesselHUThresholdMin ("Vessel Min (HU)", Float) = 150.0
        _VesselHUThresholdMax ("Vessel Max (HU)", Float) = 300.0
        _VesselColorThin ("Vessel Color Thin", Color) = (0.5, 0, 0, 1)
        _VesselColorThick ("Vessel Color Thick", Color) = (1, 0.2, 0.2, 1)
        [Toggle] _VesselSoloMode ("Vessel Solo Mode", Float) = 0
        _BoneOpacity ("Bone/Tissue Opacity", Range(0, 1)) = 1.0
        
        [Header(Vessel Glow)]
        _VesselEmissive ("Vessel Emissive Glow", Range(0, 3)) = 0.6
        _VesselColorBoost ("Vessel Color Dominance", Range(1, 10)) = 2.0
        
        _BoneThreshold  ("Bone Threshold (HU)", Float) = 500
        _SoftTissueThreshold ("Soft Tissue Threshold (HU)", Float) = 20
        
        _LightDir       ("Light Direction", Vector) = (0.5, 0.7, 1.0, 0)
        _AmbientColor   ("Ambient Color", Color) = (0.1, 0.1, 0.1, 1)
        _Specular       ("Specular", Color) = (1, 1, 1, 1)
        _Shininess      ("Shininess", Range(0.1, 128)) = 32.0
        
        _ClipPlane      ("Clip Plane (World)", Vector) = (0, 1, 0, 0)
        _TransferTex    ("Transfer Function (1D)", 2D) = "black" {}
        _MinBounds      ("Min Bounds", Vector) = (-0.5, -0.5, -0.5, 0)
        _MaxBounds      ("Max Bounds", Vector) = (0.5, 0.5, 0.5, 0)
        
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
        // N = wydzielony obiekt nr N. Pozwala wydzielić fragment do osobnego GameObjectu
        // bez duplikowania _VolumeTex: każdy klon materiału filtruje po własnym _OwnerFilterID.
        // Patrz LoadDicomData.ExtractPickedIslandAsObject / VolumeObjectManager.
        _OwnerTex       ("Piece Owner Mask", 3D) = "black" {}
        _OwnerFilterID  ("Owner Filter ID (0=main volume)", Float) = 0
        // Stały, policzony raz przy wydzieleniu obiektu podzbiór lokalnej przestrzeni -0.5..0.5
        // ORYGINALNEGO VolumeCube, do którego mapuje się lokalna -0.5..0.5 przestrzeń TEGO obiektu
        // (transform-niezmiennicza — działa poprawnie niezależnie od tego, jak user przesunie/obróci
        // wydzielony kawałek, bo Unity sama liczy lokalną przestrzeń z unity_WorldToObject).
        _SubLocalCenter ("Sub-Volume Local Center", Vector) = (0,0,0,0)
        _SubLocalSize   ("Sub-Volume Local Size", Vector) = (1,1,1,0)

        [Header(Noise and Orientation)]
        _NoiseThreshold ("Noise Threshold", Range(0, 0.5)) = 0.05
        _RotationOffset ("Rotation Euler Offset (XYZ)", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler3D _VolumeTex;
            float4 _VolumeTex_TexelSize;
            float _StepSize;
            float _WindowCenter;
            float _WindowWidth;
            float _BoneDensity;
            float _VesselDensity;
            float _VesselBoost;
            float _VesselNeighborThreshold;
            float _HUMin;
            float _HUMax;
            sampler2D _TransferTex;
            float4 _VesselColorThin;
            float4 _VesselColorThick;
            float _VesselSoloMode;
            float _BoneOpacity;
            float _BoneThreshold;
            float _SoftTissueThreshold;
            float4 _ClipPlane;
            float _VesselHUThresholdMin;
            float _VesselHUThresholdMax;
            float4 _LightDir;
            float4 _AmbientColor;
            float4 _Specular;
            float _Shininess;
            float4 _MinBounds;
            float4 _MaxBounds;
            float _VesselEmissive;
            float _VesselColorBoost;
            
            sampler3D _MaskTex;
            float _MaskIDToKeep;
            float _MaskKeepBackground;
            float _MaskNegate;
            float _MaskExtraHide1;
            float _MaskExtraHide2;
            float _MaskExtraHide3;

            sampler3D _OwnerTex;
            float _OwnerFilterID;
            float4 _SubLocalCenter;
            float4 _SubLocalSize;

            struct appdata {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f {
                float4 pos    : SV_POSITION;
                float3 objPos : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float random(float2 p) {
                return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
            }

            v2f vert(appdata v) {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v); 
                UNITY_INITIALIZE_OUTPUT(v2f, o); 
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); 
                o.pos    = UnityObjectToClipPos(v.vertex);
                o.objPos = v.vertex.xyz;
                return o;
            }

            bool RayBoxIntersect(float3 ro, float3 rd, out float tMin, out float tMax)
            {
                float3 invDir = 1.0 / rd;
                float3 t0 = (-0.5 - ro) * invDir;
                float3 t1 = ( 0.5 - ro) * invDir;
                float3 tSmall = min(t0, t1);
                float3 tBig   = max(t0, t1);
                tMin = max(max(tSmall.x, tSmall.y), tSmall.z);
                tMax = min(min(tBig.x,   tBig.y),   tBig.z);
                return tMax >= max(tMin, 0.0);
            }

            float3 GetNormal(float3 uvw)
            {
                float delta = 0.01;
                float3 n;
                n.x = tex3D(_VolumeTex, uvw + float3(delta, 0, 0)).r
                    - tex3D(_VolumeTex, uvw - float3(delta, 0, 0)).r;
                n.y = tex3D(_VolumeTex, uvw + float3(0, delta, 0)).r
                    - tex3D(_VolumeTex, uvw - float3(0, delta, 0)).r;
                n.z = tex3D(_VolumeTex, uvw + float3(0, 0, delta)).r
                    - tex3D(_VolumeTex, uvw - float3(0, 0, delta)).r;
                float len = length(n);
                return (len < 0.0001) ? float3(0, 1, 0) : -n / len;
            }

            // Zamienione funkcje na zoptymalizowaną do działania na surowym ułamku 0.0-1.0
            float IsVesselRawOpt(float raw, float vMin0, float vMin1, float vMax0, float vMax1)
            {
                float lo = smoothstep(vMin0, vMin1, raw);
                float hi = 1.0 - smoothstep(vMax0, vMax1, raw);
                return lo * hi;
            }

            float _VesselContinuity;

            // Ważony filtr sąsiadów z priorytetem ścianowych (face) sąsiadów
            // Face neighbors (dystans 1 texel) mają wagę 2.0, corner (dystans √3) mają wagę 0.5
            // Dzięki temu cienkie, linearne naczynia nie są odfiltrowane
            float NeighborVesselScoreWeighted(float3 uvw, float3 delta, float vMin0, float vMin1, float vMax0, float vMax1)
            {
                float score = 0.0;
                float totalWeight = 0.0;
                
                [unroll]
                for (int x = -1; x <= 1; x++) {
                    for (int y = -1; y <= 1; y++) {
                        for (int z = -1; z <= 1; z++) {
                            if (x == 0 && y == 0 && z == 0) continue;
                            int manhattan = abs(x) + abs(y) + abs(z);
                            // Szachownicowe próbkowanie - pomijamy krawędziowe (edge)
                            if (manhattan == 2) continue; 
                            
                            // Waga: face=2.0 (manhattan=1), corner=0.5 (manhattan=3)
                            float w = (manhattan == 1) ? 2.0 : 0.5;
                            
                            float3 offset = float3(x, y, z) * delta;
                            float v = IsVesselRawOpt(tex3Dlod(_VolumeTex, float4(uvw + offset, 0)).r, vMin0, vMin1, vMax0, vMax1);
                            score += v * w;
                            totalWeight += w;
                        }
                    }
                }
                return score / totalWeight; // 6*2.0 + 8*0.5 = 16.0
            }
            
            // Multi-scale: próbkujemy też dalszych sąsiadów (2x texel) 
            // by łapać naczynia przedzielone 1-2 vokselami pustej przestrzeni
            float NeighborVesselMultiScale(float3 uvw, float3 delta, float vMin0, float vMin1, float vMax0, float vMax1)
            {
                // Skala 1: bliskie sąsiedztwo (1 texel)
                float near = NeighborVesselScoreWeighted(uvw, delta, vMin0, vMin1, vMax0, vMax1);
                
                // Skala 2: dalsze sąsiedztwo (2 texele) - tylko 6 kierunków face
                float farScore = 0.0;
                float3 delta2 = delta * 2.0;
                farScore += IsVesselRawOpt(tex3Dlod(_VolumeTex, float4(uvw + float3(delta2.x, 0, 0), 0)).r, vMin0, vMin1, vMax0, vMax1);
                farScore += IsVesselRawOpt(tex3Dlod(_VolumeTex, float4(uvw - float3(delta2.x, 0, 0), 0)).r, vMin0, vMin1, vMax0, vMax1);
                farScore += IsVesselRawOpt(tex3Dlod(_VolumeTex, float4(uvw + float3(0, delta2.y, 0), 0)).r, vMin0, vMin1, vMax0, vMax1);
                farScore += IsVesselRawOpt(tex3Dlod(_VolumeTex, float4(uvw - float3(0, delta2.y, 0), 0)).r, vMin0, vMin1, vMax0, vMax1);
                farScore += IsVesselRawOpt(tex3Dlod(_VolumeTex, float4(uvw + float3(0, 0, delta2.z), 0)).r, vMin0, vMin1, vMax0, vMax1);
                farScore += IsVesselRawOpt(tex3Dlod(_VolumeTex, float4(uvw - float3(0, 0, delta2.z), 0)).r, vMin0, vMin1, vMax0, vMax1);
                float far = farScore / 6.0;
                
                // Bierzemy max z obu skal — jeśli w dalszym otoczeniu jest naczynie, ratujemy woksel
                return max(near, lerp(near, far, 0.5));
            }

            float _NoiseThreshold;
            float4 _RotationOffset;

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // --- POPRAWKA STEREO ---
                // W trybie OpenXR SPI, _WorldSpaceCameraPos czasem nie uwzględnia IPD (rozstawu oczu)
                // Obliczamy pozycję kamery z macierzy widoku obiektu dla danego oka.
                float3 rayOrigin = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                
                // --- POPRAWKA ORIENTACJI (90 STOPNI) ---
                // Dodajemy obs?ugę macierzy rotacji, aby nie trzeba było fizycznie obracać sześcianu.
                float3 angles = _RotationOffset.xyz * 0.0174532925; // stopnie na radiany
                float3 s, c;
                sincos(angles.x, s.x, c.x);
                sincos(angles.y, s.y, c.y);
                sincos(angles.z, s.z, c.z);

                // Rotacja Y + Z (najczęstszy przypadek przesunięcia DICOM o 90 stopni)
                float3x3 rotX = float3x3(1, 0, 0, 0, c.x, -s.x, 0, s.x, c.x);
                float3x3 rotY = float3x3(c.y, 0, s.y, 0, 1, 0, -s.y, 0, c.y);
                float3x3 rotZ = float3x3(c.z, -s.z, 0, s.z, c.z, 0, 0, 0, 1);
                float3x3 rotFinal = mul(rotZ, mul(rotY, rotX));

                float3 objVertexPos = i.objPos;
                float3 rayDir    = normalize(objVertexPos - rayOrigin);

                float tEnter, tExit;
                if (!RayBoxIntersect(rayOrigin, rayDir, tEnter, tExit))
                    discard;

                tEnter = max(tEnter, 0.0);

                float4 color = float4(0, 0, 0, 0);

                float2 screenUV = i.pos.xy / _ScreenParams.xy;
                
                // --- 3D JITTERING NOISE ---
                // Używamy _Time.y dla zmienności w czasie by ukryć pasmowanie (anti-banding)
                float3 seed = float3(screenUV.x, screenUV.y, _Time.y);
                float offset = frac(sin(dot(seed, float3(12.9898, 78.233, 45.164))) * 43758.5453) * _StepSize;
                float t = tEnter + offset;

                float rayLength = tExit - tEnter;
                int maxSteps = min((int)ceil(rayLength / _StepSize), 4096);

                float3 L = normalize(_LightDir.xyz);
                float3 viewDir = -rayDir;

                // Prawdziwy fizyczny rozmiar woksela w przestrzeni UV (0.0 - 1.0)
                // Obliczany bezpośrednio ze skanów w C# (_mWidth, _mHeight, _mDepth).
                float3 neighborDelta = _VolumeTex_TexelSize.xyz;

                // PREKALKULACJA (Zmienne wyciągnięte poza pętlę na potrzeby HoloLens) -------------------------
                float rangeHU = _HUMax - _HUMin;
                float invRange = 1.0 / max(rangeHU, 1.0);
                
                // Poszerzono marginesy o -15 i +15, aby łapać lekko osłabione sygnały przerwanych naczyń 
                // do ponownego zszycia przez filtr sąsiedztwa
                float vMin0 = (_VesselHUThresholdMin - 15.0 - _HUMin) * invRange;
                float vMin1 = (_VesselHUThresholdMin - _HUMin) * invRange;
                float vMax0 = (_VesselHUThresholdMax - _HUMin) * invRange;
                float vMax1 = (_VesselHUThresholdMax + 15.0 - _HUMin) * invRange;

                float normCenter = (_WindowCenter - _HUMin) * invRange;
                float normWidth = max(_WindowWidth * invRange, 0.0001);
                float tfMinW = normCenter - normWidth * 0.5;
                float tfInvWidth = 1.0 / normWidth;
                
                float precalcBone = _BoneDensity * _StepSize;
                float precalcVessel = _VesselDensity * _VesselBoost * _StepSize;
                
                float3 rayOriginWorld = mul(unity_ObjectToWorld, float4(rayOrigin, 1)).xyz;
                float3 rayDirWorld = mul(unity_ObjectToWorld, float4(rayDir, 0)).xyz;
                // --------------------------------------------------------------------------------------------

                [loop]
                for (int step = 0; step < maxSteps; step++)
                {
                    if (t > tExit || color.a > 0.98)
                        break;

                    float3 samplePos = rayOrigin + rayDir * t;

                    // --- SUB-VOLUME REMAP (Piece Ownership) ---
                    // Mapuje lokalną -0.5..0.5 przestrzeń TEGO obiektu z powrotem na odpowiedni
                    // podzbiór lokalnej przestrzeni oryginalnego VolumeCube — identyczność dla
                    // głównego wolumenu (_SubLocalCenter=0, _SubLocalSize=1), więc zachowanie
                    // bez zmian dopóki obiekt nie jest wydzielonym kawałkiem.
                    float3 origLocalPos = _SubLocalCenter.xyz + samplePos * _SubLocalSize.xyz;

                    // --- ORIENTACJA ---
                    // Aplikujemy rotację "wyprostowania" na punkcie próbkowania
                    float3 rotatedPos = mul((float3x3)rotFinal, origLocalPos);

                    if (any(samplePos < _MinBounds.xyz) || any(samplePos > _MaxBounds.xyz))
                    {
                        t += _StepSize;
                        continue;
                    }
                    
                    // --- CLIPPING PLANE ---
                    // Obliczenia wektorowe zamiast ciężkiego mnożenia macierzy:
                    float3 worldPos  = mul(unity_ObjectToWorld, float4(samplePos, 1.0)).xyz;
                    float  planeDist = dot(_ClipPlane.xyz, worldPos) + _ClipPlane.w;
                    if (planeDist > 0)
                    {
                        t += _StepSize;
                        continue;
                    }
                    
                    float3 uvw       = rotatedPos + 0.5;

                    // --- PIECE OWNERSHIP (wydzielone obiekty, w tym Kosz) ---
                    // Sprawdzane PRZED drogą maską morfologiczną i próbkowaniem gęstości, żeby
                    // schowane woksele kosztowały tylko 1 dodatkowy fetch, a nie całą resztę pętli.
                    // Każdy woksel "należy" do dokładnie jednego obiektu (0 = główny wolumen).
                    // Ten materiał pokazuje WYŁĄCZNIE woksele o pasującym właścicielu — dzięki temu
                    // wydzielenie kawałka nie wymaga duplikowania _VolumeTex, tylko innej wartości
                    // _OwnerFilterID na klonie materiału. Patrz LoadDicomData.ExtractPickedIslandAsObject.
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

                    float  rawSample = tex3D(_VolumeTex, uvw).r;
                    
                    // Wycinanie szumów o niskiej gęstości
                    if (rawSample < _NoiseThreshold)
                    {
                        t += _StepSize;
                        continue;
                    }

                    // ── DETEKCJA NACZYŃ (Zoptymalizowana przestrzenią 0-1) ──────────
                    float vesselIntensity = IsVesselRawOpt(rawSample, vMin0, vMin1, vMax0, vMax1);
                    float3 vesselColorCurrent = float3(1, 0, 0); // Default czerwony

                    // ── FILTR SĄSIADÓW 3D (Multi-Scale Weighted Connectivity) ─────
                    // Próg wejściowy 0.01 — łapiemy nawet "ledwo widoczne" fragmenty naczyń
                    if (vesselIntensity > 0.01)
                    {
                        float neighborScore = NeighborVesselMultiScale(uvw, neighborDelta, vMin0, vMin1, vMax0, vMax1);
                        
                        // Vessel Thickness Coloring (na podstawie gęstości otoczenia)
                        vesselColorCurrent = lerp(_VesselColorThin.rgb, _VesselColorThick.rgb, smoothstep(0.15, 0.7, neighborScore));

                        // DILACJA (Zszywanie): obniżony próg 0.2 (było 0.35) —
                        // ratujemy cienkie naczynia na prawej stronie czaszki które miały za mało sąsiadów
                        if (neighborScore > 0.2)
                        {
                            vesselIntensity = max(vesselIntensity, smoothstep(0.2, 0.6, neighborScore));
                        }

                        // Wzmacniamy naczynie jeśli ma sąsiadów (Connectivity)
                        vesselIntensity *= lerp(1.0, 1.0 + _VesselContinuity * 2.0, neighborScore);
                        
                        // Wyciszamy izolowane kropki szumu łagodnym przejściem
                        vesselIntensity *= smoothstep(_VesselNeighborThreshold - 0.05,
                                                      _VesselNeighborThreshold + 0.15,
                                                      neighborScore);
                        
                        // ── FILTR KOŚĆ vs NACZYNIE (Band-Pass) ──────────────────
                        // Naczynia = cienkie struktury (neighborScore 0.15-0.55)
                        // Kość/tkanka = gęsta masa (neighborScore > 0.6)
                        // W Solo Mode: agresywnie tłumimy gęste masy żeby pokazać TYLKO naczynia
                        // W normalnym trybie: lekkie tłumienie żeby nie mieszać koloru naczyń z kością
                        float boneSuppress = 1.0 - smoothstep(0.5, 0.8, neighborScore);
                        vesselIntensity *= lerp(1.0, boneSuppress, _VesselSoloMode * 0.9 + 0.1);
                    }

                    // ── ALPHA ────────────────────────────────────────────────────
                    float intensity  = saturate((rawSample - tfMinW) * tfInvWidth);
                    float4 tfSample  = tex2Dlod(_TransferTex, float4(intensity, 0.5, 0, 0));

                    // Hack na oddzielenie kości i naczyń: boneAlpha uwzględnia wycięcie w miejscu naczyń, 
                    // oraz globalny modyfikator krycia kości. W poleceniu Solo Mode kość zostaje zredukowana.
                    float currentBoneOpacity = lerp(_BoneOpacity, 0.0, _VesselSoloMode);
                    float boneAlpha = tfSample.a * precalcBone * currentBoneOpacity * (1.0 - saturate(vesselIntensity));
                    float vAlpha    = vesselIntensity * precalcVessel;
                    float alpha     = boneAlpha + vAlpha;

                    if (alpha > 0.001)
                    {
                        float3 normal     = GetNormal(uvw);
                        float  gradMag    = length(normal); // siła gradientu (krawędzie = wysoka)
                        float  ndotl      = saturate(dot(normal, L));
                        float3 halfDir    = normalize(L + viewDir);
                        float  ndoth      = saturate(dot(normal, halfDir));
                        float  specFactor = pow(ndoth, max(1.0, _Shininess));
                        float3 specular   = specFactor * _Specular.rgb;
                        float3 lighting   = _AmbientColor.rgb + ndotl;

                        // Wzmocniona dominacja koloru naczyń — _VesselColorBoost podnosi wagę
                        float rawVesselWeight   = saturate(vAlpha / (alpha + 1e-5));
                        float vesselWeight      = saturate(pow(rawVesselWeight, 1.0 / _VesselColorBoost));
                        
                        // ── WZMOCNIENIE KRAWĘDZI NACZYŃ (Gradient Edge Glow) ────
                        // Krawędzie naczyń mają silny gradient (przejście gęstość→pustka)
                        // Używamy gradMag do podświetlenia rozgałęzień
                        float edgeGlow = smoothstep(0.02, 0.15, gradMag) * vesselWeight;
                        
                        float3 baseColor        = lerp(tfSample.rgb, vesselColorCurrent, vesselWeight);
                        
                        // EMISYJNA POŚWIATA — cubic falloff + edge boost
                        float emissiveStrength   = vesselWeight * vesselWeight * vesselWeight;
                        // Krawędzie naczyń dostają dodatkowy boost emisji
                        emissiveStrength = max(emissiveStrength, edgeGlow * edgeGlow);
                        float3 emissive          = vesselColorCurrent * emissiveStrength * _VesselEmissive;
                        
                        // Edge highlight: jasna obwódka na krawędziach naczyń
                        float3 edgeHighlight     = vesselColorCurrent * edgeGlow * _VesselEmissive * 0.5;
                        
                        float3 finalSampleColor  = baseColor * lighting + specular + emissive + edgeHighlight;

                        color.rgb += (1.0 - color.a) * alpha * finalSampleColor;
                        color.a   += (1.0 - color.a) * alpha;
                    }

                    t += _StepSize;
                }

                return color;
            }

            ENDHLSL
        }
    }
}
