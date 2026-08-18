using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Historia zmian własności wokseli, pozwalająca cofnąć OSTATNIĄ operację edycyjną zamiast
    /// wyłącznie wszystkich naraz („Cofnij wszystkie cięcia”). Bez tego jedno nieuważne pociągnięcie
    /// pędzlem po godzinie pracy kosztuje całą tę godzinę.
    ///
    /// Zapisujemy wyłącznie to, co faktycznie się zmieniło — parę (indeks woksela, poprzedni
    /// właściciel). Cofnięcie to przepisanie tych par z powrotem; nie trzeba do tego ani kopii
    /// wolumenu, ani powtarzania segmentacji.
    ///
    /// Świadome ograniczenie zakresu: historia obejmuje operacje o ograniczonym zasięgu — pędzel,
    /// tunel, usunięcie i wydzielenie wskazanej struktury. NIE obejmuje operacji masowych
    /// (usuwanie sprzętu skanera, zamiatanie drobin), bo te zmieniają dziesiątki milionów wokseli
    /// naraz: zapamiętanie ich kosztowałoby setki megabajtów na jedną operację, a i tak są odwracalne
    /// przez kosz i przez cofnięcie wszystkich cięć. Krok, który przekroczy limit, jest odrzucany
    /// RAZEM z całą wcześniejszą historią — cofanie „co drugiej” operacji dałoby stan, którego
    /// użytkownik nigdy nie widział.
    ///
    /// Wątki: zapis idzie także z wątków roboczych (pętle w LoadDicomData), ale zawsze z JEDNEGO
    /// naraz — operacje edycyjne są wzajemnie wykluczane (patrz VolumeSession.RunExclusiveAsync
    /// i anulowanie poprzedniej operacji Pickera). Dlatego Record celowo nie zakłada blokady:
    /// przy milionach wywołań na operację jej koszt byłby wyraźnie odczuwalny.
    /// </summary>
    public class VolumeEditHistory
    {
        /// <summary>
        /// Ile wokseli może zmienić pojedyncza operacja, żeby dało się ją cofnąć. 2 mln to ok. 10 MB
        /// zapisu — z zapasem starcza na najszersze pociągnięcie pędzlem i na wydzielenie dużej
        /// struktury, a odcina operacje działające na całym wolumenie.
        /// </summary>
        public int MaxVoxelsPerStep = 2_000_000;

        /// <summary>Ile operacji wstecz da się cofnąć. Starsze wypadają z historii.</summary>
        public int MaxSteps = 12;

        private class Step
        {
            public string Label;
            public readonly List<int> Indices = new List<int>();
            public readonly List<byte> PreviousOwners = new List<byte>();
        }

        private readonly List<Step> _steps = new List<Step>();
        private Step _current;
        private bool _currentOverflowed;

        /// <summary>Zmieniła się zawartość historii — UI ma odświeżyć przycisk cofania.</summary>
        public event Action OnChanged;

        public bool CanUndo => _steps.Count > 0;
        public int StepCount => _steps.Count;
        public string NextUndoLabel => _steps.Count > 0 ? _steps[_steps.Count - 1].Label : null;

        /// <summary>
        /// Otwiera nowy krok. Wołanie tego bez późniejszego Commit nie psuje stanu — kolejny Begin
        /// porzuca niedokończony krok (operacja przerwana w połowie i tak nie zmieniła nic, co
        /// dałoby się sensownie cofnąć).
        /// </summary>
        public void Begin(string label)
        {
            _current = new Step { Label = label };
            _currentOverflowed = false;
        }

        /// <summary>
        /// Zapamiętuje pojedynczą zmianę. Wołane TUŻ PRZED nadpisaniem woksela, z jego dotychczasowym
        /// właścicielem. Poza otwartym krokiem nic nie robi, więc wpięcie w kod edycji jest bezpieczne
        /// nawet tam, gdzie historia akurat nie jest prowadzona.
        /// </summary>
        public void Record(int index, byte previousOwner)
        {
            if (_current == null || _currentOverflowed) return;

            if (_current.Indices.Count >= MaxVoxelsPerStep)
            {
                _currentOverflowed = true;
                return;
            }

            _current.Indices.Add(index);
            _current.PreviousOwners.Add(previousOwner);
        }

        /// <summary>
        /// Zamyka krok i dokłada go do historii. Krok pusty jest pomijany (operacja niczego nie
        /// zmieniła), a krok przepełniony czyści historię — patrz opis klasy.
        /// </summary>
        public void Commit()
        {
            if (_current == null) return;

            var step = _current;
            _current = null;

            if (_currentOverflowed)
            {
                _currentOverflowed = false;
                if (_steps.Count > 0)
                {
                    _steps.Clear();
                    OnChanged?.Invoke();
                }
                Debug.Log($"[VolumeEditHistory] Operacja „{step.Label}” zmieniła zbyt wiele wokseli, " +
                          "żeby dało się ją cofnąć pojedynczo — historia wyczyszczona. " +
                          "Cały stan nadal cofa „Cofnij wszystkie cięcia”.");
                return;
            }

            if (step.Indices.Count == 0) return;

            _steps.Add(step);
            while (_steps.Count > MaxSteps) _steps.RemoveAt(0);

            OnChanged?.Invoke();
        }

        /// <summary>Porzuca otwarty krok — operacja została anulowana albo nie doszła do skutku.</summary>
        public void Abort()
        {
            _current = null;
            _currentOverflowed = false;
        }

        /// <summary>
        /// Cofa ostatnią zapamiętaną operację, przepisując zachowanych właścicieli z powrotem do
        /// maski. Zwraca false, gdy nie ma czego cofać. Wywołujący MUSI potem zsynchronizować maskę
        /// z GPU i przeliczyć segmentację — sama tablica własności to nie wszystko, co opisuje stan.
        /// </summary>
        public bool Undo(NativeArray<byte> owners, out string label)
        {
            label = null;
            if (_steps.Count == 0 || !owners.IsCreated) return false;

            var step = _steps[_steps.Count - 1];
            _steps.RemoveAt(_steps.Count - 1);
            label = step.Label;

            // Od końca: gdyby ta sama komórka trafiła do kroku dwa razy (pędzel przejeżdżający
            // dwukrotnie po tym samym miejscu w jednym pociągnięciu), obowiązuje wartość NAJSTARSZA,
            // czyli ta sprzed całej operacji.
            for (int i = step.Indices.Count - 1; i >= 0; i--)
            {
                int index = step.Indices[i];
                if (index >= 0 && index < owners.Length) owners[index] = step.PreviousOwners[i];
            }

            OnChanged?.Invoke();
            return true;
        }

        public void Clear()
        {
            _current = null;
            _currentOverflowed = false;
            if (_steps.Count == 0) return;

            _steps.Clear();
            OnChanged?.Invoke();
        }
    }
}
