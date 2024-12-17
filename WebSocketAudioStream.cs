using System;
using System.Net.WebSockets;

namespace AzureSimpleRAG
{
    public class WebSocketAudioStream : Stream
    {
        private const int SAMPLES_PER_SECOND = 8000;
        private const int BYTES_PER_SAMPLE = 2;
        private const int CHANNELS = 1;

        // For simplicity, this is configured to use a static 10-second ring buffer.
        private readonly byte[] _buffer = new byte[BYTES_PER_SAMPLE * SAMPLES_PER_SECOND * CHANNELS * 10];
        private readonly object _bufferLock = new();
        private int _bufferReadPos = 0;
        private int _bufferWritePos = 0;
        WebSocket _webSocket;
        private bool _isRecording = false;


        private WebSocketAudioStream(WebSocket webSocket)
        {
            _webSocket = webSocket;
        }

        private async Task StartRecordingAsync()
        {
            _isRecording = true;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () =>
            {
                bool _isCocketClosed = false;
                WebSocketReceiveResult receiveResult = null;
                while (_isRecording && !_isCocketClosed)
                {
                    byte[] _tmpbuffer = new byte[BYTES_PER_SAMPLE * SAMPLES_PER_SECOND * CHANNELS * 1];
                    receiveResult = await _webSocket.ReceiveAsync(new ArraySegment<byte>(_tmpbuffer), CancellationToken.None);
                    lock (_bufferLock)
                    {
                        int bytesToCopy = receiveResult.Count;
                        if (_bufferWritePos + bytesToCopy >= _buffer.Length)
                        {
                            int bytesToCopyBeforeWrap = _buffer.Length - _bufferWritePos;
                            Array.Copy(_tmpbuffer, 0, _buffer, _bufferWritePos, bytesToCopyBeforeWrap);
                            bytesToCopy -= bytesToCopyBeforeWrap;
                            _bufferWritePos = 0;
                        }
                        Array.Copy(_tmpbuffer, 0, _buffer, _bufferWritePos, bytesToCopy);
                        _bufferWritePos += bytesToCopy;
                    }
                    _isCocketClosed = receiveResult.CloseStatus.HasValue;
                }
                Console.WriteLine("Closed socket");
                if (receiveResult is not null && receiveResult.CloseStatus.HasValue)
                    await _webSocket.CloseAsync(
            receiveResult.CloseStatus.Value,
        receiveResult.CloseStatusDescription,
        CancellationToken.None);
            });
        }

        public static async Task<WebSocketAudioStream> StartAsync(WebSocket webSocket)
        {
            var stream = new WebSocketAudioStream(webSocket);
            await stream.StartRecordingAsync();
            return stream;

        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalCount = count;

            int GetBytesAvailable() => _bufferWritePos < _bufferReadPos
                ? _bufferWritePos + (_buffer.Length - _bufferReadPos)
                : _bufferWritePos - _bufferReadPos;

            // For simplicity, we'll block until all requested data is available and not perform partial reads.
            while (GetBytesAvailable() < count)
            {
                Thread.Sleep(100);
            }

            lock (_bufferLock)
            {
                if (_bufferReadPos + count >= _buffer.Length)
                {
                    int bytesBeforeWrap = _buffer.Length - _bufferReadPos;
                    Array.Copy(
                        sourceArray: _buffer,
                        sourceIndex: _bufferReadPos,
                        destinationArray: buffer,
                        destinationIndex: offset,
                        length: bytesBeforeWrap);
                    _bufferReadPos = 0;
                    count -= bytesBeforeWrap;
                    offset += bytesBeforeWrap;
                }

                Array.Copy(_buffer, _bufferReadPos, buffer, offset, count);
                _bufferReadPos += count;
            }
            Console.Write('.');
            return totalCount;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
