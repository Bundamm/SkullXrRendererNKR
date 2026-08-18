using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif

namespace SkullXrRendererNKR.Platform
{
    /// <summary>
    /// Systemowe okno wyboru folderu (to samo, które pokazuje Eksplorator Windows), używane na ekranie
    /// startowym do wskazania folderu z serią DICOM.
    ///
    /// Dwie rzeczy, które trzeba tu wiedzieć:
    ///
    /// 1. Dialog jest MODALNY i blokuje wątek, na którym działa. Wywołany wprost z wątku Unity
    ///    zamroziłby renderowanie — a że sesja XR idzie tu przez Holographic Remoting z tego samego
    ///    komputera, oznaczałoby to zamrożony obraz w goglach na cały czas przeglądania dysku.
    ///    Dlatego okno pokazujemy na własnym wątku (COM wymaga apartamentu STA), a wątek Unity tylko
    ///    czeka na wynik przez UniTask.WaitUntil — czyli normalnie renderuje kolejne klatki.
    ///
    /// 2. Działa WYŁĄCZNIE w buildzie desktopowym i w Edytorze na Windows. W aplikacji uruchomionej
    ///    natywnie na HoloLens nie ma czego pokazać — dlatego jest IsSupported, a interfejs musi mieć
    ///    drogę zapasową (pole na wpisanie ścieżki, patrz ekran startowy).
    /// </summary>
    public static class WindowsFolderPicker
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        public static bool IsSupported => true;
#else
        public static bool IsSupported => false;
#endif

        /// <summary>
        /// Pokazuje okno wyboru folderu i zwraca wybraną ścieżkę, albo null gdy użytkownik anulował
        /// (lub gdy platforma tego nie wspiera). Nie blokuje wątku Unity.
        /// </summary>
        public static async UniTask<string> PickFolderAsync(string title = "Wskaż folder z serią DICOM",
                                                            string initialFolder = null)
        {
            if (!IsSupported)
            {
                Debug.LogWarning("[WindowsFolderPicker] Systemowe okno wyboru folderu nie jest dostępne na tej platformie.");
                return null;
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string result = null;
            bool finished = false;

            var thread = new Thread(() =>
            {
                try
                {
                    result = ShowDialogBlocking(title, initialFolder);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[WindowsFolderPicker] Nie udało się otworzyć okna wyboru folderu: {ex.Message}");
                    result = null;
                }
                finally
                {
                    Volatile.Write(ref finished, true);
                }
            });

            // STA jest wymagane przez COM-owe okna powłoki Windows; wątek w tle, żeby nie trzymał
            // procesu przy zamykaniu aplikacji z otwartym oknem.
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            // Czekanie ODBYWA SIĘ na wątku Unity (PlayerLoop), więc kontynuacja wraca na wątek główny
            // i wolno z niej dotykać obiektów sceny bez dodatkowego przełączania.
            await UniTask.WaitUntil(() => Volatile.Read(ref finished));
            return result;
#else
            await UniTask.CompletedTask;
            return null;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // --- Minimalne wiązania COM do IFileOpenDialog (Windows Vista+) ---------------------------
        // Interfejsy COM wymagają zadeklarowania WSZYSTKICH metod w kolejności tablicy wirtualnej,
        // także tych, których nie używamy — stąd metody-zaślepki bez argumentów. Nigdy ich nie wołamy;
        // istnieją wyłącznie po to, żeby te używane wypadły pod właściwym indeksem.

        private const uint FOS_PICKFOLDERS     = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST   = 0x00000800;
        private const uint SIGDN_FILESYSPATH   = 0x80058000;
        private const int  S_OK                = 0;

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCoClass { }

        // IID_IFileOpenDialog (NIE IFileDialog — ten interfejs deklaruje też GetResults/GetSelectedItems).
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr parent);
            // IFileDialog
            void SetFileTypes();
            void SetFileTypeIndex();
            void GetFileTypeIndex();
            void Advise();
            void Unadvise();
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder();
            void GetCurrentSelection();
            void SetFileName();
            void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel();
            void SetFileNameLabel();
            void GetResult(out IShellItem ppsi);
            void AddPlace();
            void SetDefaultExtension();
            void Close();
            void SetClientGuid();
            void ClearClientData();
            void SetFilter();
            // IFileOpenDialog
            void GetResults();
            void GetSelectedItems();
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler();
            void GetParent();
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes();
            void Compare();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

        private static string ShowDialogBlocking(string title, string initialFolder)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
            try
            {
                dialog.GetOptions(out uint options);
                // FORCEFILESYSTEM odrzuca lokalizacje wirtualne (biblioteki, "Ten komputer"), z których
                // i tak nie dałoby się odczytać plików zwykłym Directory.GetFiles.
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder))
                {
                    // Otwarcie od ostatnio używanego folderu — przy kolejnych skanach tego samego
                    // pacjenta oszczędza całą wędrówkę przez drzewo katalogów.
                    var shellItemGuid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                    try
                    {
                        SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref shellItemGuid, out IShellItem startItem);
                        if (startItem != null) dialog.SetFolder(startItem);
                    }
                    catch
                    {
                        // Nieosiągalna ścieżka startowa to nie powód, żeby nie pokazać okna w ogóle.
                    }
                }

                if (dialog.Show(IntPtr.Zero) != S_OK) return null; // anulowane przez użytkownika

                dialog.GetResult(out IShellItem item);
                if (item == null) return null;

                item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
#endif
    }
}
