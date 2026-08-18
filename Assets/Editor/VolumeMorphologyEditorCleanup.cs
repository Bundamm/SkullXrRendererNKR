using Helpers;
using UnityEditor;

/// <summary>
/// Siatka bezpieczeństwa (poza LoadDicomData.OnDestroy) na wyciek natywnej pamięci bufory statyczne
/// VolumeMorphology (NativeArray z Allocator.Persistent) NIE są sprzątane przez GC. Gdyby OnDestroy z
/// jakiegoś powodu nie zdążył zadziałać (np. wyłączone "Reload Domain" w Enter Play Mode Settings, albo
/// obiekt sceny usunięty/refaktoryzowany bez tego hooka), ten hook i tak zwolni bufory przy każdym
/// przeładowaniu assembly (co obejmuje zarówno wyjście z Play Mode, jak i rekompilację skryptów).
/// </summary>
[InitializeOnLoad]
public static class VolumeMorphologyEditorCleanup
{
    static VolumeMorphologyEditorCleanup()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
    }

    private static void OnBeforeAssemblyReload()
    {
        VolumeMorphology.DisposeStaticBuffers();
    }
}
