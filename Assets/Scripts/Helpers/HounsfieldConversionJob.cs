using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Helpers
{
    [BurstCompile]
    public struct HounsfieldConversionJob : IJobParallelFor
    {
        // Parametry konwersji pobrane z metadanych DICOM (Rescale Slope/Intercept)
        [ReadOnly] public float slope;
        [ReadOnly] public float intercept;

        // Surowe bajty prosto z pliku DICOM
        [ReadOnly] public NativeArray<byte> rawDicomBytes;

        // Metadane formatu obrazu potrzebne do poprawnego odczytu bajtów
        [ReadOnly] public int bitsStored;
        [ReadOnly] public bool isSigned;

        [WriteOnly] public NativeArray<int> output;

        public void Execute(int index)
        {
            int rawPixel;

            if (bitsStored <= 8)
            {
                // Przypadek prosty: 1 piksel = 1 bajt
                rawPixel = rawDicomBytes[index];
            }
            else
            {
                // Przypadek medyczny: 1 piksel = 2 bajty (Little Endian)
                // Każdy piksel 'index' zaczyna się pod adresem index * 2
                int byteOffset = index * 2;
                
                // Sklejanie dwóch 8-bitowych bajtów w jedną 16-bitową liczbę:
                // rawDicomBytes[byteOffset]        - młodszy bajt (Low Byte)
                // rawDicomBytes[byteOffset + 1]    - starszy bajt (High Byte), przesuwany o 8 bitów w lewo (<< 8)
                // Operator '|' (OR) łączy je bitowo w jedną strukturę.
                if (isSigned)
                {
                    short signedPixelValue = (short)(rawDicomBytes[byteOffset] | (rawDicomBytes[byteOffset + 1] << 8));
                    rawPixel = signedPixelValue;
                }
                else
                {
                    ushort unsignedPixelValue = (ushort)(rawDicomBytes[byteOffset] | (rawDicomBytes[byteOffset + 1] << 8));
                    rawPixel = unsignedPixelValue;
                }
            }

            // Finalna formuła HU: Wartość_Piksela * Nachylenie + Przesunięcie
            output[index] = (int)(rawPixel * slope + intercept);
        }
    }
}