// See https://aka.ms/new-console-template for more information

using System.Net;
using System.Text;
using Valve.Sockets;
using System.Net.Sockets;
using System.Runtime.InteropServices;

/// <summary>
/// Relay server for managing master client and multiple client connections
/// Architecture: Master Client <-> Relay Server <-> Client(s)
/// </summary>
class Touhou99Relay
    {
        private static NetworkingSockets? serverAcceptor;
        //private static NetworkingSockets? client;
        private static NetworkingUtils serverUtils = new NetworkingUtils();
        //private static NetworkingUtils clientUtils = new NetworkingUtils();
        private static uint listenSocket;
        private const ushort SERVER_PORT = 8095;

        private Dictionary<uint, uint> roomToSocket = new();

        // Connection tracking
        private static Dictionary<uint, ClientConnection> connectedClients = new();
        private static Dictionary<uint, RoomData> roomDataMap = new();
        
        private const int MAX_MESSAGES = 20;
        private static NetworkingMessage[] netMessages = new NetworkingMessage[MAX_MESSAGES];

        struct RoomData
        {
            public uint hostConnection;
            public List<uint> clientConnections;
        }
        
        public enum PacketType
        {
            PLAYER_DATA,
            REGISTER_PLAYER,
            PLAYER_COUNT,
            GAME_STATE,
            STORE_PLAYER_RESULTS,
            SEND_RESULT,
            OFFENSIVE_BOMB_DATA,
            OTHER_PLAYER_FINISH,
            REGISTER_ROOM,
            JOIN_ROOM,
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct TypeFinder
        {
            public int type;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct RoomExtractor
        {
            public int type;
            public uint roomID;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct RegisterPlayer
        {
            public int type;
            public uint roomID;
            public uint playerID;
        };

        /// <summary>
        /// Represents a connected client
        /// </summary>
        private class ClientConnection
        {
            public uint ConnectionId { get; set; }
            public string IpAddress { get; set; } = "";
            public bool IsMaster { get; set; } = false;
            public ConnectionState State { get; set; } = ConnectionState.None;
        }

    // The entry point of the application
    static void Main(string[] args)
    {
        Console.WriteLine("=== Touhou99 Relay Server Started ===");
        Console.WriteLine($"Listening on port {SERVER_PORT}");

        SetUpServerAcceptor();
        RunMainLoop();
    }

    static void SetUpServerAcceptor()
    {
        // Initialize the Valve Sockets library
        Valve.Sockets.Library.Initialize();

        // Create the server socket
        serverAcceptor = new NetworkingSockets();
        uint pollGroup = serverAcceptor.CreatePollGroup();

        // Define the status callback to handle state changes
        StatusCallback status = (ref StatusInfo info) => {
            switch (info.connectionInfo.state) {
                case ConnectionState.None:
                    Console.WriteLine("Something was received " + info.connectionInfo.state);
                    break;

                case ConnectionState.Connecting:
                    Console.WriteLine("Connection incomming from " + info.connectionInfo.address.GetIP());
                    serverAcceptor.AcceptConnection(info.connection);
                    break;

                case ConnectionState.Connected:
                    Console.WriteLine("Client connected - ID: " + info.connection + ", IP: " + info.connectionInfo.address.GetIP());
                    HandleClientConnected(info.connection);
                    break;

                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    serverAcceptor.CloseConnection(info.connection);
                    Console.WriteLine("Client disconnected - ID: " + info.connection + ", IP: " + info.connectionInfo.address.GetIP());
                    break;
            }
        };
        
        serverUtils.SetStatusCallback(status);

        Address address = new Address();

        // IPAddress serverIP = new IPAddress(0);
        // var host = Dns.GetHostEntry(Dns.GetHostName());
        // foreach (var ip in host.AddressList)
        // {
        //     if (ip.AddressFamily == AddressFamily.InterNetwork)
        //     {
        //         serverIP = ip;
        //     }
        // }
        
        address.SetAddress("65.183.141.222", SERVER_PORT); 
        
        listenSocket = serverAcceptor.CreateListenSocket(ref address);

        if (listenSocket == uint.MaxValue)
        {
            Console.WriteLine("ERROR: Failed to create listen socket!");
            return;
        }

        Console.WriteLine($"Relay server listening on IP 65.183.141.222 and the port {SERVER_PORT}");

        // Set up debug callback
        DebugCallback debugCallback = (DebugType type, string message) =>
        {
            if (type == DebugType.Everything)
            {
                Console.WriteLine($"[DEBUG {type}] {message}");
            }
        };
        
        //utils.SetDebugCallback(DebugType.Everything, debugCallback);
        
        //TestClientJoin();
        //SetUpCallbacks();
    }

    // static void TestClientJoin()
    // {
    //     client = new NetworkingSockets();
    //
    //     uint connection = 0;
    //
    //     StatusCallback status = (ref StatusInfo info) => {
    //         switch (info.connectionInfo.state) {
    //             case ConnectionState.None:
    //                 break;
    //
    //             case ConnectionState.Connected:
    //                 Console.WriteLine("Client connected to server - ID: " + connection);
    //                 break;
    //
    //             case ConnectionState.ClosedByPeer:
    //             case ConnectionState.ProblemDetectedLocally:
    //                 client.CloseConnection(connection);
    //                 Console.WriteLine("Client disconnected from server");
    //                 break;
    //         }
    //     };
    //
    //     //clientUtils.SetStatusCallback(status);
    //
    //     Address address = new Address();
    //
    //     address.SetAddress("65.183.141.222", SERVER_PORT);
    //
    //     connection = client.Connect(ref address);
    // }

    private static void HandleClientConnected(uint clientConnection)
    {
        if (!connectedClients.ContainsKey(clientConnection))
        {
            connectedClients[clientConnection] = new ClientConnection
            {
                ConnectionId = clientConnection,
                IpAddress = "Unknown",
                State = ConnectionState.Connected
            };
        }
    }

    static void RunMainLoop()
    {
        Console.WriteLine("Entering main relay loop...\n");
        bool running = true;

        if (running)
        {
            // Process incoming connections and messages
            ProcessNetworkEvents();
        }
    }

    static void ProcessNetworkEvents()
    {
        if (serverAcceptor == null)
            return;

        // Poll for incoming connection requests
        while (true)
        {
            // uint remoteAddress = 0;
            // Result result = server.AcceptConnection(remoteAddress);
            //
            // if (result != Result.OK)
            //     break; // No more pending connections
            //Console.WriteLine("Running Callbacks");

            serverAcceptor.RunCallbacks();

            ProcessMessages();
            
            // Small delay to prevent CPU spinning
            System.Threading.Thread.Sleep(16); // ~60 FPS
            //HandleNewConnection(remoteAddress, remoteAddress);
        }
    }

    static void ProcessMessages()
    {
        MessageCallback messageCallbacks = (in NetworkingMessage netMessage) => 
        { 
            byte[] messageDataBuffer = new byte[netMessage.length];
            netMessage.CopyTo(messageDataBuffer);
            //netMessage.Destroy();
            
            IntPtr ptPoit = Marshal.AllocHGlobal(messageDataBuffer.Length);
            Marshal.Copy(messageDataBuffer, 0, ptPoit, messageDataBuffer.Length);

            TypeFinder packetType = (TypeFinder)Marshal.PtrToStructure(ptPoit, typeof(TypeFinder));

            switch ((PacketType)packetType.type)
            {
                case PacketType.REGISTER_ROOM:
                    RoomExtractor pData = (RoomExtractor)Marshal.PtrToStructure(ptPoit, typeof(RoomExtractor));
                    Console.WriteLine($"Room {pData.roomID} connected id: {pData.type}");
                    roomDataMap[pData.roomID] = new RoomData { hostConnection = netMessage.connection, clientConnections = new List<uint>() };
                    break;
                 case ((PacketType.JOIN_ROOM)):
                    RoomExtractor packetData = (RoomExtractor)Marshal.PtrToStructure(ptPoit, typeof(RoomExtractor));
                    Console.WriteLine($"JOINING Room {packetData.roomID} connected id: {packetData.type}");
                     roomDataMap[packetData.roomID].clientConnections.Add(netMessage.connection);
                     RegisterNewClientOnHostingClient(netMessage.connection, packetData.roomID);
                     break;
                 default:
                    RoomExtractor roomData = (RoomExtractor)Marshal.PtrToStructure(ptPoit, typeof(RoomExtractor));
                    Console.WriteLine($"SENDING ON Room {roomData.roomID} packet type: {roomData.type}");
                    if (roomDataMap.ContainsKey(roomData.roomID))
                    {
                        if (roomDataMap[roomData.roomID].hostConnection == netMessage.connection)
                        {
                            Console.WriteLine($"Client Count : {roomDataMap[roomData.roomID].clientConnections.Count}");
                            foreach (uint client in roomDataMap[roomData.roomID].clientConnections)
                            {
                                EchoMessage(client, messageDataBuffer);
                            }
                        }
                        else
                        {
                            EchoMessage(roomDataMap[roomData.roomID].hostConnection, messageDataBuffer);
                        }
                    }

                    break;
            }
            
            Marshal.FreeHGlobal(ptPoit);
        };
        
        foreach (var clientId in connectedClients.Keys)
        {
            serverAcceptor.ReceiveMessagesOnConnection(clientId, messageCallbacks, MAX_MESSAGES);
        }
    }

    private static void RegisterNewClientOnHostingClient(uint clientId, uint roomId)
    {
        if (!roomDataMap.ContainsKey(roomId)) { return; }
        
        RegisterPlayer playerData = new RegisterPlayer();
        playerData.type = (int)PacketType.REGISTER_PLAYER;
        playerData.playerID = clientId;
        playerData.roomID = roomId;
        
        Byte[] bytes = new Byte[Marshal.SizeOf(typeof(RegisterPlayer))];
        GCHandle pinStructure = GCHandle.Alloc(playerData, GCHandleType.Pinned);
        try
        {
            Marshal.Copy(pinStructure.AddrOfPinnedObject(), bytes, 0, bytes.Length);
        }
        finally
        {
            serverAcceptor.SendMessageToConnection(roomDataMap[roomId].hostConnection, bytes);
            serverAcceptor.SendMessageToConnection(clientId, bytes);
        }
        
        pinStructure.Free();
    }

    private static void EchoMessage(uint clientToSendTo, byte[] messageData)
    {
        try
        {
            serverAcceptor.SendMessageToConnection(clientToSendTo, messageData);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}