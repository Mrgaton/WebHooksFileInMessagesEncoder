using DSFiles_Shared.Properties;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Aes = System.Security.Cryptography.Aes;

namespace DSFiles_Shared
{
    public class AesCTRStream : Stream
    {
        private byte[] TransformationKey = Resources.bin;

        private const int BlockSize = 16;
        private const int CounterSize = sizeof(ulong);

        private readonly byte[] Key;

        private readonly byte[] Nonce;
        private readonly int _nonceOffsetInBlock;

        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptEcb;

        private readonly Stream _baseStream;

        public AesCTRStream(Stream? baseStream, byte[]? subKey = null)
        {
            if (baseStream != null)
            {
                _baseStream = baseStream;
            }

            if (subKey != null)
            {
                //int hashSize = 256;
                //var derivedKey = HKDF.Expand(HashAlgorithmName.SHA256, SHA512.HashData(subKey), (hashSize / 8) * 255);

                Key = new HMACSHA256(SHA512.HashData(subKey)).ComputeHash(TransformationKey);

                Nonce = (new HMACMD5(TransformationKey).ComputeHash(Key));
                _nonceOffsetInBlock = CounterSize;
                Buffer.BlockCopy(Nonce, 8, _counterBlock, _nonceOffsetInBlock, BlockSize - CounterSize);

                _aes = AesCng.Create();
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;
                _aes.Key = Key;
                _encryptEcb = _aes.CreateEncryptor();
            }
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position { get; set; } = 0;

        public override void Flush()
        {
            _baseStream.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition;

            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPosition = offset;
                    break;

                case SeekOrigin.Current:
                    newPosition = this.Position + offset;
                    break;

                case SeekOrigin.End:
                    newPosition = this.Length + offset;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (newPosition < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");

            return this.Position = _baseStream.Seek(newPosition, SeekOrigin.Begin);
        }

        public override void SetLength(long value)
        {
            _baseStream.SetLength(value);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).Result;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) // Added CancellationToken
        {
            long currentStreamPos = this.Position; // Capture position before read

            int bytesRead = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);

            if (bytesRead > 0)
            {
                Transform(buffer.AsSpan(offset, bytesRead), currentStreamPos);
                this.Position += bytesRead;
            }
            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count) => this.WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public new async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Transform(buffer, this.Position);
            await _baseStream.WriteAsync(buffer, offset, count, cancellationToken);
            this.Position += count;
        }

        public void Encode(Span<byte> bufferSegment, int count)
        {
            Transform(bufferSegment, this.Position);

            this.Position += count;
        }

        private readonly byte[] _counterBlock = new byte[BlockSize];
        private readonly byte[] _keystream = new byte[BlockSize];

        private readonly byte[] _keystream32 = new byte[2 * BlockSize];

        public void Transform(Span<byte> buffer, long position)
        {
            int length = buffer.Length;
            if (length == 0) return;

            int offsetInBlock = (int)(position & (BlockSize - 1));
            ulong counterValue = (ulong)(position >> 4);
            int processedBytes = 0;

            if (offsetInBlock != 0)
            {
                WriteCounterToBlock(counterValue);
                _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                int bytesToXor = Math.Min(BlockSize - offsetInBlock, length);

                XorBytes(buffer.Slice(0, bytesToXor),
                         _keystream.AsSpan().Slice(offsetInBlock, bytesToXor));

                processedBytes += bytesToXor;
                counterValue++;
            }

            int remainingLength = length - processedBytes;
            int fullBlocks = remainingLength / BlockSize;

            if (fullBlocks > 0)
            {
                if (Avx2.IsSupported)
                {
                    int numAvxPairs = fullBlocks / 2;

                    if (numAvxPairs > 0)
                    {
                        for (int i = 0; i < numAvxPairs; i++)
                        {
                            WriteCounterToBlock(counterValue);
                            _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream32, 0);
                            WriteCounterToBlock(counterValue + 1);
                            _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream32, BlockSize);

                            Span<byte> dstSlice = buffer.Slice(processedBytes, 2 * BlockSize);

                            ref byte dstRef = ref MemoryMarshal.GetReference<byte>(dstSlice);

                            ref byte ksRef = ref MemoryMarshal.GetReference<byte>(_keystream32);

                            var ksVec = Unsafe.ReadUnaligned<Vector256<byte>>(ref ksRef);
                            var dataVec = Unsafe.ReadUnaligned<Vector256<byte>>(ref dstRef);

                            var resultVec = Avx2.Xor(ksVec, dataVec);

                            Unsafe.WriteUnaligned(ref dstRef, resultVec);

                            processedBytes += 2 * BlockSize;
                            counterValue += 2;
                        }

                        fullBlocks -= numAvxPairs * 2;
                    }
                }

                for (int i = 0; i < fullBlocks; i++)
                {
                    WriteCounterToBlock(counterValue);

                    _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                    Span<byte> targetSlice = buffer.Slice(processedBytes, BlockSize);

                    ReadOnlySpan<ulong> ksU = MemoryMarshal.Cast<byte, ulong>(_keystream);
                    Span<ulong> dstU = MemoryMarshal.Cast<byte, ulong>(targetSlice);
                    for (int j = 0; j < dstU.Length; j++)
                        dstU[j] ^= ksU[j];

                    processedBytes += BlockSize;
                    counterValue++;
                }
            }

            if (processedBytes < length)
            {
                WriteCounterToBlock(counterValue);
                _encryptEcb.TransformBlock(_counterBlock, 0, BlockSize, _keystream, 0);

                int bytesRemaining = length - processedBytes;
                XorBytes(buffer.Slice(processedBytes, bytesRemaining),
                         _keystream.AsSpan().Slice(0, bytesRemaining));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteCounterToBlock(ulong counter)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_counterBlock, counter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void XorBytes(Span<byte> target, ReadOnlySpan<byte> source)
        {
            for (int i = 0; i < target.Length; i++)
            {
                target[i] ^= source[i];
            }
        }

        private bool _disposed = false;

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _encryptEcb?.Dispose();
                    _aes?.Dispose();
                    _baseStream?.Dispose();
                }
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}