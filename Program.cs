// See https://aka.ms/new-console-template for more information

using System.Diagnostics;
using System.Net;
using System.Text;
using Valve.Sockets;

/// <summary>
/// Relay server for managing master client and multiple client connections
/// Architecture: Master Client <-> Relay Server <-> Client(s)
/// </summary>
class Touhou99Relay
    {
        private static NetworkingSockets? server;
        private static NetworkingSockets? client;
        private static NetworkingUtils serverUtils = new NetworkingUtils();
        private static NetworkingUtils clientUtils = new NetworkingUtils();
        private static uint listenSocket;
        private const ushort SERVER_PORT = 8095;
        private static readonly bool enableSelfTest = IsSelfTestEnabled();

        // Connection tracking
        private static Dictionary<uint, ClientConnection> connectedClients = new();
        private static uint? masterClientConnection = null;

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

        if (!RunServerSetUp())
        {
            Console.WriteLine("Startup aborted because the relay server failed to bind its listen socket.");
            return;
        }

        RunMainLoop();
    }

    static bool IsSelfTestEnabled()
    {
        string? rawValue = Environment.GetEnvironmentVariable("TOUHOU99RELAY_ENABLE_SELF_TEST");
        return string.Equals(rawValue, "1", StringComparison.OrdinalIgnoreCase)
            || (bool.TryParse(rawValue, out bool enabled) && enabled);
    }

    static bool RunServerSetUp()
    {
        // Initialize the Valve Sockets library
        try
        {
            Valve.Sockets.Library.Initialize();
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine("[STARTUP ERROR] Failed to load the GameNetworkingSockets native library.");
            Console.WriteLine($"[STARTUP ERROR] {ex.Message}");
            Console.WriteLine("[STARTUP ERROR] If you are on Apple Silicon or another non-x64 environment, run the relay through Docker with linux/amd64 or install matching native libraries.");
            return false;
        }
        catch (BadImageFormatException ex)
        {
            Console.WriteLine("[STARTUP ERROR] GameNetworkingSockets was found, but the native library architecture is incompatible with the current runtime.");
            Console.WriteLine($"[STARTUP ERROR] {ex.Message}");
            Console.WriteLine("[STARTUP ERROR] Use an x64-compatible environment for Valve.Sockets, or run the Docker image with linux/amd64.");
            return false;
        }

        // Create the server socket
        server = new NetworkingSockets();
        NetworkingSockets activeServer = server;
        uint pollGroup = activeServer.CreatePollGroup();

        // Define the status callback to handle state changes
        StatusCallback status = (ref StatusInfo info) => {
            string ipAddress = info.connectionInfo.address.GetIP();
            Console.WriteLine($"[STATUS][SERVER] Connection {info.connection}, IP: {ipAddress}, State: {info.connectionInfo.state}");

            switch (info.connectionInfo.state) {
                case ConnectionState.None:
                    Console.WriteLine("[STATUS][SERVER] Received ConnectionState.None - this usually means an incomplete handshake or self-test traffic.");
                    break;

                case ConnectionState.Connecting:
                    Console.WriteLine("[CONNECT] Accepting connection from " + ipAddress);
                    activeServer.AcceptConnection(info.connection);
                    activeServer.SetConnectionPollGroup(pollGroup, info.connection);
                    TrackConnection(info.connection, ipAddress, ConnectionState.Connecting);
                    break;

                case ConnectionState.Connected:
                    TrackConnection(info.connection, ipAddress, ConnectionState.Connected);
                    Console.WriteLine("Client connected - ID: " + info.connection + ", IP: " + ipAddress);
                    break;

                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    TrackConnection(info.connection, ipAddress, info.connectionInfo.state);
                    activeServer.CloseConnection(info.connection);
                    Console.WriteLine("Client disconnected - ID: " + info.connection + ", IP: " + ipAddress);
                    HandleClientDisconnect(info.connection, closeConnection: false);
                    break;
            }
        };
        
        serverUtils.SetStatusCallback(status);

        Address address = new Address();

        address.SetAddress("0.0.0.0", SERVER_PORT);

        listenSocket = activeServer.CreateListenSocket(ref address);

        if (listenSocket == uint.MaxValue)
        {
            Console.WriteLine("ERROR: Failed to create listen socket!");
            return false;
        }

        Console.WriteLine($"Relay server listening on the port {SERVER_PORT} (listen socket {listenSocket})");

        if (enableSelfTest)
        {
            Console.WriteLine($"[SELF-TEST] Enabled. Creating a loopback client against 127.0.0.1:{SERVER_PORT}.");
            TestClientJoin();
        }
        else
        {
            Console.WriteLine("[SELF-TEST] Disabled. Set TOUHOU99RELAY_ENABLE_SELF_TEST=1 to run the local loopback client probe.");
        }

        //SetUpCallbacks();
        return true;
    }

    static void TestClientJoin()
    {
        client = new NetworkingSockets();
        NetworkingSockets activeClient = client;

        uint connection = 0;

        StatusCallback status = (ref StatusInfo info) => {
            string ipAddress = info.connectionInfo.address.GetIP();
            Console.WriteLine($"[STATUS][SELF-TEST] Connection {info.connection}, IP: {ipAddress}, State: {info.connectionInfo.state}");

            switch (info.connectionInfo.state) {
                case ConnectionState.None:
                    break;

                case ConnectionState.Connected:
                    Console.WriteLine("Self-test client connected to server - ID: " + connection);
                    break;

                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    activeClient.CloseConnection(connection);
                    Console.WriteLine("Self-test client disconnected from server");
                    break;
            }
        };

        clientUtils.SetStatusCallback(status);

        Address address = new Address();

        address.SetAddress("127.0.0.1", SERVER_PORT);

        connection = activeClient.Connect(ref address);
        Console.WriteLine($"[SELF-TEST] Started loopback connection attempt {connection} to 127.0.0.1:{SERVER_PORT}");
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
        if (server == null)
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

            server.RunCallbacks();

            if (client != null)
            {
                client.RunCallbacks();
            }

            // Process messages from all connected clients
            foreach (var clientId in connectedClients.Keys.ToList())
            {
                ProcessMessagesFromClient(clientId);
            }

            // Poll for connection status changes
            foreach (var clientId in connectedClients.Keys.ToList())
            {
                ProcessConnectionState(clientId);
            }
            
            // Small delay to prevent CPU spinning
            System.Threading.Thread.Sleep(16); // ~60 FPS
            //HandleNewConnection(remoteAddress, remoteAddress);
        }
    }

    static void TrackConnection(uint connectionId, string ipAddress, ConnectionState state)
    {
        bool isNewConnection = !connectedClients.TryGetValue(connectionId, out ClientConnection? clientConnection);
        ConnectionState previousState = clientConnection?.State ?? ConnectionState.None;

        if (isNewConnection)
        {
            clientConnection = new ClientConnection
            {
                ConnectionId = connectionId,
                IpAddress = ipAddress,
                State = state
            };

            connectedClients[connectionId] = clientConnection;

            Console.WriteLine($"[CONNECT] New client tracked - ID: {connectionId}, IP: {ipAddress}");
            Console.WriteLine($"Total connected clients: {connectedClients.Count}");
        }
        else
        {
            clientConnection!.IpAddress = ipAddress;
            clientConnection.State = state;
        }

        if (previousState != state)
        {
            Console.WriteLine($"[STATE CHANGE] Client {connectionId}: {previousState} -> {state}");
        }

        if (previousState != ConnectionState.Connected && state == ConnectionState.Connected)
        {
            SendMessageToClient(connectionId, "WELCOME");
        }
    }

    static void SetUpCallbacks()
    {
        // Define the status callback to handle state changes
        StatusCallback status = (ref StatusInfo info) =>
        {
            if (server == null)
            {
                Console.WriteLine("[ERROR] Received a server callback before the server socket was initialized.");
                return;
            }

            NetworkingSockets activeServer = server;
            Console.WriteLine("Status: " + info.connectionInfo.state);
            Console.WriteLine("Accepting connection from " + info.connectionInfo.address.GetIP());
            switch (info.connectionInfo.state)
            {
                case ConnectionState.Connecting:
                    // This is where you accept the incoming connection
                    activeServer.AcceptConnection(info.connection);
                    Console.WriteLine("Accepting connection from " + info.connectionInfo.address.GetIP());
                    break;

                case ConnectionState.Connected:
                    Console.WriteLine("Client successfully connected - ID: " + info.connection);
                    break;

                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    activeServer.CloseConnection(info.connection);
                    Console.WriteLine("Client disconnected.");
                    break;
            }
        };
        
        // Register the callback with the networking library
       // utils.SetStatusCallback(status);
    }

    static void ProcessMessagesFromClient(uint clientId)
    {
        if (server == null || !connectedClients.ContainsKey(clientId))
            return;

        IntPtr[] messageData = new IntPtr[20]; // k_nMaxMessagesPerBatch
        MessageCallback callback = (in NetworkingMessage message) =>
        {
            ProcessMessageFromClient(clientId, message);
        };

        // Poll messages  - this is a simplified approach
        // In practice, you would use server.ReceiveMessagesOnConnection or a callback mechanism
    }

    static void ProcessMessageFromClient(uint clientId, in NetworkingMessage message)
    {
        try
        {
            // Get the message data - NetworkingMessage structure contains the data
            // Extract the message payload based on the message structure
            string messageText = $"Message from client {clientId}";

            Console.WriteLine($"[MESSAGE] From client {clientId}: {messageText}");

            // Handle special commands
            if (messageText.StartsWith("MASTER:"))
            {
                HandleMasterCommand(clientId, messageText.Substring(7));
            }
            else if (messageText.StartsWith("RELAY:"))
            {
                HandleRelayCommand(clientId, messageText.Substring(6));
            }
            else
            {
                // Broadcast to all other clients if this is the master, or send to master if this is a client
                RelayMessage(clientId, messageText);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to process message: {ex.Message}");
        }
    }

    static void HandleMasterCommand(uint clientId, string command)
    {
        Console.WriteLine($"[MASTER CMD] Client {clientId}: {command}");

        // Designate this client as the master
        if (masterClientConnection.HasValue && masterClientConnection.Value != clientId)
        {
            // Notify previous master
            SendMessageToClient(masterClientConnection.Value, "STATUS:DEMOTED_FROM_MASTER");
            connectedClients[masterClientConnection.Value].IsMaster = false;
        }

        masterClientConnection = clientId;
        connectedClients[clientId].IsMaster = true;
        SendMessageToClient(clientId, "STATUS:PROMOTED_TO_MASTER");

        Console.WriteLine($"[INFO] Client {clientId} is now the master client");
    }

    static void HandleRelayCommand(uint clientId, string command)
    {
        Console.WriteLine($"[RELAY CMD] Client {clientId}: {command}");
        // Handle relay-specific commands here
    }

    static void RelayMessage(uint fromClientId, string message)
    {
        if (!connectedClients.ContainsKey(fromClientId))
            return;

        string prefix = $"FROM_CLIENT_{fromClientId}:";
        string relayMessage = prefix + message;

        // If message is from a regular client, send to master
        if (!connectedClients[fromClientId].IsMaster && masterClientConnection.HasValue)
        {
            SendMessageToClient(masterClientConnection.Value, relayMessage);
        }
        // If message is from master, broadcast to all clients
        else if (connectedClients[fromClientId].IsMaster)
        {
            foreach (var clientId in connectedClients.Keys.Where(c => c != fromClientId))
            {
                SendMessageToClient(clientId, relayMessage);
            }
        }
    }

    static void ProcessConnectionState(uint clientId)
    {
        if (server == null || !connectedClients.ContainsKey(clientId))
            return;

        ConnectionInfo connectionInfo = new();
        if (!server.GetConnectionInfo(clientId, ref connectionInfo))
            return;

        ConnectionState oldState = connectedClients[clientId].State;
        ConnectionState newState = connectionInfo.state;

        if (oldState != newState)
        {
            connectedClients[clientId].State = newState;
            Console.WriteLine($"[STATE CHANGE] Client {clientId}: {oldState} -> {newState}");

            switch (newState)
            {
                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    HandleClientDisconnect(clientId);
                    break;
            }
        }
    }

    static void HandleClientDisconnect(uint clientId, bool closeConnection = true)
    {
        if (connectedClients.ContainsKey(clientId))
        {
            var client = connectedClients[clientId];
            Console.WriteLine($"[DISCONNECT] Client {clientId} ({client.IpAddress}) disconnected");

            if (client.IsMaster)
            {
                masterClientConnection = null;
                Console.WriteLine("[INFO] Master client disconnected - waiting for new master");
            }

            connectedClients.Remove(clientId);
            Console.WriteLine($"Total connected clients: {connectedClients.Count}");
        }

        // Close the connection
        if (closeConnection && server != null)
        {
            server.CloseConnection(clientId, 0, "Client disconnected", true);
        }
    }

    static void SendMessageToClient(uint clientId, string message)
    {
        if (server == null || !connectedClients.ContainsKey(clientId))
            return;

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, ptr, data.Length);

            Result result = server.SendMessageToConnection(clientId, ptr, (uint)data.Length, SendFlags.Reliable);

            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);

            if (result != Result.OK)
            {
                Console.WriteLine($"[ERROR] Failed to send message to client {clientId}: {result}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Exception sending message: {ex.Message}");
        }
    }
}