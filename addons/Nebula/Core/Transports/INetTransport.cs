using System;

namespace Nebula;

/// <summary>
/// This provides a common interface for various transports useable with Nebula.
/// </summary>
public interface INetTransport
{
    public void StartServer();

    public void StartClient();

    public event Action<uint> OnPeerConnected;

    public event Action<uint> OnPeerDisconnected;

    public event Action OnConnectedToServer;
}