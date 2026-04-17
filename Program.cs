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
        private static NetworkingUtils utils = new NetworkingUtils();
        private static uint listenSocket;
        private const ushort SERVER_PORT = 50295;

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

        RunServerSetUp();
        RunMainLoop();
    }

    static void RunServerSetUp()
    {
        // Initialize the Valve Sockets library
        Valve.Sockets.Library.Initialize();

        // Create the server socket
        server = new NetworkingSockets();

        // Set up the listen address
        Address address = new();
        address.SetAddress("65.183.141.222", SERVER_PORT);

        // Create listen socket
        listenSocket = server.CreateListenSocket(ref address);

        if (listenSocket == uint.MaxValue)
        {
            Console.WriteLine("ERROR: Failed to create listen socket!");
            return;
        }

        Console.WriteLine($"Relay server listening on the port {SERVER_PORT}");

        // Set up debug callback
        DebugCallback debugCallback = (DebugType type, string message) =>
        {
            if (type == DebugType.Everything)
            {
                Console.WriteLine($"[DEBUG {type}] {message}");
            }
        };
        
        utils.SetDebugCallback(DebugType.Everything, debugCallback);
        
        SetUpCallbacks();
    }

    static void RunMainLoop()
    {
        Console.WriteLine("Entering main relay loop...\n");
        bool running = true;

        while (running)
        {
            // Process incoming connections and messages
            ProcessNetworkEvents();

            // Small delay to prevent CPU spinning
            System.Threading.Thread.Sleep(16); // ~60 FPS
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
            
            server.RunCallbacks();

            //HandleNewConnection(remoteAddress, remoteAddress);
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
    }

    static void HandleNewConnection(uint connectionId, uint remoteAddress)
    {
        Address addr = new();
        addr.SetAddress("0.0.0.0", SERVER_PORT); // Default address

        var clientConnection = new ClientConnection
        {
            ConnectionId = connectionId,
            IpAddress = addr.GetIP(),
            State = ConnectionState.Connected
        };

        connectedClients[connectionId] = clientConnection;

        Console.WriteLine($"[CONNECT] New client connected - ID: {connectionId}, IP: {clientConnection.IpAddress}");
        Console.WriteLine($"Total connected clients: {connectedClients.Count}");

        // Send welcome message to client
        SendMessageToClient(connectionId, "WELCOME");
    }

    static void SetUpCallbacks()
    {
        // Define the status callback to handle state changes
        StatusCallback status = (ref StatusInfo info) =>
        {
            Console.WriteLine("Status: " + info.connectionInfo.state);
            switch (info.connectionInfo.state)
            {
                case ConnectionState.Connecting:
                    // This is where you accept the incoming connection
                    server.AcceptConnection(info.connection);
                    Console.WriteLine("Accepting connection from " + info.connectionInfo.address.GetIP());
                    break;

                case ConnectionState.Connected:
                    Console.WriteLine("Client successfully connected - ID: " + info.connection);
                    break;

                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    server.CloseConnection(info.connection);
                    Console.WriteLine("Client disconnected.");
                    break;
            }
        };
        
        // Register the callback with the networking library
        utils.SetStatusCallback(status);
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

    static void HandleClientDisconnect(uint clientId)
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
        if (server != null)
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