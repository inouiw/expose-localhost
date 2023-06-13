using System.Collections.Concurrent;
using proxy_common;

var proxyServerIpOrHost = "<proxy-server-host-or-ip>"; // "127.0.0.1";
var proxyServerPort = 4000;
var localWebserverIpOrHost = "192.168.2.65";
var localWebserverPort = 7278;

ILogger _logger = new Logger();
ISocketHelper _socketHelper = new SocketHelper(_logger);
ITcpProxy _tcpProxy = new TcpProxy(_logger, _socketHelper);

_logger.Info($"Connecting to proxy server {proxyServerIpOrHost}:{proxyServerPort}");
_logger.Info($"Incoming requests from the proxy server will be forwarded to {localWebserverIpOrHost}:{localWebserverPort}");

var connectionIdToWebserverClient = new ConcurrentDictionary<int, IWrappedSocket>();
var clientToFromProxyServer = await _socketHelper.ConnectToServer(proxyServerIpOrHost, proxyServerPort, $"ConnectedToProxyServerPort-{proxyServerPort}");
_logger.Info($"Connected to proxy server. Socket name: {clientToFromProxyServer.Name}");
await _tcpProxy.WriteClientHelloMessage(clientToFromProxyServer);
var t = _tcpProxy.SendKeepAlivePingInBckground(clientToFromProxyServer);

await _tcpProxy.ReadMessage(clientToFromProxyServer, async (buffer, connectionId) =>
    {
        // _logger.Info($"Data from server. {buffer.Length} bytes. ConnectionId: {connectionId}");
        if (connectionIdToWebserverClient.TryGetValue(connectionId, out var webserverClient) is false)
        {
            webserverClient = await _socketHelper.ConnectToServer(localWebserverIpOrHost, localWebserverPort, $"ConnectedToLocalServerPort-{localWebserverPort}");
            connectionIdToWebserverClient.TryAdd(connectionId, webserverClient);
            var x = Task.Run(async () =>
            {
                var result = await _socketHelper.ReadSocketUntilError(webserverClient, TcpProxy.WrappedDataMessageMaxLen, async (buf) =>
                {
                    await _tcpProxy.WriteMessage(clientToFromProxyServer, connectionId, buf);
                    return new ReadMessageResult(ConnectionError: false);
                });
                // if readSocket returns, it means the connection was closed
                // webserverClient.Close();
                connectionIdToWebserverClient.TryRemove(connectionId, out _);
            });
        }
        await webserverClient.SendAsync(buffer);
    });

_logger.Info("Exiting");
