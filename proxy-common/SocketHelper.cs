using System.Net;
using System.Net.Sockets;

namespace proxy_common;

public static class SocketHelper
{
    public async static Task<Socket> ConnectToServer(string ipOrHost, int port)
    {
        var ipAddress = await ParseIPAddress(ipOrHost);
        var ipEndPoint = new IPEndPoint(ipAddress, port);
        var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(ipOrHost, port);
        return socket;
    }

    public async static Task ListenForConnections(int port, Func<Socket, Task> onNewConnection)
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        socket.Listen(100);

        try
        {
            while (true)
            {
                var socketClient = await socket.AcceptAsync();
                await onNewConnection(socketClient);
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine($"SocketException in ListenForConnections on port {port}: {e}");
            throw;
        }
    }

    public async static Task ReadSocket(Socket socket, int maxResultLen, Func<Memory<byte>, Task> onNewData)
    {
        try
        {
            while (true)
            {
                var buffer = new byte[maxResultLen];
                var memory = new Memory<byte>(buffer);
                int received = await socket.ReceiveAsync(memory);
                if (received > 0)
                {
                    await onNewData(memory[..received]);
                }
                else
                {
                    Console.WriteLine($"received 0 bytes. Connected: {socket.Connected}");
                    break;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            Console.WriteLine($"ObjectDisposedException in ReadSocket. Socket closed.");
            throw;
        }
        catch (SocketException e)
        {
            Console.WriteLine($"SocketException in ReadSocket. SocketException: {e}");
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
