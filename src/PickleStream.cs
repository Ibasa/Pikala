using System;
using System.IO;
using System.Threading.Tasks;

namespace Ibasa.Pikala
{
    /// <summary>
    /// We always wrap streams with this stream which will track offset from where we started in the stream.
    /// That is Position will always work, and will always return 0 to start.
    /// </summary>
    sealed class PickleStream : Stream
    {
        readonly Stream _stream;
        long _position;

        public PickleStream(Stream stream)
        {
            _stream = stream;
            _position = 0;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _stream.CanWrite;

        public override long Length => throw new NotImplementedException("Pikala should never need to get the stream length");

        public override long Position { get => _position; set => throw new NotImplementedException("Pikala should never need to seek the stream"); }

        public override void Flush()
        {
            _stream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(new Span<byte>(buffer, offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var result = _stream.Read(buffer);
            _position += result;
            return result;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException("Pikala should never need to seek the stream");
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException("Pikala should never need to set the stream length");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(new ReadOnlySpan<byte>(buffer, offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _stream.Write(buffer);
            _position += buffer.Length;
        }
    }
}
