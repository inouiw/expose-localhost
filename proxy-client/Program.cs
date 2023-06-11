using System.Collections.Concurrent;
using System.Net.Sockets;
using proxy_common;

var proxyServerIpOrHost = "your.domain.name";
var proxyServerPort = 4000;
var localWebserverIpOrHost = "192.168.2.65";
var localWebserverPort = 7278;

Console.WriteLine($"Connecting to proxy server {proxyServerIpOrHost}:{proxyServerPort}");
Console.WriteLine($"Incoming connections from the proxy server will be forwarded to {localWebserverIpOrHost}:{localWebserverPort}");

var connectionIdToWebserverClient = new ConcurrentDictionary<int, Socket>();
var clientToFromProxyServer = await SocketHelper.ConnectToServer(proxyServerIpOrHost, proxyServerPort);
Console.WriteLine("Connected to proxy server");

var x = Task.Run(() => TcpProxy.ReadMessage(clientToFromProxyServer, async (buffer, connectionId) =>
    {
        Console.WriteLine($"data from server {buffer.Length}");
        if (connectionIdToWebserverClient.TryGetValue(connectionId, out var webserverClient) is false)
        {
            webserverClient = await SocketHelper.ConnectToServer(localWebserverIpOrHost, localWebserverPort);
            connectionIdToWebserverClient.TryAdd(connectionId, webserverClient);
            var readFromWebserverTask = Task.Run(() => SocketHelper.ReadSocket(webserverClient, TcpProxy.WrappedMessageMaxLen, async (buf) =>
            {
                await TcpProxy.WriteMessage(clientToFromProxyServer, connectionId, buf);
            }));
        }
        await webserverClient.SendAsync(buffer);
    }));

Console.ReadKey();
