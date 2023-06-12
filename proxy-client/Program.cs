using System.Collections.Concurrent;
using System.Net.Sockets;
using proxy_common;

var proxyServerIpOrHost = "20.8.239.52";
var proxyServerPort = 4000;
var localWebserverIpOrHost = "192.168.2.65";
var localWebserverPort = 7278;

ILogger _logger = new Logger();
ISocketHelper _socketHelper = new SocketHelper(_logger);
ITcpProxy _tcpProxy = new TcpProxy(_logger, _socketHelper);

_logger.Info($"Connecting to proxy server {proxyServerIpOrHost}:{proxyServerPort}");
_logger.Info($"Incoming requests from the proxy server will be forwarded to {localWebserverIpOrHost}:{localWebserverPort}");

var connectionIdToWebserverClient = new ConcurrentDictionary<int, Socket>();
var clientToFromProxyServer = await _socketHelper.ConnectToServer(proxyServerIpOrHost, proxyServerPort);
_logger.Info($"Connected to proxy server. Socket handle: {clientToFromProxyServer.Handle}");
var t = _tcpProxy.SendKeepAlivePingInBckground(clientToFromProxyServer);

var x = Task.Run(() => _tcpProxy.ReadMessage(clientToFromProxyServer, async (buffer, connectionId) =>
    {
        // _logger.Info($"Data from server. {buffer.Length} bytes. ConnectionId: {connectionId}");
        if (connectionIdToWebserverClient.TryGetValue(connectionId, out var webserverClient) is false)
        {
            webserverClient = await _socketHelper.ConnectToServer(localWebserverIpOrHost, localWebserverPort);
            connectionIdToWebserverClient.TryAdd(connectionId, webserverClient);
            var x = Task.Run(async () =>
            {
                var result = await _socketHelper.ReadSocket(webserverClient, TcpProxy.WrappedDataMessageMaxLen, async (buf) =>
                {
                    await _tcpProxy.WriteMessage(clientToFromProxyServer, connectionId, buf);
                    return new ReadMessageResult(ConnectionError: false);
                });
                // if readSocket returns, it means the connection was closed
                connectionIdToWebserverClient.TryRemove(connectionId, out _);
            });
        }
        await webserverClient.SendAsync(buffer);
    }));

x.Wait();
