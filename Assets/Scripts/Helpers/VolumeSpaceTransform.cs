using UnityEngine;

namespace Helpers
{
    public static class VolumeSpaceTransform
    {
        /// <summary>
        /// Konwertuje pozycję lokalną wewnątrz VolumeCube (od -0.5 do 0.5) na koordynaty UVW tekstury (0.0 do 1.0)
        /// z uwzględnieniem rotacji HLSL z shadera RaymarchCT.
        /// UWAGA: Ponieważ shader rzutuje promień (World->Local) i obraca próbkę, aby odczytać teksturę, 
        /// my również aplikujemy DOKŁADNIE taką samą rotację w tę samą stronę podczas naszego raymarchingu.
        /// Zmiana kolejności mnożenia w shaderze musi być odzwierciedlona tutaj!
        /// </summary>
        private static Matrix4x4 BuildRotFinal(Vector3 rotationOffsetDegrees)
        {
            Vector3 angles = rotationOffsetDegrees * Mathf.Deg2Rad;
            float sx = Mathf.Sin(angles.x), cx = Mathf.Cos(angles.x);
            float sy = Mathf.Sin(angles.y), cy = Mathf.Cos(angles.y);
            float sz = Mathf.Sin(angles.z), cz = Mathf.Cos(angles.z);

            Matrix4x4 rotX = new Matrix4x4();
            rotX.SetRow(0, new Vector4(1, 0, 0, 0));
            rotX.SetRow(1, new Vector4(0, cx, -sx, 0));
            rotX.SetRow(2, new Vector4(0, sx, cx, 0));
            rotX.SetRow(3, new Vector4(0, 0, 0, 1));

            Matrix4x4 rotY = new Matrix4x4();
            rotY.SetRow(0, new Vector4(cy, 0, sy, 0));
            rotY.SetRow(1, new Vector4(0, 1, 0, 0));
            rotY.SetRow(2, new Vector4(-sy, 0, cy, 0));
            rotY.SetRow(3, new Vector4(0, 0, 0, 1));

            Matrix4x4 rotZ = new Matrix4x4();
            rotZ.SetRow(0, new Vector4(cz, -sz, 0, 0));
            rotZ.SetRow(1, new Vector4(sz, cz, 0, 0));
            rotZ.SetRow(2, new Vector4(0, 0, 1, 0));
            rotZ.SetRow(3, new Vector4(0, 0, 0, 1));

            return rotZ * rotY * rotX;
        }

        public static Vector3 LocalToUVW(Vector3 localPos, Vector3 rotationOffsetDegrees)
        {
            Matrix4x4 rotFinal = BuildRotFinal(rotationOffsetDegrees);
            Vector3 rotatedPos = rotFinal.MultiplyPoint3x4(localPos);
            return rotatedPos + new Vector3(0.5f, 0.5f, 0.5f);
        }

        /// <summary>
        /// Odwrotność LocalToUVW — z UVW tekstury (0..1) z powrotem na pozycję lokalną wewnątrz
        /// VolumeCube (-0.5..0.5). rotFinal jest macierzą czysto rotacyjną (ortogonalną), więc
        /// jej odwrotność jest bezpieczna/dokładna (Matrix4x4.inverse, nie ręczna transpozycja,
        /// żeby nie polegać po cichu na tym założeniu, gdyby BuildRotFinal kiedyś się zmieniło).
        /// Używane przy wydzielaniu fragmentu jako osobnego obiektu — trzeba policzyć AABB
        /// wyciętych wokseli z powrotem w lokalnej przestrzeni oryginalnego VolumeCube.
        /// </summary>
        public static Vector3 UvwToLocal(Vector3 uvw, Vector3 rotationOffsetDegrees)
        {
            Matrix4x4 rotFinal = BuildRotFinal(rotationOffsetDegrees);
            Vector3 rotatedPos = uvw - new Vector3(0.5f, 0.5f, 0.5f);
            return rotFinal.inverse.MultiplyPoint3x4(rotatedPos);
        }

        /// <summary>
        /// Mapuje pozycję lokalną (-0.5..0.5) NA WYDZIELONYM OBIEKCIE z powrotem na odpowiadającą
        /// pozycję lokalną w ORYGINALNYM VolumeCube — DOKŁADNIE ten sam wzór co "SUB-VOLUME REMAP"
        /// w RaymarchCT.shader/RaymarchCT_Simplified.shader (musi być trzymany w synchronizacji
        /// ręcznie, jak reszta CPU-GPU picking mathu w tej klasie). subLocalCenter/subLocalSize są
        /// stałe, policzone raz przy wydzieleniu (patrz LoadDicomData.ExtractPickedIslandAsObject)
        /// — działa poprawnie niezależnie od tego, jak user później przesunie/obróci/przeskaluje
        /// wydzielony obiekt, bo ta pozycja lokalna jest transform-niezmiennicza.
        /// </summary>
        public static Vector3 SubLocalToOriginalLocal(Vector3 subLocalPos, Vector3 subLocalCenter, Vector3 subLocalSize)
        {
            return subLocalCenter + Vector3.Scale(subLocalPos, subLocalSize);
        }

        /// <summary>
        /// Odwrotność SubLocalToOriginalLocal — z pozycji w lokalnej przestrzeni ORYGINALNEGO
        /// VolumeCube z powrotem na pozycję lokalną (-0.5..0.5) NA WYDZIELONYM OBIEKCIE opisanym
        /// przez subLocalCenter/subLocalSize. Potrzebne przy DALSZYM dzieleniu JUŻ wydzielonego
        /// (i możliwie przesuniętego/obróconego) obiektu — nowy pod-kawałek trzeba umieścić w
        /// świecie względem TRANSFORMU RODZICA (source targetu), nie zawsze względem głównego
        /// volumeCube, patrz LoadDicomData.FinalizeExtractionAsync.
        /// </summary>
        public static Vector3 OriginalLocalToSubLocal(Vector3 originalLocalPos, Vector3 subLocalCenter, Vector3 subLocalSize)
        {
            Vector3 d = originalLocalPos - subLocalCenter;
            return new Vector3(
                Mathf.Abs(subLocalSize.x) > 1e-6f ? d.x / subLocalSize.x : 0f,
                Mathf.Abs(subLocalSize.y) > 1e-6f ? d.y / subLocalSize.y : 0f,
                Mathf.Abs(subLocalSize.z) > 1e-6f ? d.z / subLocalSize.z : 0f);
        }

        public static Vector3Int UvwToVoxelIndex(Vector3 uvw, int width, int height, int depth)
        {
            int px = Mathf.Clamp(Mathf.FloorToInt(uvw.x * width), 0, width - 1);
            int py = Mathf.Clamp(Mathf.FloorToInt(uvw.y * height), 0, height - 1);
            int pz = Mathf.Clamp(Mathf.FloorToInt(uvw.z * depth), 0, depth - 1);
            return new Vector3Int(px, py, pz);
        }

        public static int GetFlatIndex(Vector3Int voxel, int width, int height)
        {
            return voxel.z * width * height + voxel.y * width + voxel.x;
        }
    }
}
