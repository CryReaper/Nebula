using System;
using Godot;
using ENet;
using Nebula.Authentication;
using Nebula.Utility.Tools;
using Nebula.Serialization;

namespace Nebula;

/// <summary>
/// ---
/// </summary>
public partial class ENetTransport : Node, INetTransport
{    
    internal Host ENetHost;

    /// <summary>
    /// Describes the channels of communication used by the network.
    /// </summary>
    public enum ENetChannelId
    {
        /// <summary>
        /// Tick data sent by the server to the client, and from the client indicating the most recent tick it has received.
        /// </summary>
        Tick = 1,

        /// <summary>
        /// Input data sent from the client.
        /// </summary>
        Input = 2,

        /// <summary>
        /// NetFunction call.
        /// </summary>
        Function = 3,

        /// <summary>
        /// World-transfer control (reliable). Server→client "change world" and client→server
        /// "ready" ack for live cross-world migration. Kept off the tick stream so it is
        /// guaranteed-delivered and never bundled with per-tick state.
        /// See <see cref="MigratePeerToWorld"/>.
        /// </summary>
        World = 4,
    }

    private static bool _libraryInitialized = false;

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        if (!_libraryInitialized)
        {
            try
            {
                if (!Library.Initialize())
                {
                    return;
                }
                _libraryInitialized = true;
            }
            catch (Exception e)
            {
                return;
            }
        }
    }

    public override void _ExitTree()
    {
        ENetHost?.Flush();
        ENetHost?.Dispose();

        if (_libraryInitialized)
        {
            Library.Deinitialize();
            _libraryInitialized = false;
        }
    }

    public void StartServer()
    {
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        if (NetRunner.Instance.Authentication == null)
        {
            NetRunner.Instance.SetAuthentication(new DefaultAuthenticator());
        }

        NetRunner.Instance.IsServer = true;
        Debugger.Instance.Log("Starting Server");
        GetTree().MultiplayerPoll = false;

        ENetHost = new Host();
        var address = new Address();
        // Note: For server, only set Port. Do NOT call SetHost - this binds to all interfaces (0.0.0.0)
        address.Port = (ushort)NetRunner.Instance.Port;

        try
        {
            ENetHost.Create(address, NetRunner.Instance.MaxPeers, NetRunner.MaxChannels);
            // Note: ENet-CSharp doesn't have built-in compression like Godot's ENET wrapper
        }
        catch (Exception ex)
        {
            Debugger.Instance.Log(Debugger.DebugLevel.ERROR, $"Error starting: {ex.Message}");
            return;
        }

        NetRunner.Instance.NetStarted = true;
        Debugger.Instance.Log($"Started on port {NetRunner.Instance.Port}");

        // The debug channel is not started here: it is process-wide (see
        // StartDebugHub) so that clients get one too, and so it is already
        // listening before the network starts.
    }

    public void StartClient()
    {
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;

        if (NetRunner.Instance.Authentication == null)
        {
            NetRunner.Instance.SetAuthentication(new DefaultAuthenticator());
        }

        ENetHost = new Host();
        ENetHost.Create();

        var address = new Address();
        address.SetHost(NetRunner.Instance.ServerAddress);
        address.Port = (ushort)NetRunner.Instance.Port;

        // The connect packet carries our protocol hash; the server validates it before
        // admitting the peer and rejects mismatched builds (see ProtocolMismatchException)
        NetRunner.Instance.ServerPeer = ENetHost.Connect(address, NetRunner.MaxChannels, Protocol.HandshakeHash);

        if (!NetRunner.Instance.ServerPeer.IsSet)
        {
            Debugger.Instance.Log($"Error connecting.");
            return;
        }

        NetRunner.Instance.NetStarted = true;
        var worldRunner = new WorldRunner();
        WorldRunner.CurrentWorld = worldRunner;
        GetTree().CurrentScene.AddChild(worldRunner);
        Debugger.Instance.Log("Started");
    }

    public event Action<uint> OnPeerConnected;

    public event Action<uint> OnPeerDisconnected;

    public event Action OnConnectedToServer;

    /// <summary>
    /// ENet disconnect reason code the server sends when rejecting a client whose
    /// protocol hash doesn't match ("PROT" in ASCII). Clients receiving this raise
    /// <see cref="OnProtocolMismatch"/> (or throw <see cref="ProtocolMismatchException"/>
    /// if no handler is subscribed).
    /// </summary>
    public const uint ProtocolMismatchDisconnectCode = 0x50524F54;

    /// <summary>
    /// ENet disconnect reason code the server sends when a peer's packet fails to
    /// deserialize ("MALP" in ASCII). A protocol-compliant client should never produce an
    /// unparseable packet post-handshake, so we treat it as hostile/broken and drop the peer.
    /// </summary>
    public const uint MalformedPacketDisconnectCode = 0x4D414C50;

    /// <summary>
    /// Client-side. Raised when the server rejects the connection due to a protocol
    /// hash mismatch. Subscribe to handle it gracefully (e.g. an "update required"
    /// screen); with no subscribers, the exception is thrown from the event pump.
    /// </summary>
    public event Action<ProtocolMismatchException> OnProtocolMismatch;

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (!NetRunner.Instance.NetStarted)
            return;

        Event netEvent;
        int checkResult = ENetHost.CheckEvents(out netEvent);
        int serviceResult = 0;
        
        if (checkResult <= 0)
        {
            serviceResult = ENetHost.Service(0, out netEvent);
        }
        
        while (checkResult > 0 || serviceResult > 0)
        {
            switch (netEvent.Type)
            {
                case EventType.None:
                    return;

                case EventType.Connect:
                    if (NetRunner.Instance.IsServer)
                    {
                        // Protocol handshake: the connect packet's data field carries the
                        // client's protocol hash. Reject mismatched builds before auth or
                        // world admission - a mismatched client would misparse everything.
                        if (netEvent.Data != Protocol.HandshakeHash)
                        {
                            Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                                $"Rejecting peer {netEvent.Peer.ID}: protocol hash mismatch (server 0x{Protocol.HandshakeHash:X8}, client 0x{netEvent.Data:X8}). Client is running a different build.");
                            netEvent.Peer.Disconnect(ProtocolMismatchDisconnectCode);
                            break;
                        }

                        Debugger.Instance.Log("Peer connected");
                        NetRunner.Instance.PeersByNativeId[netEvent.Peer.ID] = netEvent.Peer;
                        OnPeerConnected?.Invoke(netEvent.Peer.ID);
                    }
                    else
                    {
                        Debugger.Instance.Log("Connected to server");
                        OnConnectedToServer?.Invoke();
                    }
                    break;

                case EventType.Disconnect:
                case EventType.Timeout:
                    if (!NetRunner.Instance.IsServer
                        && netEvent.Type == EventType.Disconnect
                        && netEvent.Data == ProtocolMismatchDisconnectCode)
                    {
                        _OnPeerDisconnected(netEvent.Peer);

                        var mismatch = new ProtocolMismatchException(Protocol.Hash, Protocol.HandshakeHash);
                        Debugger.Instance.Log(mismatch.Message, Debugger.DebugLevel.ERROR);
                        if (OnProtocolMismatch != null)
                        {
                            OnProtocolMismatch.Invoke(mismatch);
                            break;
                        }
                        throw mismatch;
                    }
                    _OnPeerDisconnected(netEvent.Peer);
                    break;

                case EventType.Receive:
                {
                    var channel = netEvent.ChannelID;
                    var packetData = new byte[netEvent.Packet.Length];
                    netEvent.Packet.CopyTo(packetData);
                    netEvent.Packet.Dispose();

                    using var data = new NetBuffer(packetData);

                    // A malformed packet must never abort the event pump: an unhandled
                    // exception here would drop every remaining queued event this frame for
                    // ALL peers. Catch per-packet so one bad sender can't stall everyone.
                    try
                    {
                    switch ((ENetChannelId)channel)
                    {
                        case ENetChannelId.Tick:
                            if (NetRunner.Instance.IsServer)
                            {
                                if (packetData.Length == 0)
                                {
                                    break;
                                }
                                var tick = NetReader.ReadInt32(data);
                                var peerId = NetRunner.Instance.GetPeerId(netEvent.Peer);
                                if (NetRunner.Instance.PeerWorldMap.TryGetValue(peerId, out var world))
                                {
                                    world.PeerAcknowledge(netEvent.Peer, tick);
                                }
                            }
                            else
                            {
                                if (packetData.Length == 0)
                                {
                                    break;
                                }
                                var tick = NetReader.ReadInt32(data);
                                var bytes = NetReader.ReadRemainingBytes(data);
                                // Debug: dump the full payload hex for every server tick
                                // (gated behind the Nebula/config/debug/log_tick_payloads setting).
                                if (NetRunner.LogTickPayloads)
                                {
                                    Debugger.Instance.Log(Debugger.DebugLevel.INFO,
                                        $"[Nebula][TickPayload] tick={tick} ({bytes.Length} bytes) {Convert.ToHexString(bytes)}");
                                }
                                // Debug: simulate packet loss by dropping received ticks before
                                // processing. Client-side only, never touches the shared sim -
                                // exists to exercise loss-recovery paths (spawn resend, delta
                                // baseline fallback) deterministically on a LAN with no real loss.
                                if (NetRunner.SimulateIncomingTickLoss > 0
                                    && NetRunner._tickLossRng.RandiRange(1, 100) <= NetRunner.SimulateIncomingTickLoss)
                                {
                                    break;
                                }
                                WorldRunner.CurrentWorld.ClientProcessTick(tick, bytes);
                            }
                            break;

                        case ENetChannelId.Input:
                            if (NetRunner.Instance.IsServer)
                            {
                                var peerId = NetRunner.Instance.GetPeerId(netEvent.Peer);
                                if (NetRunner.Instance.PeerWorldMap.TryGetValue(peerId, out var world))
                                {
                                    world.ReceiveInput(netEvent.Peer, data);
                                }
                            }
                            // Clients should never receive messages on the Input channel
                            break;

                        case ENetChannelId.Function:
                            if (NetRunner.Instance.IsServer)
                            {
                                var peerId = NetRunner.Instance.GetPeerId(netEvent.Peer);
                                if (NetRunner.Instance.PeerWorldMap.TryGetValue(peerId, out var world))
                                {
                                    world.ReceiveNetFunction(netEvent.Peer, data);
                                }
                            }
                            else
                            {
                                WorldRunner.CurrentWorld.ReceiveNetFunction(NetRunner.Instance.ServerPeer, data);
                            }
                            break;

                        case ENetChannelId.World:
                            NetRunner.Instance.HandleWorldChannel(netEvent.Peer, packetData);
                            break;

                        default:
                            if (NetRunner.Instance.ReservedChannels.TryGetValue(channel, out var handler))
                            {
                                var peer = NetRunner.Instance.GetPeerByNativeId(netEvent.Peer.ID);
                                if (peer.IsSet)
                                {
                                    handler(peer, packetData);
                                }
                            }
                            break;
                    }
                    }
                    catch (Exception ex)
                    {
                        // Server: drop the offending peer (see MalformedPacketDisconnectCode).
                        // Client: the server is trusted, so a malformed packet is a bug, not
                        // an attack - log it but stay connected.
                        Debugger.Instance.Log(Debugger.DebugLevel.ERROR,
                            $"[Nebula][MalformedPacket] Failed to parse packet on channel {channel} from peer {netEvent.Peer.ID}: {ex.Message}");
                        if (NetRunner.Instance.IsServer)
                        {
                            netEvent.Peer.Disconnect(MalformedPacketDisconnectCode);
                        }
                    }
                    break;
                }
            }

            // Check for more events
            checkResult = ENetHost.CheckEvents(out netEvent);
            if (checkResult <= 0)
            {
                serviceResult = ENetHost.Service(0, out netEvent);
            }
        }
    }

    /// <summary>
    /// Helper method to send a packet to a peer.
    /// </summary>
    public static void SendPacket(Peer peer, byte channelId, byte[] data, PacketFlags flags)
    {
        var packet = default(Packet);
        packet.Create(data, flags);
        peer.Send(channelId, ref packet);
    }

    /// <summary>
    /// Helper method to send a packet using a NetBuffer directly (zero-allocation).
    /// Uses the buffer's internal array with proper length to avoid ToArray() allocation.
    /// </summary>
    public static void SendPacket(Peer peer, byte channelId, NetBuffer buffer, PacketFlags flags)
    {
        var packet = default(Packet);
        packet.Create(buffer.RawBuffer, buffer.Length, flags);
        peer.Send(channelId, ref packet);
    }

    /// <summary>
    /// Helper method to send a reliable packet.
    /// </summary>
    public static void SendReliable(Peer peer, byte channelId, byte[] data)
    {
        SendPacket(peer, channelId, data, PacketFlags.Reliable);
    }

    /// <summary>
    /// Helper method to send a reliable packet using a NetBuffer directly (zero-allocation).
    /// </summary>
    public static void SendReliable(Peer peer, byte channelId, NetBuffer buffer)
    {
        SendPacket(peer, channelId, buffer, PacketFlags.Reliable);
    }

    /// <summary>
    /// Helper method to send an unreliable packet.
    /// </summary>
    public static void SendUnreliable(Peer peer, byte channelId, byte[] data)
    {
        SendPacket(peer, channelId, data, PacketFlags.None);
    }

    /// <summary>
    /// Helper method to send an unreliable packet using a NetBuffer directly (zero-allocation).
    /// </summary>
    public static void SendUnreliable(Peer peer, byte channelId, NetBuffer buffer)
    {
        SendPacket(peer, channelId, buffer, PacketFlags.None);
    }

    /// <summary>
    /// Helper method to send an unreliable sequenced packet (newer packets discard older ones).
    /// </summary>
    public static void SendUnreliableSequenced(Peer peer, byte channelId, byte[] data)
    {
        SendPacket(peer, channelId, data, PacketFlags.Unsequenced);
    }

    /// <summary>
    /// Helper method to send an unreliable sequenced packet using a NetBuffer directly (zero-allocation).
    /// </summary>
    public static void SendUnreliableSequenced(Peer peer, byte channelId, NetBuffer buffer)
    {
        SendPacket(peer, channelId, buffer, PacketFlags.Unsequenced);
    }

    private void SendWorldMessage(NetPeer peer, byte opcode, in UUID worldId)
    {
        NetRunner.Instance._worldChannelBuffer ??= new NetBuffer();
        NetRunner.Instance._worldChannelBuffer.Reset();
        NetWriter.WriteByte(NetRunner.Instance._worldChannelBuffer, opcode);
        Span<byte> guidBytes = stackalloc byte[16];
        worldId.Guid.TryWriteBytes(guidBytes);
        NetWriter.WriteBytes(NetRunner.Instance._worldChannelBuffer, (ReadOnlySpan<byte>)guidBytes);
        SendReliable(peer, (byte)ENetChannelId.World, NetRunner.Instance._worldChannelBuffer);
    }

    public void _OnPeerDisconnected(Peer peer)
    {
        Debugger.Instance.Log($"Peer disconnected peerId: {peer.ID}");
        OnPeerDisconnected?.Invoke(peer.ID);
        NetRunner.Instance.PeersByNativeId.Remove(peer.ID);
    }
}