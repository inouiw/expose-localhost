using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using proxy_common;

int connectionIdCounter = 0;
var connectionIdToNonProxyClient = new ConcurrentDictionary<int, Socket>();
var connectedProxyClient = (Socket?)null;

int portListenForProxyClients = 4000;
int portListenForNonProxyClients = 443;

var listener1 = SocketHelper.ListenForConnections(portListenForProxyClients, (socketClient) =>
{
    Console.WriteLine($"New client connected on port {portListenForProxyClients} {(socketClient.RemoteEndPoint as IPEndPoint)?.Address}");
    connectedProxyClient = socketClient;
    return TcpProxy.ReadMessage(socketClient, async (buffer, connectionId) =>
    {
        var nonProxyClient = connectionIdToNonProxyClient[connectionId];
        await nonProxyClient.SendAsync(buffer);
    });
});

var listener2 = SocketHelper.ListenForConnections(portListenForNonProxyClients, (socketClient) =>
{
    Console.WriteLine($"New client connected on port {portListenForNonProxyClients} {(socketClient.RemoteEndPoint as IPEndPoint)?.Address}");
    int connectionId = ++connectionIdCounter;
    connectionIdToNonProxyClient.TryAdd(connectionId, socketClient);
    return Task.Run(() => SocketHelper.ReadSocket(socketClient, TcpProxy.WrappedMessageMaxLen, async (buffer) =>
    {
        if (connectedProxyClient != null)
        {
            Console.WriteLine($"WriteMessageToProxyClient {buffer.Length}");
            await TcpProxy.WriteMessage(connectedProxyClient, connectionId, buffer);
        }
    }));
});

Console.WriteLine("ProxyServer waiting for connections");
await Task.WhenAny(listener1, listener2);
Console.WriteLine("ProxyServer exiting");
