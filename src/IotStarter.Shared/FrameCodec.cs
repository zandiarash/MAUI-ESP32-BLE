// Shared between firmware and app. Conservative C# subset only.
#nullable disable

namespace IotStarter.Shared
{
    /// <summary>
    /// Splits messages into numbered chunks that fit a GATT write or notification.
    /// Chunk layout: [hdr][msgId][data...], hdr = END flag (bit 7) | index (bits 0..6).
    /// </summary>
    public static class FrameSplitter
    {
        /// <summary>
        /// Splits <paramref name="message"/> into chunks of at most <paramref name="chunkSize"/> bytes.
        /// Returns null if the message is empty, too large, or the chunk size is out of range.
        /// </summary>
        public static byte[][] Split(byte[] message, byte messageId, int chunkSize)
        {
            if (message == null || message.Length == 0 || message.Length > ProtocolConstants.MaxMessageSize)
            {
                return null;
            }

            if (chunkSize < ProtocolConstants.DefaultChunkSize || chunkSize > ProtocolConstants.MaxChunkSize)
            {
                return null;
            }

            int dataPerChunk = chunkSize - ProtocolConstants.ChunkHeaderSize;
            int count = (message.Length + dataPerChunk - 1) / dataPerChunk;
            if (count > ProtocolConstants.ChunkIndexMask + 1)
            {
                return null;
            }

            byte[][] chunks = new byte[count][];
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                int length = message.Length - offset;
                if (length > dataPerChunk)
                {
                    length = dataPerChunk;
                }

                byte[] chunk = new byte[ProtocolConstants.ChunkHeaderSize + length];
                byte header = (byte)i;
                if (i == count - 1)
                {
                    header |= ProtocolConstants.ChunkEndFlag;
                }

                chunk[0] = header;
                chunk[1] = messageId;
                ByteHelpers.Copy(message, offset, chunk, ProtocolConstants.ChunkHeaderSize, length);
                chunks[i] = chunk;
                offset += length;
            }

            return chunks;
        }
    }

    /// <summary>
    /// Reassembles chunks into messages. Rejects out-of-order, oversized, stale or foreign chunks
    /// without throwing. Not thread-safe: callers serialise access.
    /// </summary>
    public class FrameAssembler
    {
        private readonly byte[] _buffer = new byte[ProtocolConstants.MaxMessageSize];
        private int _length;
        private int _expectedIndex;
        private int _messageId = -1;
        private long _lastChunkAtMs;
        private byte[] _completed;
        private ErrorCode _lastError = ErrorCode.None;

        /// <summary>Error that caused the last <see cref="FrameStatus.Rejected"/>.</summary>
        public ErrorCode LastError
        {
            get { return _lastError; }
        }

        /// <summary>Message id of the message being assembled or just completed (-1 if none).</summary>
        public int MessageId
        {
            get { return _messageId; }
        }

        /// <summary>
        /// Feeds one chunk. <paramref name="nowMs"/> is a monotonic millisecond clock supplied by the caller.
        /// </summary>
        public FrameStatus Accept(byte[] chunk, long nowMs)
        {
            _completed = null;

            if (chunk == null || chunk.Length < ProtocolConstants.ChunkHeaderSize + 1)
            {
                return Reject(ErrorCode.InvalidFrame);
            }

            int index = chunk[0] & ProtocolConstants.ChunkIndexMask;
            bool isLast = (chunk[0] & ProtocolConstants.ChunkEndFlag) != 0;
            int id = chunk[1];

            if (_expectedIndex > 0 && nowMs - _lastChunkAtMs > ProtocolConstants.ReassemblyTimeoutMs)
            {
                // The previous message went stale; start over with this chunk.
                Reset();
            }

            if (index == 0)
            {
                // A new message always restarts assembly, even if one was in progress.
                Reset();
                _messageId = id;
            }
            else if (index != _expectedIndex || id != _messageId)
            {
                return Reject(ErrorCode.InvalidFrame);
            }

            int dataLength = chunk.Length - ProtocolConstants.ChunkHeaderSize;
            if (_length + dataLength > ProtocolConstants.MaxMessageSize)
            {
                return Reject(ErrorCode.InvalidPayload);
            }

            ByteHelpers.Copy(chunk, ProtocolConstants.ChunkHeaderSize, _buffer, _length, dataLength);
            _length += dataLength;
            _expectedIndex = index + 1;
            _lastChunkAtMs = nowMs;

            if (!isLast)
            {
                if (_expectedIndex > ProtocolConstants.ChunkIndexMask)
                {
                    return Reject(ErrorCode.InvalidFrame);
                }

                return FrameStatus.InProgress;
            }

            _completed = ByteHelpers.Slice(_buffer, 0, _length);
            int completedId = _messageId;
            Reset();
            _messageId = completedId;
            _lastError = ErrorCode.None;
            return FrameStatus.Complete;
        }

        /// <summary>Returns the message completed by the last <see cref="Accept"/> call, once.</summary>
        public byte[] TakeMessage()
        {
            byte[] message = _completed;
            _completed = null;
            return message;
        }

        /// <summary>Discards any partial message.</summary>
        public void Reset()
        {
            // Clear the buffer so secrets do not linger in RAM longer than necessary.
            for (int i = 0; i < _length; i++)
            {
                _buffer[i] = 0;
            }

            _length = 0;
            _expectedIndex = 0;
            _messageId = -1;
        }

        private FrameStatus Reject(ErrorCode error)
        {
            Reset();
            _lastError = error;
            return FrameStatus.Rejected;
        }
    }
}
