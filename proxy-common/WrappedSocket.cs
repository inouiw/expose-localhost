using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace proxy_common;

public interface IWrappedSocket : IDisposable
{
    ValueTask<int> SendAsync(Memory<byte> buffer);
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer);
    ValueTask<int> ReceiveAsync(byte[] buffer, TimeSpan timeout);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketFlags socketFlags, TimeSpan timeout);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer);
    ValueTask ConnectAsync(EndPoint remoteEP);
    void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, bool optionValue);
    void Bind(EndPoint localEP);
    void Listen(int backlog);
    Task<IWrappedSocket> AcceptAsync(string socketNamePrefix);
    nint Handle { get; }
    void Close([CallerMemberName] string? caller = null);
    IPAddress? RemoteAddress { get; }
    int? RemotePort { get; }
    string Name { get; }
    bool IsClosed { get; }
}

public class WrappedSocket : IWrappedSocket, IDisposable
{
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly SemaphoreSlim _sendMutex = new(1);
    private readonly SemaphoreSlim _receiveMutex = new(1);
    private readonly CancellationTokenSource cancellationTokenSource = new();

    public static List<WrappedSocket> OpenSockets = new();

    public WrappedSocket(Socket socket, ILogger logger, string socketNamePrefix)
    {
        _socket = socket;
        _logger = logger;
        _name = $"{socketNamePrefix}_Handle-{_socket.Handle}";
        OpenSockets.Add(this);
    }

    public WrappedSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, ILogger logger, string socketNamePrefix)
    {
        _socket = new Socket(addressFamily, socketType, protocolType);
        _logger = logger;
        _name = $"{socketNamePrefix}_Handle-{_socket.Handle}";
        OpenSockets.Add(this);
    }

    public async ValueTask<int> SendAsync(Memory<byte> buffer)
    {
        await _sendMutex.WaitAsync();
        try
        {
            return await _socket.SendAsync(buffer, cancellationTokenSource.Token);
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer)
    {
        await _sendMutex.WaitAsync();
        try
        {
            return await _socket.SendAsync(buffer, cancellationTokenSource.Token);
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    public async ValueTask<int> ReceiveAsync(byte[] buffer, TimeSpan timeout)
    {
        await _receiveMutex.WaitAsync();
        try
        {
            var cts = new CancellationTokenSource(timeout);
            return await _socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            _receiveMutex.Release();
        }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketFlags socketFlags, TimeSpan timeout)
    {
        await _receiveMutex.WaitAsync();
        try
        {
            var cts = new CancellationTokenSource(timeout);
            return await _socket.ReceiveAsync(buffer, socketFlags, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            _receiveMutex.Release();
        }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer)
    {
        await _receiveMutex.WaitAsync();
        try
        {
            return await _socket.ReceiveAsync(buffer, cancellationTokenSource.Token);
        }
        finally
        {
            _receiveMutex.Release();
        }
    }

    public ValueTask ConnectAsync(EndPoint remoteEP)
    {
        return _socket.ConnectAsync(remoteEP, cancellationTokenSource.Token);
    }

    public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, bool optionValue)
    {
        _socket.SetSocketOption(optionLevel, optionName, optionValue);
    }

    public void Bind(EndPoint localEP)
    {
        _socket.Bind(localEP);
    }

    public void Listen(int backlog)
    {
        _socket.Listen(backlog);
    }

    public async Task<IWrappedSocket> AcceptAsync(string socketNamePrefix)
    {
        var socket = await _socket.AcceptAsync();
        return new WrappedSocket(socket, _logger, socketNamePrefix);
    }

    public nint Handle => _socket.Handle;
    public bool IsClosed { get; private set; } = false;

    public void Close([CallerMemberName] string? caller = null)
    {
        _logger.Info($"Closing socket. Name: {Name}, Caller: {caller}");
        cancellationTokenSource.Cancel();
        _socket.Close();
        OpenSockets.Remove(this);
        IsClosed = true;
    }

    public void Dispose()
    {
        _logger.Info($"Disposing socket. Name: {Name}");
        _socket.Dispose();
        GC.SuppressFinalize(this);
    }

    public IPAddress? RemoteAddress => (_socket.RemoteEndPoint as IPEndPoint)?.Address;
    public int? RemotePort => (_socket.RemoteEndPoint as IPEndPoint)?.Port;
    public string Name => _name;
}