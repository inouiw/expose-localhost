using System.Collections.Concurrent;
using System.Net.Sockets;
using proxy_common;

int connectionIdCounter = 0;
var connectionIdToNonProxyClient = new ConcurrentDictionary<int, IWrappedSocket>();
var connectedProxyClient = (IWrappedSocket?)null;

int portListenForProxyClients = 4000;
int portListenForNonProxyClients = 443;

ILogger _logger = new Logger();
ISocketHelper _socketHelper = new SocketHelper(_logger);
ITcpProxy _tcpProxy = new TcpProxy(_logger, _socketHelper);

var listener1 = _socketHelper.ListenForConnections(portListenForProxyClients, async (socketClient) =>
{
    if (await _tcpProxy.TryReadClientHelloMessage(socketClient) is false)
    {
        socketClient.Close();
        return;
    }
    _logger.Info($"Proxy client connected. IP: {socketClient.RemoteAddress}, port: {socketClient.RemotePort}, Socket handle: {socketClient.Handle}");
    connectedProxyClient = socketClient;
    await _tcpProxy.ReadMessage(socketClient, async (buffer, connectionId) =>
    {
        var nonProxyClient = connectionIdToNonProxyClient[connectionId];
        try
        {
            int bytesSent = await nonProxyClient.SendAsync(buffer);
            _logger.Info($"Sent {bytesSent} bytes to non-proxy client. ConnectionId: {connectionId}");
        }
        catch (ObjectDisposedException)
        {
            _logger.Error($"ObjectDisposedException in ReadMessage callback. Socket closed. Socket handle: {nonProxyClient.Handle}.");
        }
        catch (SocketException e)
        {
            _logger.Error($"SocketException in ReadMessage callback. Socket handle: {nonProxyClient.Handle}. SocketException: {e.Message}");
        }
    });
});

var listener2 = _socketHelper.ListenForConnections(portListenForNonProxyClients, async (socketClient) =>
{
    int connectionId = ++connectionIdCounter;
    _logger.Info($"Browser client connected. IP: {socketClient.RemoteAddress}, port: {socketClient.RemotePort}, Socket handle: {socketClient.Handle}, connectionId: {connectionId}");
    connectionIdToNonProxyClient.TryAdd(connectionId, socketClient);
    var x = await _socketHelper.ReadSocketUntilError(socketClient, TcpProxy.WrappedDataMessageMaxLen, async (buffer) =>
    {
        if (connectedProxyClient != null)
        {
            _logger.Info($"Writing to proxy client. {buffer.Length} bytes without header data, {buffer.Length + TcpProxy.DataMessageHeaderLen} bytes including header data.");
            int bytesSent = await _tcpProxy.WriteMessage(connectedProxyClient, connectionId, buffer);
            _logger.Info($"Sent {bytesSent} bytes to proxy client. ConnectionId: {connectionId}");
        }
        return new ReadMessageResult(ConnectionError: false);
    });
    // socketClient.Close();
    // return x;
});

_logger.Info("ProxyServer waiting for connections");
await Task.WhenAny(listener1, listener2);
_logger.Info("ProxyServer exiting");
