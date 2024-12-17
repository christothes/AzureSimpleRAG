using System;
using System.Net.WebSockets;
using System.Text;

namespace AzureSimpleRAG
{
    public class WebSocketAudioStream : Stream
    {
        private const int SAMPLES_PER_SECOND = 16000;
        private const int BYTES_PER_SAMPLE = 2;
        private const int CHANNELS = 1;

        // For simplicity, this is configured to use a static 10-second ring buffer.
        private readonly byte[] _buffer = new byte[BYTES_PER_SAMPLE * SAMPLES_PER_SECOND * CHANNELS * 10];
        private readonly object _bufferLock = new();
        private int _bufferReadPos = 0;
        private int _bufferWritePos = 0;
        WebSocket _webSocket;
        private bool _isRecording = false;
        //private static string filePath = @"c:\users\chriss\desktop\audio.wav";
        //private FileStream _fileStream;


        private WebSocketAudioStream(WebSocket webSocket)
        {
            _webSocket = webSocket;
            //_fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            //WriteWavHeader();
        }

        private async Task StartRecordingAsync()
        {
            _isRecording = true;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () =>
            {
                bool _isSocketClosed = false;
                WebSocketReceiveResult? receiveResult = null;
                while (_isRecording && !_isSocketClosed)
                {
                    byte[] _tmpbuffer = new byte[1024 * 16];
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
                        Array.Copy(_tmpbuffer, receiveResult.Count - bytesToCopy, _buffer, _bufferWritePos, bytesToCopy);
                        _bufferWritePos += bytesToCopy;
                    }
                    _isSocketClosed = receiveResult.CloseStatus.HasValue;
                }
                Console.WriteLine("WebSocketAudioStream detected Closed socket");
                _isRecording = false;
                if (receiveResult is not null && receiveResult.CloseStatus.HasValue && _webSocket.State == WebSocketState.Open)
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

        //private void WriteWavHeader()
        //{
        //    // WAV file header format
        //    int sampleRate = SAMPLES_PER_SECOND;
        //    short bitsPerSample = 16;
        //    short channels = CHANNELS;
        //    int byteRate = sampleRate * channels * bitsPerSample / 8;
        //    short blockAlign = (short)(channels * bitsPerSample / 8);

        //    // RIFF header
        //    _fileStream.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
        //    _fileStream.Write(BitConverter.GetBytes(0), 0, 4); // Placeholder for file size
        //    _fileStream.Write(Encoding.ASCII.GetBytes("WAVE"), 0, 4);

        //    // fmt subchunk
        //    _fileStream.Write(Encoding.ASCII.GetBytes("fmt "), 0, 4);
        //    _fileStream.Write(BitConverter.GetBytes(16), 0, 4); // Subchunk1Size (16 for PCM)
        //    _fileStream.Write(BitConverter.GetBytes((short)1), 0, 2); // AudioFormat (1 for PCM)
        //    _fileStream.Write(BitConverter.GetBytes(channels), 0, 2);
        //    _fileStream.Write(BitConverter.GetBytes(sampleRate), 0, 4);
        //    _fileStream.Write(BitConverter.GetBytes(byteRate), 0, 4);
        //    _fileStream.Write(BitConverter.GetBytes(blockAlign), 0, 2);
        //    _fileStream.Write(BitConverter.GetBytes(bitsPerSample), 0, 2);

        //    // data subchunk
        //    _fileStream.Write(Encoding.ASCII.GetBytes("data"), 0, 4);
        //    _fileStream.Write(BitConverter.GetBytes(0), 0, 4); // Placeholder for data chunk size
        //}

        //private void UpdateWavHeader()
        //{
        //    _fileStream.Seek(4, SeekOrigin.Begin);
        //    _fileStream.Write(BitConverter.GetBytes((int)(_fileStream.Length - 8)), 0, 4); // File size - 8 bytes

        //    _fileStream.Seek(40, SeekOrigin.Begin);
        //    _fileStream.Write(BitConverter.GetBytes((int)(_fileStream.Length - 44)), 0, 4); // Data chunk size
        //}

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_webSocket.State == WebSocketState.Closed)
            {
                return 0;
            }
            int totalCount = count;

            int GetBytesAvailable() => _bufferWritePos < _bufferReadPos
                ? _bufferWritePos + (_buffer.Length - _bufferReadPos)
                : _bufferWritePos - _bufferReadPos;

            // For simplicity, we'll block until all requested data is available and not perform partial reads.
            while (GetBytesAvailable() < count)
            {
                Thread.Sleep(100);
                if (_webSocket.State == WebSocketState.Closed)
                {
                    return 0;
                }
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

            // Write the read bytes to the file
            //_fileStream.Write(buffer, 0, totalCount);
            //_fileStream.Flush();



            Console.Write($".");
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
            Console.WriteLine("Disposing WebSocketAudioStream");
            //if (disposing)
            //{
            //    Console.WriteLine("DIsposing Filestream");
            //    UpdateWavHeader();
            //    _fileStream?.Dispose();
            //}
            base.Dispose(disposing);
        }
    }
}
