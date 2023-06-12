using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace proxy_common;

public interface ITcpProxy
{
    Task<ReadMessageResult> ReadMessage(Socket socket, Func<ReadOnlyMemory<byte>, int, Task> onNewMessage);
    Task WriteMessage(Socket socket, int connectionId, ReadOnlyMemory<byte> message);
    Task SendKeepAlivePingInBckground(Socket socket);
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
    public const int WrappedDataMessageMaxLen = 4_096 - DataMessageHeaderLen;
    public const string MessageTypePing = "PING";

    private int _sequenceNumber = 0;

    private readonly ILogger _logger;
    private readonly ISocketHelper _socketHelper;

    public TcpProxy(ILogger logger, ISocketHelper socketHelper)
    {
        _logger = logger;
        _socketHelper = socketHelper;
    }

    public static int PingMessageTotalLen => PingMessageHeaderLen + MessageTypePing.Length;

    public async Task WriteMessage(Socket socket, int connectionId, ReadOnlyMemory<byte> message)
    {
        try
        {
            if (message.Length > 4_096 - DataMessageHeaderLen)
            {
                throw new Exception($"message length {message.Length} must not be larger than 4_096 - {DataMessageHeaderLen}");
            }
            int messageLenIncludingHeader = message.Length + DataMessageHeaderLen;
            var sequenceNumber = NextSequenceNumber();
            var header = Encoding.UTF8.GetBytes($"{messageLenIncludingHeader,4}DATA{sequenceNumber,4}{connectionId,4}");
            _logger.Info($"Sending DATA message to proxy. messageLenIncludingHeader: {messageLenIncludingHeader}, sequenceNumber: {sequenceNumber}, connectionId: {connectionId}");
            var headerAndMessage = new Memory<byte>(new byte[header.Length + message.Length]);
            header.CopyTo(headerAndMessage);
            message.CopyTo(headerAndMessage[header.Length..]);
            await socket.SendAsync(headerAndMessage);
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in {nameof(WriteMessage)}: {e}");
        }
    }

    private static int? MessageLengthInclHeader(ReadOnlySequence<byte> bufferSeqStartingAtMessage)
        => bufferSeqStartingAtMessage.Length < 4 ? null : int.Parse(Encoding.UTF8.GetString(bufferSeqStartingAtMessage));

    record PartialMessageFromPreviousCallback(Memory<byte> PartialData, int PayloadLength, int ConnectionId, int SequenceNumber);



    public async Task<ReadMessageResult> ReadMessage(Socket socket, Func<ReadOnlyMemory<byte>, int, Task> onNewMessage)
    {
        return await Task.Run(() =>
        {
            var partialDataFromPreviousCallback = Memory<byte>.Empty;

            return _socketHelper.ReadSocket(socket, 4_096, (async (buffer) =>
            {
                try
                {
                    var enrichedBuffer = buffer;
                    if (partialDataFromPreviousCallback.IsEmpty is false)
                    {
                        enrichedBuffer = new Memory<byte>(new byte[partialDataFromPreviousCallback.Length + buffer.Length]);
                        partialDataFromPreviousCallback.CopyTo(enrichedBuffer);
                        buffer.CopyTo(enrichedBuffer[partialDataFromPreviousCallback.Length..]);
                    }

                    var bufferSeq = new ReadOnlySequence<byte>(enrichedBuffer);
                    int messageStartIndexInBuffer = 0;

                    while (enrichedBuffer.Length > messageStartIndexInBuffer)
                    {
                        int remainingBytesInBuffer = enrichedBuffer.Length - messageStartIndexInBuffer;
                        int? messageLengthInclHeader = MessageLengthInclHeader(bufferSeq.Slice(messageStartIndexInBuffer, 4));
                        _logger.Info($"remainingBytesInBuffer: {remainingBytesInBuffer}, messageStartIndexInBuffer: {messageStartIndexInBuffer}, messageLengthInclHeader: {messageLengthInclHeader}");
                        if (remainingBytesInBuffer < 4 || remainingBytesInBuffer < messageLengthInclHeader!.Value)
                        {
                            partialDataFromPreviousCallback = enrichedBuffer.Slice(messageStartIndexInBuffer, remainingBytesInBuffer);
                            break;
                        }
                        var dataToProcess = enrichedBuffer.Slice(messageStartIndexInBuffer, messageLengthInclHeader!.Value);
                        var result = await ProcessCompleteMessage(socket.Handle, onNewMessage, dataToProcess);
                        if (result.ConnectionError)
                        {
                            return new ReadMessageResult(ConnectionError: true);
                        }
                        messageStartIndexInBuffer = messageLengthInclHeader!.Value;
                    }
                    return new ReadMessageResult(ConnectionError: false);
                }
                catch (Exception e)
                {
                    if (e is SocketException se && se.ErrorCode == 32) // Broken pipe
                    {
                        _logger.Error($"SocketException with ErrorCode Broken pipe. socket handle: {socket.Handle}");
                        return new ReadMessageResult(ConnectionError: true);
                    }
                    var bufferSeq = new ReadOnlySequence<byte>(buffer);
                    var bufferData = Encoding.UTF8.GetString(bufferSeq);
                    _logger.Error($"Exception in {nameof(ReadMessage)}. bufferSeq.Length: {bufferSeq.Length}. {e}\n\nbuffer:{bufferData}\n\n");

                    await Task.Delay(TimeSpan.FromSeconds(2)); // Some time to prevent too many log messages.
                    return new ReadMessageResult(ConnectionError: false);
                }
            }));
        });
    }

    private async Task<ReadMessageResult> ProcessCompleteMessage(
        nint socketHandleForLogging,
        Func<ReadOnlyMemory<byte>, int, Task> onNewMessage,
        Memory<byte> buffer)
    {
        var bufferSeq = new ReadOnlySequence<byte>(buffer);
        var messageLengthInclHeader = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(0, 4)));
        var messageType = Encoding.UTF8.GetString(bufferSeq.Slice(4, 4));
        _logger.Info($"bufferSeq.Length: {bufferSeq.Length}, messageLengthInclHeader: {messageLengthInclHeader}, messageType: {messageType}");

        if (messageType == MessageTypePing)
        {
            _logger.Info($"Received {MessageTypePing}. socket handle: {socketHandleForLogging}");
        }
        else if (messageType == "DATA")
        {
            var payloadLen = messageLengthInclHeader - DataMessageHeaderLen;
            var sequenceNumber = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(8, 4)));
            var connectionId = int.Parse(Encoding.UTF8.GetString(bufferSeq.Slice(12, 4)));

            _logger.Info($"Received message socket handle: {socketHandleForLogging}, payloadLen: {payloadLen}, sequenceNumber: {sequenceNumber}, connectionId: {connectionId}");

            if (messageLengthInclHeader != buffer.Length)
            {
                throw new Exception("messageLengthInclHeader != buffer.Length. Should never get here");
            }
            var messageData = buffer.Slice(DataMessageHeaderLen, payloadLen);
            await onNewMessage(messageData, connectionId);
        }
        return new ReadMessageResult(ConnectionError: false);
    }

    public Task SendKeepAlivePingInBckground(Socket socket)
    {
        return Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                await WritePingMessage(socket);
            }
        });
    }

    private async Task WritePingMessage(Socket socket)
    {
        try
        {
            var header = Encoding.UTF8.GetBytes($"   {PingMessageTotalLen}{MessageTypePing}");
            await socket.SendAsync(header);
        }
        catch (Exception e)
        {
            _logger.Error($"Exception in {nameof(WritePingMessage)}: {e}");
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
