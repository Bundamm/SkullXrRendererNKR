using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace SkullXrRendererNKR.App
{
    /// <summary>
    /// Jeden zapisany zestaw wartości pod własną nazwą. Wartości są wyłącznie liczbowe —
    /// interpretację (która pozycja to co) narzuca kategoria magazynu, patrz PresetStore.
    /// </summary>
    public readonly struct Preset
    {
        public readonly string Name;
        public readonly float[] Values;

        public Preset(string name, float[] values)
        {
            Name = name;
            Values = values;
        }

        /// <summary>Wartość spod indeksu albo zastępcza, gdy preset zapisano w starszym, krótszym układzie.</summary>
        public float Get(int index, float fallback) =>
            Values != null && index >= 0 && index < Values.Length ? Values[index] : fallback;
    }

    /// <summary>
    /// Magazyn presetów jednej kategorii, trzymany w PlayerPrefs — tak samo jak lista ostatnio
    /// otwieranych badań w ScanLibrary, żeby nie mnożyć sposobów zapisywania drobnych ustawień.
    ///
    /// Świadomie bez JSON-a: preset to nazwa i kilka liczb, a PlayerPrefs i tak przechowuje tekst.
    /// Format jest na tyle prosty, że da się go przeczytać i poprawić gołym okiem w rejestrze,
    /// gdyby kiedyś zaszła taka potrzeba.
    ///
    /// Nazwa jest kluczem: zapis pod istniejącą nazwą nadpisuje wpis. Dzięki temu „popraw preset"
    /// nie wymaga osobnej operacji — wystarczy zapisać ponownie tak samo nazwany.
    /// </summary>
    public class PresetStore
    {
        private const string KeyPrefix = "SkullXr.Presets.";
        private const char EntrySeparator = '\n';   // nie wystąpi w nazwie wpisywanej w polu tekstowym
        private const char NameSeparator = '|';
        private const char ValueSeparator = ';';

        private readonly string _key;
        private List<Preset> _cache;

        /// <summary>Zmienił się skład listy — interfejs ma się przebudować.</summary>
        public event Action OnChanged;

        public PresetStore(string category)
        {
            _key = KeyPrefix + category;
        }

        public IReadOnlyList<Preset> All
        {
            get
            {
                if (_cache != null) return _cache;

                _cache = new List<Preset>();
                string raw = PlayerPrefs.GetString(_key, string.Empty);
                if (string.IsNullOrEmpty(raw)) return _cache;

                foreach (string entry in raw.Split(EntrySeparator))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    int split = entry.IndexOf(NameSeparator);
                    if (split <= 0) continue;

                    string name = entry.Substring(0, split);
                    string[] parts = entry.Substring(split + 1).Split(ValueSeparator);

                    var values = new float[parts.Length];
                    bool ok = true;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        // InvariantCulture w obie strony: bez tego preset zapisany na maszynie
                        // z przecinkiem dziesiętnym nie odczyta się tam, gdzie separatorem jest kropka.
                        if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                        {
                            ok = false;
                            break;
                        }
                    }

                    if (ok) _cache.Add(new Preset(name, values));
                }

                return _cache;
            }
        }

        public bool TryGet(string name, out Preset preset)
        {
            foreach (var p in All)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    preset = p;
                    return true;
                }
            }
            preset = default;
            return false;
        }

        /// <summary>
        /// Zapisuje preset pod podaną nazwą, nadpisując wpis o tej samej nazwie. Nazwa jest
        /// oczyszczana ze znaków rozdzielających, bo trafia do tego samego ciągu co dane —
        /// niedopilnowanie tego rozbiłoby cały magazyn przy pierwszej nazwie ze średnikiem.
        /// </summary>
        public void Save(string name, params float[] values)
        {
            name = Sanitize(name);
            if (string.IsNullOrEmpty(name) || values == null || values.Length == 0) return;

            var list = All.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(new Preset(name, values));

            Persist(list);
        }

        public void Delete(string name)
        {
            var list = All.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == All.Count) return;

            Persist(list);
        }

        public void Clear()
        {
            if (All.Count == 0) return;
            Persist(new List<Preset>());
        }

        private void Persist(List<Preset> list)
        {
            var entries = list.Select(p =>
                p.Name + NameSeparator +
                string.Join(ValueSeparator.ToString(),
                            p.Values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));

            PlayerPrefs.SetString(_key, string.Join(EntrySeparator.ToString(), entries));
            PlayerPrefs.Save();

            _cache = list;
            OnChanged?.Invoke();
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return name.Trim()
                       .Replace(EntrySeparator, ' ')
                       .Replace(NameSeparator, ' ')
                       .Replace(ValueSeparator, ' ');
        }
    }
}
