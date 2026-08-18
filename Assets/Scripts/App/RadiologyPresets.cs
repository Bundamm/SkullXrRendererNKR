using UnityEngine;

namespace SkullXrRendererNKR.App
{
    /// <summary>
    /// Nastawy okna gęstości (WC/WW) — dokładnie te same, które radiolog zna z konsoli skanera i
    /// z każdej przeglądarki DICOM. Podanie ich jako gotowych przycisków zastępuje ustawianie dwóch
    /// suwaków na wyczucie: „okno kostne” to konkretna, powszechnie znana para liczb, a nie coś, co
    /// trzeba wymacać.
    /// </summary>
    public readonly struct WindowPreset
    {
        public readonly string Name;
        public readonly float CenterHU;
        public readonly float WidthHU;
        public readonly string Description;

        public WindowPreset(string name, float centerHU, float widthHU, string description)
        {
            Name = name; CenterHU = centerHU; WidthHU = widthHU; Description = description;
        }

        public bool Matches(float center, float width) =>
            Mathf.Abs(center - CenterHU) < 0.5f && Mathf.Abs(width - WidthHU) < 0.5f;
    }

    public static class RadiologyPresets
    {
        /// <summary>
        /// Zestaw ograniczony do neurochirurgii — aplikacja powstaje pod konkretne zamówienie, więc
        /// okna spoza tego zakresu (płucne, naczyniowe) tylko zabierałyby miejsce w wierszu i kazały
        /// przebiegać wzrokiem po nastawach, których nikt tu nie użyje.
        ///
        /// „Pełny” jest pierwszy i jest stanem wyjściowym: obejmuje całą skalę Hounsfielda, więc nic
        /// nie wygasza i model wygląda tak, jak przed wprowadzeniem okien. Bez tego nie dałoby się
        /// wrócić do widoku „pokaż wszystko” inaczej niż ręcznym rozsuwaniem suwaków.
        /// </summary>
        public static readonly WindowPreset[] Windows =
        {
            new WindowPreset("Pełny",   1000f, 6000f, "Bez wygaszania — widoczny cały zakres gęstości."),
            new WindowPreset("Kość",     400f, 1800f, "Okno kostne — struktury kostne i zwapnienia."),
            new WindowPreset("Tk. miękkie", 40f,  400f, "Okno tkanek miękkich — narządy i mięśnie."),
            new WindowPreset("Mózg",      40f,   80f, "Okno mózgowe — wąskie, uwidacznia różnicę istoty białej i szarej.")
        };

        /// <summary>Nastawa startowa — patrz komentarz przy Windows.</summary>
        public static WindowPreset FullRange => Windows[0];

        public static int IndexOfWindow(float centerHU, float widthHU)
        {
            for (int i = 0; i < Windows.Length; i++)
                if (Windows[i].Matches(centerHU, widthHU)) return i;
            return -1; // ustawienie własne, spoza listy
        }
    }
}
