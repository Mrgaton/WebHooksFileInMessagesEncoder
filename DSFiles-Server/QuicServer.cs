using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.TagHelpers.Cache;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace DSFiles_Server
{
    internal class QuicServer
    {
        private const ushort CurrentVersion = 50002;
        private const int MaxQueuedPacketsPerClient = 128;
        private const int SoftQueuedPacketsPerClient = 48;
        private const int HardQueuedPacketsPerClient = 96;
        private const long MaxQueuedBytesPerClient = 8L * 1024 * 1024;
        private const long SoftQueuedBytesPerClient = 2L * 1024 * 1024;
        private const long HardQueuedBytesPerClient = 6L * 1024 * 1024;
        private const int InboundFrameHeaderSize = sizeof(ushort) + sizeof(byte) + sizeof(int);
        private const int OutboundFrameHeaderSize = sizeof(ushort) + sizeof(ulong) + sizeof(byte) + sizeof(int);
        private const int MaxTargetBytesPerFrame = byte.MaxValue * sizeof(ulong);

        private static readonly TimeSpan QueueWaitInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan BottleneckDisconnectGracePeriod = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan AbsoluteDisconnectGracePeriod = TimeSpan.FromSeconds(20);

        private static readonly ConcurrentDictionary<UInt128, ConcurrentDictionary<ulong, Client>> _connections = new();

        public class Client
        {
            public QuicConnection Connection { get; set; }
            public Stream Stream { get; set; }

            public UInt128 Pool { get; set; }
            public ulong Id { get; set; }

            public byte[] InboundHeaderBuffer { get; } = new byte[InboundFrameHeaderSize];
            public byte[] InboundTargetsBuffer { get; } = new byte[MaxTargetBytesPerFrame];
            public byte[] OutboundHeaderBuffer { get; } = new byte[OutboundFrameHeaderSize + MaxTargetBytesPerFrame];

            public Channel<OutgoingPacket> SendQueue { get; } = Channel.CreateBounded<OutgoingPacket>(new BoundedChannelOptions(MaxQueuedPacketsPerClient)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            private long _queuedBytes;
            private int _queuedPackets;
            private long _saturatedSinceTimestamp;
            private int _disconnectScheduled;
            private readonly SemaphoreSlim _queueSignal = new(0, MaxQueuedPacketsPerClient);

            public long QueuedBytes => Volatile.Read(ref _queuedBytes);
            public int QueuedPackets => Volatile.Read(ref _queuedPackets);

            public bool IsSeverelyBackedUp =>
                QueuedBytes >= HardQueuedBytesPerClient || QueuedPackets >= HardQueuedPacketsPerClient;

            public bool TryReserveQueueSpace(int packetBytes)
            {
                while (true)
                {
                    long queuedBytes = Volatile.Read(ref _queuedBytes);

                    if (queuedBytes + packetBytes > MaxQueuedBytesPerClient)
                        return false;

                    if (Interlocked.CompareExchange(ref _queuedBytes, queuedBytes + packetBytes, queuedBytes) != queuedBytes)
                        continue;

                    int queuedPackets = Interlocked.Increment(ref _queuedPackets);

                    if (queuedPackets > MaxQueuedPacketsPerClient)
                    {
                        Interlocked.Decrement(ref _queuedPackets);
                        Interlocked.Add(ref _queuedBytes, -packetBytes);
                        return false;
                    }

                    if (queuedBytes + packetBytes >= SoftQueuedBytesPerClient || queuedPackets >= SoftQueuedPacketsPerClient)
                        MarkSaturated();

                    return true;
                }
            }

            public void ReleaseQueueSpace(int packetBytes)
            {
                long queuedBytes = Interlocked.Add(ref _queuedBytes, -packetBytes);
                int queuedPackets = Interlocked.Decrement(ref _queuedPackets);

                if (queuedBytes < SoftQueuedBytesPerClient && queuedPackets < SoftQueuedPacketsPerClient)
                    Interlocked.Exchange(ref _saturatedSinceTimestamp, 0);

                SignalQueueChange();
            }

            public bool HasBeenSaturatedFor(TimeSpan duration)
            {
                long saturatedSince = Volatile.Read(ref _saturatedSinceTimestamp);
                return saturatedSince != 0 && Stopwatch.GetElapsedTime(saturatedSince) >= duration;
            }

            public bool TryBeginDisconnect() => Interlocked.Exchange(ref _disconnectScheduled, 1) == 0;

            public bool IsDisconnectScheduled => Volatile.Read(ref _disconnectScheduled) != 0;

            public Task<bool> WaitForQueueCapacityChangeAsync(TimeSpan timeout, CancellationToken ct) => _queueSignal.WaitAsync(timeout, ct);

            private void MarkSaturated()
            {
                long now = Stopwatch.GetTimestamp();
                Interlocked.CompareExchange(ref _saturatedSinceTimestamp, now, 0);
            }

            public void SignalQueueChange()
            {
                try
                {
                    _queueSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }

        private sealed class SharedPayload
        {
            private byte[]? _buffer;
            private int _referenceCount = 1;

            public int Length { get; }

            private SharedPayload(byte[] buffer, int length)
            {
                _buffer = buffer;
                Length = length;
            }

            public static SharedPayload Rent(int length)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
                return new SharedPayload(buffer, length);
            }

            public Memory<byte> WritableMemory => _buffer!.AsMemory(0, Length);
            public ReadOnlyMemory<byte> ReadOnlyMemory => _buffer!.AsMemory(0, Length);

            public void AddReference() => Interlocked.Increment(ref _referenceCount);

            public void Release()
            {
                if (Interlocked.Decrement(ref _referenceCount) != 0)
                    return;

                var buffer = Interlocked.Exchange(ref _buffer, null);
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal readonly struct OutgoingPacket
        {
            readonly ushort Flags;
            readonly ulong SenderId;
            readonly ulong[]? Targets;
            readonly SharedPayload Payload;
            readonly int QueuedBytes;

            OutgoingPacket(ushort flags, ulong senderId, ulong[]? targets, SharedPayload payload)
            {
                Flags = flags;
                SenderId = senderId;
                Targets = targets;
                Payload = payload;
                QueuedBytes =
                    sizeof(ushort) +   // flags
                    sizeof(ulong) +    // sender id
                    sizeof(byte) +     // target count
                    ((targets?.Length ?? 0) * sizeof(ulong)) +
                    sizeof(int) +      // content length
                    payload.Length;
            }
        }

        public static async Task AcceptLoopAsync(QuicListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var conn = await listener.AcceptConnectionAsync(ct);
                    Console.WriteLine($"Accepted {conn.RemoteEndPoint}");

                    /*var pingStream = await conn.AcceptInboundStreamAsync(ct);

                    Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            int i = 0;

                            while (true)
                            {
                                pingStream.WriteByte((byte)(i++ % 255));
                                Thread.Sleep(2000);
                                pingStream.ReadByte();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(ex.ToString());
                        }
                    });*/

                    var stream = await conn.AcceptInboundStreamAsync(ct);


                    if (stream.Type != QuicStreamType.Bidirectional)
                    {
                        stream.Close();
                        continue;
                    }

                    //Version plus compression flag
                    byte[] buffer = new byte[2 + 1];
                    await ReadAtLeastAsync(stream, buffer, buffer.Length, ct);

                    ushort version = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(0, sizeof(ushort)));
                    bool compressed = BitConverter.ToBoolean(buffer, 2);

                    if (version != CurrentVersion)
                    {
                        stream.Close();
                        throw new Exception("Version mismatch");
                    }

                    _ = Task.Run(() => Verify(conn, compressed ? new BrotliTransparentStream(stream) : stream, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Accept error: {ex}");

                    await Task.Delay(1000, ct);
                }
            }
        }
        internal static async Task Verify(QuicConnection qc, Stream qs, CancellationToken ct)
        {
            byte[] handshakeBuffer = ArrayPool<byte>.Shared.Rent(25);
            byte[] skipBuffer = ArrayPool<byte>.Shared.Rent(1024);

            try
            {
                await ReadAtLeastAsync(qs, handshakeBuffer, 25, ct);

                ulong poolLow = BinaryPrimitives.ReadUInt64LittleEndian(handshakeBuffer.AsSpan(0, 8));
                ulong poolHigh = BinaryPrimitives.ReadUInt64LittleEndian(handshakeBuffer.AsSpan(8, 8));
                UInt128 pool = poolLow | ((UInt128)poolHigh << 64);
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(handshakeBuffer.AsSpan(16, 8));
                byte flags = handshakeBuffer[24];

                for (int i = 0; i < flags; i++)
                {
                    await ReadAtLeastAsync(qs, handshakeBuffer, 1, ct);
                    byte keySize = handshakeBuffer[0];

                    await SkipBytesAsync(qs, keySize, skipBuffer, ct);

                    await ReadAtLeastAsync(qs, handshakeBuffer, 4, ct);
                    int contentLength = BinaryPrimitives.ReadInt32LittleEndian(handshakeBuffer.AsSpan(0, 4));
                    if (contentLength < 0)
                        throw new InvalidDataException("Invalid handshake metadata length.");

                    await SkipBytesAsync(qs, contentLength, skipBuffer, ct);
                }

                Client c = new Client()
                {
                    Connection = qc,
                    Stream = qs,

                    Id = id,
                    Pool = pool
                };

                var poolConnections = _connections.GetOrAdd(pool, _ => []);
                poolConnections.TryAdd(id, c);

                var writeTask = WriteLoop(c, ct);

                try
                {
                    await ReadLoop(c, ct);
                }
                finally
                {
                    c.SendQueue.Writer.TryComplete();

                    await writeTask;


                    if (_connections.TryGetValue(pool, out var dict))
                        dict.TryRemove(id, out _);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(handshakeBuffer);
                ArrayPool<byte>.Shared.Return(skipBuffer);
            }
        }

        private static async Task ReadLoop(Client c, CancellationToken ct)
        {  
            while (!ct.IsCancellationRequested)
            {
                await ReadAtLeastAsync(c.Stream, c.InboundHeaderBuffer, InboundFrameHeaderSize, ct);

                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(c.InboundHeaderBuffer.AsSpan(0, sizeof(ushort)));
                byte targetsAmount = c.InboundHeaderBuffer[sizeof(ushort)];
                int contentLength = BinaryPrimitives.ReadInt32LittleEndian(c.InboundHeaderBuffer.AsSpan(sizeof(ushort) + sizeof(byte), sizeof(int)));

                if (contentLength < 0)
                    throw new InvalidDataException("Invalid frame size.");

                ulong[]? targets = null;
                if (targetsAmount > 0)
                {
                    int targetBytes = targetsAmount * sizeof(ulong);
                    await ReadAtLeastAsync(c.Stream, c.InboundTargetsBuffer, targetBytes, ct);

                    targets = new ulong[targetsAmount];
                    for (int i = 0; i < targetsAmount; i++)
                    {
                        targets[i] = BinaryPrimitives.ReadUInt64LittleEndian(c.InboundTargetsBuffer.AsSpan(i * sizeof(ulong), sizeof(ulong)));
                    }
                }

                SharedPayload payload = SharedPayload.Rent(contentLength);

                try
                {
                    await ReadAtLeastAsync(c.Stream, payload.WritableMemory, ct);
                }
                catch
                {
                    payload.Release();
                    throw;
                }

                await BroadcastAsync(c, flags, targets, payload, ct);
            }
        }

        private static async Task WriteLoop(Client c, CancellationToken ct)
        {
            try
            {
                var reader = c.SendQueue.Reader;

                while (await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var msg))
                    {
                        try
                        {
                            int targetLen = msg.Targets?.Length ?? 0;
                            int headerLength = OutboundFrameHeaderSize + (targetLen * sizeof(ulong));
                            Span<byte> headerSpan = c.OutboundHeaderBuffer.AsSpan(0, headerLength);

                            BinaryPrimitives.WriteUInt16LittleEndian(headerSpan[..sizeof(ushort)], msg.Flags);
                            BinaryPrimitives.WriteUInt64LittleEndian(headerSpan.Slice(sizeof(ushort), sizeof(ulong)), msg.SenderId);
                            headerSpan[sizeof(ushort) + sizeof(ulong)] = (byte)targetLen;
                            BinaryPrimitives.WriteInt32LittleEndian(headerSpan.Slice(sizeof(ushort) + sizeof(ulong) + sizeof(byte), sizeof(int)), msg.Payload.Length);

                            int offset = OutboundFrameHeaderSize;
                            if (targetLen > 0)
                            {
                                foreach (var target in msg.Targets!)
                                {
                                    BinaryPrimitives.WriteUInt64LittleEndian(headerSpan.Slice(offset, sizeof(ulong)), target);
                                    offset += sizeof(ulong);
                                }
                            }

                            await c.Stream.WriteAsync(c.OutboundHeaderBuffer.AsMemory(0, headerLength), ct);

                            if (msg.Payload.Length > 0)
                                await c.Stream.WriteAsync(msg.Payload.ReadOnlyMemory, ct);
                        }
                        finally
                        {
                            c.ReleaseQueueSpace(msg.QueuedBytes);
                            msg.Payload.Release();
                        }
                    }

                    await c.Stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* Normal shutdown */ }
            catch (Exception ex)
            {
                Console.WriteLine($"Write error for {c.Id}: {ex.Message}");
                ScheduleDisconnect(c, "write failure");
            }
        }

        private static async Task BroadcastAsync(Client c, ushort flags, ulong[]? targets, SharedPayload payload, CancellationToken ct)
        {
            if (!_connections.TryGetValue(c.Pool, out var poolClients)) 
            {
                payload.Release();
                return;
            }

            var packet = new OutgoingPacket(flags, c.Id, targets, payload);
            List<Client>? congestedReceivers = null;
            int receiversCount = 0;
            HashSet<ulong>? targetSet = targets is { Length: > 8 } ? new HashSet<ulong>(targets) : null;

            try
            {
                foreach (var receiverEntry in poolClients)
                {
                    var receiver = receiverEntry.Value;

                    if (receiver.Id == c.Id)
                        continue;

                    if (targets != null && targets.Length > 0)
                    {
                        bool found = targetSet?.Contains(receiver.Id) == true;

                        if (!found)
                        {
                            for (int i = 0; i < targets.Length; i++)
                            {
                                if (targets[i] == receiver.Id)
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }

                        if (!found)
                            continue;
                    }

                    receiversCount++;
                    payload.AddReference();

                    if (!TryQueuePacket(receiver, packet))
                        (congestedReceivers ??= []).Add(receiver);
                }

                if (congestedReceivers == null || congestedReceivers.Count == 0)
                    return;

                bool laggardsAreBottleneck = receiversCount > 1 &&
                                             (congestedReceivers.Count == 1 ||
                                              congestedReceivers.Count * 3 <= receiversCount);

                foreach (var receiver in congestedReceivers)
                {
                    if (!await WaitForReceiverCapacityAsync(receiver, packet, laggardsAreBottleneck, ct))
                        payload.Release();
                }
            }
            finally
            {
                payload.Release();
            }
        }

        private static bool TryQueuePacket(Client receiver, OutgoingPacket packet)
        {
            if (receiver.IsDisconnectScheduled)
                return false;

            if (!receiver.TryReserveQueueSpace(packet.QueuedBytes))
                return false;

            if (receiver.SendQueue.Writer.TryWrite(packet))
                return true;

            receiver.ReleaseQueueSpace(packet.QueuedBytes);
            return false;
        }

        private static async Task<bool> WaitForReceiverCapacityAsync(Client receiver, OutgoingPacket packet, bool laggardIsBottleneck, CancellationToken ct)
        {
            while (true)
            {
                if (receiver.IsDisconnectScheduled)
                    return false;

                if (TryQueuePacket(receiver, packet))
                    return true;

                if (ShouldDisconnectSlowReceiver(receiver, laggardIsBottleneck))
                {
                    ScheduleDisconnect(receiver, laggardIsBottleneck ? "receiver became the pool bottleneck" : "receiver never recovered from overload");
                    return false;
                }

                try
                {
                    await receiver.WaitForQueueCapacityChangeAsync(QueueWaitInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        private static bool ShouldDisconnectSlowReceiver(Client receiver, bool laggardIsBottleneck)
        {
            if (laggardIsBottleneck &&
                receiver.IsSeverelyBackedUp &&
                receiver.HasBeenSaturatedFor(BottleneckDisconnectGracePeriod))
            {
                return true;
            }

            return receiver.QueuedBytes >= MaxQueuedBytesPerClient &&
                   receiver.HasBeenSaturatedFor(AbsoluteDisconnectGracePeriod);
        }

        private static void ScheduleDisconnect(Client client, string reason)
        {
            if (!client.TryBeginDisconnect())
                return;

            Console.WriteLine($"Disconnecting slow client {client.Id} from pool {client.Pool}: {reason}");

            if (_connections.TryGetValue(client.Pool, out var poolClients))
                poolClients.TryRemove(client.Id, out _);

            client.SendQueue.Writer.TryComplete();
            client.SignalQueueChange();

            _ = Task.Run(async () =>
            {
                try
                {
                    await client.Connection.CloseAsync(1, CancellationToken.None);
                }
                catch { }

                try
                {
                    client.Stream.Close();
                }
                catch { }
            });
        }


        /*private static async Task Broadcast(Client c, ushort flags, ulong[]? targets, byte[] content, CancellationToken ct)
        {
            Console.WriteLine("Sending from " + c.Id + " in " + c.Pool + " size " + content.Length);
            //Parallel.ForEach(_connections[c.Pool], async (s) =>

            foreach(var s in _connections[c.Pool])
            {
                try
                {
                    if (s.Value.Id == c.Id || (targets.Length > 0 && !targets.Contains(s.Value.Id)))
                        continue;

                    //Console.WriteLine("Sending to " + s.Key);

                    s.Value.Writer.Write(flags);
                    s.Value.Writer.Write(CurrentVersion);

                    s.Value.Writer.Write(c.Id);

                    s.Value.Writer.Write((byte)targets.Length);

                    foreach (var t in targets)
                    {
                        s.Value.Writer.Write(t);
                    }

                    s.Value.Writer.Write(content.Length);
                    s.Value.Writer.Flush();

                    await s.Value.Stream.WriteAsync(content);
                    await s.Value.Stream.FlushAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.ToString());

                    _connections[c.Pool].Remove(s.Key, out var _);
                }
            }
        }*/

        private static async Task<int> ReadAtLeastAsync(Stream s, byte[] buffer, int len, CancellationToken ct)
        {
            int total = 0;
            while (total < len)
            {
                int read = await s.ReadAsync(buffer, total, len - total, ct);
                if (read == 0)
                    throw new EndOfStreamException();
                total += read;
            }
            return total;
        }

        private static async Task ReadAtLeastAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await s.ReadAsync(buffer[total..], ct);
                if (read == 0)
                    throw new EndOfStreamException();
                total += read;
            }
        }

        private static async Task SkipBytesAsync(Stream s, int len, byte[] scratchBuffer, CancellationToken ct)
        {
            int remaining = len;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, scratchBuffer.Length);
                await ReadAtLeastAsync(s, scratchBuffer, chunk, ct);
                remaining -= chunk;
            }
        }
    }
} 
