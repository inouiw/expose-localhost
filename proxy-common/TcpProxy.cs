using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace proxy_common;

public interface ITcpProxy
{
    Task<ReadMessageResult> ReadMessage(IWrappedSocket socket, Func<ReadOnlyMemory<byte>, int, Task> onNewMessage);
    Task<int> WriteMessage(IWrappedSocket socket, int connectionId, ReadOnlyMemory<byte> message);
    Task SendKeepAlivePingInBckground(IWrappedSocket socket);
    Task WriteClientHelloMessage(IWrappedSocket socket);
    Task<bool> TryReadClientHelloMessage(IWrappedSocket socket);
}

// Protocol between proxy-client and proxy-server:
// - Each message starts with a header.
// - The first 4 bytes of the header is the length of the message including the header. The value must not be larger than 4_096. The value is encoded as UTF8 string.
// - The following 4 bytes of the header is the type of message. Supported are "PING" and "DATA".
// - Specific to type "DATA"
//   - The following 4 bytes of the header is a sequence number encoded as a UTF8 string. Since it is a 4 byte string the max value is 9999, then it wraps around to 0.
//   - The following 4 bytes of the header is the connection-id the data refers to encoded as a UTF8 string.
// - Specific to type "PING"
//   - No additional data.

public class TcpProxy : ITcpProxy
{
    public const int DataMessageHeaderLen = 4 + 4 + 4 + 4;
    public const int PingMessageHeaderLen = 4;
    public const int ClientHelloMessageHeaderLen = 4;
    public const int WrappedDataMessageMaxLen = 4_096 - DataMessageHeaderLen;
    public const string MessageTypeData = "DATA";
    public const string MessageTypePing = "PING";
    public const string MessageTypeClientHello = "CHEL";

    private int _sequenceNumber = 0;

    private readonly ILogger _logger;
    private readonly ISocketHelper _socketHelper;

    public TcpProxy(ILogger logger, ISocketHelper socketHelper)
    {
        _logger = logger;
        _socketHelper = socketHelper;
    }

    public static int PingMessageTotalLen => PingMessageHeaderLen + MessageTypePing.Length;
    public static int ClientHelloMessageTotalLen => PingMessageHeaderLen + MessageTypeClientHello.Length;

    public async Task<int> WriteMessage(IWrappedSocket socket, int connectionId, ReadOnlyMemory<byte> message)
    {
        try
        {
            if (message.Length > 4_096 - DataMessageHeaderLen)
            {
                throw new Exception($"message length {message.Length} must not be larger than 4_096 - {DataMessageHeaderLen}");
            }
            int messageLenIncludingHeader = message.Length + DataMessageHeaderLen;
            var sequenceNumber = NextSequenceNumber();
            var header = Encoding.UTF8.GetBytes($"{messageLenIncludingHeader,4}{MessageTypeData}{sequenceNumber,4}{connectionId,4}");
            _logger.Info($"Sending DATA message. socket name: {socket.Name}. messageLenIncludingHeader: {messageLenIncludingHeader}, sequenceNumber: {sequenceNumber}, connectionId: {connectionId}");
            var headerAndMessage = new Memory<byte>(new byte[header.Length + message.Length]);
            header.CopyTo(headerAndMessage);
            message.CopyTo(headerAndMessage[header.Length..]);
            return await socket.SendAsync(headerAndMessage);
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in {nameof(WriteMessage)}: {e}");
            return 0;
        }
    }

    public async Task<bool> TryReadClientHelloMessage(IWrappedSocket socket)
    {
        var buffer = new byte[ClientHelloMessageTotalLen];
        var bytesRead = await socket.ReceiveAsync(buffer, TimeSpan.FromSeconds(4));

        if (bytesRead == ClientHelloMessageTotalLen
            && Encoding.UTF8.GetString(buffer) == $"   {ClientHelloMessageTotalLen}{MessageTypeClientHello}")
        {
            return true;
        }
        return false;
    }

    private static int? MessageLengthInclHeader(ReadOnlySequence<byte> bufferSeqStartingAtMessage)
        => bufferSeqStartingAtMessage.Length < 4 ? null : int.Parse(Encoding.UTF8.GetString(bufferSeqStartingAtMessage));

    public async Task<ReadMessageResult> ReadMessage(IWrappedSocket socket, Func<ReadOnlyMemory<byte>, int, Task> onNewMessage)
    {
        var partialDataFromPreviousCallback = Memory<byte>.Empty;

        var x = await _socketHelper.ReadSocketUntilError(socket, 4_096, (async (buffer) =>
        {
            int messageStartIndexInBuffer = 0;
            var enrichedBuffer = buffer;
            try
            {
                if (partialDataFromPreviousCallback.IsEmpty is false)
                {
                    enrichedBuffer = new Memory<byte>(new byte[partialDataFromPreviousCallback.Length + buffer.Length]);
                    partialDataFromPreviousCallback.CopyTo(enrichedBuffer);
                    buffer.CopyTo(enrichedBuffer[partialDataFromPreviousCallback.Length..]);
                    partialDataFromPreviousCallback = Memory<byte>.Empty;
                }

                var bufferSeq = new ReadOnlySequence<byte>(enrichedBuffer);

                while (enrichedBuffer.Length > messageStartIndexInBuffer)
                {
                    int remainingBytesInBuffer = enrichedBuffer.Length - messageStartIndexInBuffer;
                    int? messageLengthInclHeader = MessageLengthInclHeader(bufferSeq.Slice(messageStartIndexInBuffer, 4));
                    // _logger.Info($"remainingBytesInBuffer: {remainingBytesInBuffer}, messageStartIndexInBuffer: {messageStartIndexInBuffer}, messageLengthInclHeader: {messageLengthInclHeader}");
                    if (remainingBytesInBuffer < 4 || remainingBytesInBuffer < messageLengthInclHeader!.Value)
                    {
                        partialDataFromPreviousCallback = enrichedBuffer.Slice(messageStartIndexInBuffer, remainingBytesInBuffer);
                        break;
                    }
                    var dataToProcess = enrichedBuffer.Slice(messageStartIndexInBuffer, messageLengthInclHeader!.Value);
                    await ProcessCompleteMessage(socket.Name, onNewMessage, dataToProcess);

                    messageStartIndexInBuffer += messageLengthInclHeader!.Value;
                }
                return new ReadMessageResult(ConnectionError: false);
            }
            catch (OperationCanceledException)
            {
                _logger.Error($"OperationCanceledException in ReadMessage. Socket name: {socket.Name}.");
                return new ReadMessageResult(ConnectionError: true);
            }
            catch (ObjectDisposedException e)
            {
                _logger.Error($"ObjectDisposedException in ReadMessage. Socket closed. Socket name: {socket.Name}. {e}");
                return new ReadMessageResult(ConnectionError: true);
            }
            catch (SocketException e)
            {
                _logger.Error($"SocketException in ReadMessage. Socket name: {socket.Name}. SocketException: {e.Message}");
                return new ReadMessageResult(ConnectionError: true);
            }
            catch (Exception e)
            {
                if (e is SocketException se && se.ErrorCode == 32) // Broken pipe
                {
                    _logger.Error($"SocketException with ErrorCode Broken pipe. Socket name: {socket.Name}");
                    return new ReadMessageResult(ConnectionError: true);
                }
                var bufferSeq = new ReadOnlySequence<byte>(enrichedBuffer);
                var bufferData = Encoding.UTF8.GetString(bufferSeq);
                _logger.Error($"Exception in {nameof(ReadMessage)}. Socket name: {socket.Name}, messageStartIndexInBuffer: {messageStartIndexInBuffer}, bufferSeq.Length: {bufferSeq.Length}. {e}\n\nbuffer:{bufferData}\n\n");

                await Task.Delay(TimeSpan.FromSeconds(2)); // Some time to prevent too many log messages.
                return new ReadMessageResult(ConnectionError: false);
            }
        }));
        // socket.Close();
        return x;
    }

    private async Task ProcessCompleteMessage(
        string socketNameForLogging,
        Func<ReadOnlyMemory<byte>, int, Task> onNewMessage,
        Memory<byte> buffer)
    {
        var bufferSeq = new ReadOnlySequence<byte>(buffer);
        var messageLengthInclHeader = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(0, 4)));
        var messageType = Encoding.UTF8.GetString(bufferSeq.Slice(4, 4));
        // _logger.Info($"bufferSeq.Length: {bufferSeq.Length}, messageLengthInclHeader: {messageLengthInclHeader}, messageType: {messageType}");

        if (messageType == MessageTypePing)
        {
            _logger.Info($"Received {MessageTypePing}. socket name: {socketNameForLogging}");
            _logger.Info($"Number of open sockets: {WrappedSocket.OpenSockets.Count}");
        }
        else if (messageType == MessageTypeData)
        {
            var payloadLen = messageLengthInclHeader - DataMessageHeaderLen;
            var sequenceNumber = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(8, 4)));
            var connectionId = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(12, 4)));

            _logger.Info($"Received {MessageTypeData} message. socket name: {socketNameForLogging}, payloadLen: {payloadLen}, sequenceNumber: {sequenceNumber}, connectionId: {connectionId}");

            if (messageLengthInclHeader != buffer.Length)
            {
                throw new Exception("messageLengthInclHeader != buffer.Length. Should never get here");
            }
            var messageData = buffer.Slice(DataMessageHeaderLen, payloadLen);
            await onNewMessage(messageData, connectionId);
        }
    }

    public Task SendKeepAlivePingInBckground(IWrappedSocket socket)
    {
        var task = new Task(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                await WritePingMessage(socket);
            }
        }, TaskCreationOptions.LongRunning);
        task.Start();
        return task;
    }

    private async Task WritePingMessage(IWrappedSocket socket)
    {
        try
        {
            _logger.Info($"Sending keep alive {MessageTypePing} message to proxy");
            var header = Encoding.UTF8.GetBytes($"   {PingMessageTotalLen}{MessageTypePing}");
            await socket.SendAsync(header);
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in {nameof(WritePingMessage)}: {e}");
        }
    }

    // Anyone could connect to the server. By sending a message after connecting we can identify most fake connections.
    public async Task WriteClientHelloMessage(IWrappedSocket socket)
    {
        try
        {
            _logger.Info($"Sending client hello ({MessageTypeClientHello}) message to proxy");
            var header = Encoding.UTF8.GetBytes($"   {ClientHelloMessageTotalLen}{MessageTypeClientHello}");
            await socket.SendAsync(header);
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in {nameof(WriteClientHelloMessage)}: {e}");
        }
    }

    private int NextSequenceNumber()
    {
        if (++_sequenceNumber > 9999)
        {
            _sequenceNumber = 0;
        }
        return _sequenceNumber;
    }
}
