using System;
using Godot;

namespace Nebula;

/// <summary>
/// ---
/// </summary>
public partial class SteamTransport : Node, INetTransport
{
    public event Action<uint> OnPeerConnected;
    public event Action<uint> OnPeerDisconnected;
    public event Action OnConnectedToServer;

    public void StartServer()
    {
        throw new NotImplementedException();
    }

    public void StartClient()
    {
        throw new NotImplementedException();
    }
}