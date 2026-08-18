using System;
using Microsoft.MixedReality.OpenXR.Remoting;
using UnityEngine;
using TMPro;

/// <summary>
/// Nawiązuje strumieniowanie obrazu do gogli (Holographic Remoting). Aplikacja renderuje na
/// komputerze, a gogle są wyświetlaczem — dlatego obie warstwy interfejsu (panel operatora i menu
/// na dłoni) działają w tej samej sesji.
/// </summary>
public class RemotingConnectionManager : MonoBehaviour
{
    [SerializeField]
    private string m_RemotingIP;
    [SerializeField]
    private ushort m_RemotingPort = 8265;
    [SerializeField]
    private TMP_Text connectionCheckText;
    private RemotingConnectConfiguration RemotingConf;

    /// <summary>Adres używany, gdy nie podano innego — do wypełnienia pola w panelu operatora.</summary>
    public string DefaultIP => m_RemotingIP;

    /// <summary>
    /// Stan połączenia opisany jednym zdaniem, gotowym do pokazania w interfejsie. Zwraca też, czy
    /// wolno teraz próbować się łączyć — panel na tej podstawie blokuje przycisk, zamiast pozwalać
    /// wystrzelić drugą próbę w trakcie pierwszej.
    /// </summary>
    public string DescribeState(out bool canConnect)
    {
        if (!AppRemoting.TryGetConnectionState(out ConnectionState state, out DisconnectReason reason))
        {
            canConnect = true;
            return "Nie połączono z goglami.";
        }

        switch (state)
        {
            case ConnectionState.Connected:
                canConnect = false;
                return "Połączono z goglami.";
            case ConnectionState.Connecting:
                canConnect = false;
                return "Łączenie z goglami…";
            default:
                canConnect = true;
                return reason == DisconnectReason.None
                    ? "Nie połączono z goglami."
                    : $"Rozłączono ({reason}).";
        }
    }

    /// <summary>Zdarzenie dla interfejsu — stan połączenia się zmienił.</summary>
    public event Action OnConnectionStateChanged;

    public void StartConnection(string ipAddress)
    {
        string targetIP = string.IsNullOrEmpty(ipAddress) ? m_RemotingIP : ipAddress;

        if (string.IsNullOrWhiteSpace(targetIP))
        {
            Debug.LogWarning("[Remoting] Nie podano adresu gogli — wpisz go w panelu albo ustaw w Inspektorze.");
            return;
        }

        RemotingConf = new RemotingConnectConfiguration
        {
            RemoteHostName = targetIP,
            RemotePort = m_RemotingPort,
            VideoCodec = RemotingVideoCodec.H265,
            MaxBitrateKbps = 20000
        };
        if (AppRemoting.TryGetConnectionState(out ConnectionState currentState, out DisconnectReason reason))
        {
            if (currentState != ConnectionState.Disconnected)
            {
                Debug.LogWarning($"Streaming już działa lub jest w trakcie łączenia! Obecny stan: {currentState}");
                return;
            }
        }
        AppRemoting.StartConnectingToPlayer(RemotingConf);

        Debug.Log($"Próba połączenia z: {targetIP}...");
        if (connectionCheckText != null) connectionCheckText.text = "Łączenie...";
        OnConnectionStateChanged?.Invoke();
    }

    /// <summary>Rozłącza sesję strumieniowania (przycisk w panelu operatora).</summary>
    public void StopConnection()
    {
        AppRemoting.Disconnect();
        Debug.Log("[Remoting] Rozłączono na żądanie.");
        OnConnectionStateChanged?.Invoke();
    }

    private void OnEnable()
    {
        AppRemoting.Connected += OnConnected;
        AppRemoting.Disconnecting += OnDisconnected;
    }

    private void OnDisable()
    {
        AppRemoting.Connected -= OnConnected;
        AppRemoting.Disconnecting -= OnDisconnected;

        AppRemoting.Disconnect();
    }

    private void OnConnected()
    {
        Debug.Log("Połączono pomyślnie!");
        if (connectionCheckText != null) connectionCheckText.text = "Połączono";
        OnConnectionStateChanged?.Invoke();
    }

    private void OnDisconnected(DisconnectReason reason)
    {
        Debug.LogError($"Połączenie przerwane. Powód: {reason}");
        // Pole tekstowe jest opcjonalne (panel operatora pokazuje stan po swojemu) — bez tej straży
        // każde rozłączenie bez podpiętego pola kończyło się dodatkowym wyjątkiem w logu.
        if (connectionCheckText != null) connectionCheckText.text = reason.ToString();
        OnConnectionStateChanged?.Invoke();
    }
}
