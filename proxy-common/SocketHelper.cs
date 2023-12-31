using System.Net;
using System.Net.Sockets;

namespace proxy_common;

public interface ISocketHelper
{
    Task<IWrappedSocket> ConnectToServer(string ipOrHost, int port, string socketNamePrefix);
    Task ListenForConnections(int port, Func<IWrappedSocket, Task> onNewConnection);
    Task<ReadMessageResult> ReadSocketUntilError(IWrappedSocket socket, int maxResultLen, Func<Memory<byte>, Task<ReadMessageResult>> onNewData);
}

public record ReadMessageResult(bool ConnectionError);

public class SocketHelper : ISocketHelper
{
    private readonly ILogger _logger;

    public SocketHelper(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<IWrappedSocket> ConnectToServer(string ipOrHost, int port, string socketNamePrefix)
    {
        IPEndPoint? ipEndPoint = null;
        IWrappedSocket? socket = null;
        try
        {
            var ipAddress = await ParseIPAddress(ipOrHost);
            ipEndPoint = new IPEndPoint(ipAddress, port);
            socket = new WrappedSocket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp, _logger, socketNamePrefix);
            await socket.ConnectAsync(ipEndPoint);
            return socket;
        }
        catch (Exception e)
        {
            _logger.Error($"{e.GetType().Name} in ConnectToServer. ipOrHost: {ipOrHost}, ipEndPoint: {ipEndPoint}. {e}");
            socket?.Dispose();
            throw;
        }
    }

    public async Task ListenForConnections(int port, Func<IWrappedSocket, Task> onNewConnection)
    {
        try
        {
            using var socket = new WrappedSocket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, _logger, $"ListenOnPort-{port}");
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            socket.Listen(100);

            while (true)
            {
                var socketClient = await socket.AcceptAsync($"AcceptedOnPort-{port}");

                new Task(async () =>
                {
                    if (await CanReadFromSocket(socketClient))
                    {
                        await onNewConnection(socketClient);
                    }
                    else
                    {
                        socketClient.Close();
                    }
                }, TaskCreationOptions.LongRunning)
                .Start();
            }
        }
        catch (SocketException e)
        {
            _logger.Error($"SocketException in ListenForConnections on port {port}. {e}");
            throw;
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in ListenForConnections. {e}");
            throw;
        }
    }

    private static async Task<bool> CanReadFromSocket(IWrappedSocket socket)
    {
        var buffer = new byte[1];
        var bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.Peek, TimeSpan.FromSeconds(4));
        return bytesRead > 0;
    }

    public async Task<ReadMessageResult> ReadSocketUntilError(IWrappedSocket socket, int maxResultLen, Func<Memory<byte>, Task<ReadMessageResult>> onNewData)
    {
        try
        {
            while (true)
            {
                // _logger.Info($"Begin ReadSocket. socket handle: {socket.Handle}");
                var buffer = new byte[maxResultLen];
                var memory = new Memory<byte>(buffer);
                int received = await socket.ReceiveAsync(memory);
                if (received > 0)
                {
                    var result = await onNewData(memory[..received]);
                    if (result.ConnectionError)
                    {
                        return result;
                    }
                }
                else
                {
                    _logger.Info($"Received 0 bytes. This indicates a connection error. Socket name: {socket.Name}");
                    return new ReadMessageResult(ConnectionError: true);
                }
                // _logger.Info($"End ReadSocket. socket handle: {socket.Handle}");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Error($"OperationCanceledException in {nameof(ReadSocketUntilError)}. Socket name: {socket.Name}.");
            return new ReadMessageResult(ConnectionError: true);
        }
        catch (ObjectDisposedException e)
        {
            _logger.Error($"ObjectDisposedException in {nameof(ReadSocketUntilError)}. Socket closed. Socket name: {socket.Name}. {e}");
            return new ReadMessageResult(ConnectionError: true);
        }
        catch (SocketException e)
        {
            _logger.Error($"SocketException in {nameof(ReadSocketUntilError)}. Socket name: {socket.Name}. SocketException: {e.Message}");
            return new ReadMessageResult(ConnectionError: true);
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in {nameof(ReadSocketUntilError)}. Socket name: {socket.Name}. {e}");
            throw;
        }
    }

    private async static Task<IPAddress> ParseIPAddress(string ipOrHost)
    {
        if (IPAddress.TryParse(ipOrHost.Trim(), out var iPAddress))
        {
            return iPAddress;
        }
        var ipHostInfo = await Dns.GetHostEntryAsync(ipOrHost);
        return ipHostInfo.AddressList[0];
    }
}
