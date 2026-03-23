using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.TagHelpers.Cache;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const ushort CurrentVersion = 50001;

        private static readonly ConcurrentDictionary<UInt128, ConcurrentDictionary<ulong, Client>> _connections = new();

        public class Client
        {
            public QuicConnection Connection { get; set; }
            public Stream Stream { get; set; }

            public BinaryWriter Writer { get; set; }
            public BinaryReader Reader { get; set; }

            public UInt128 Pool { get; set; }
            public ulong Id { get; set; }

            public Channel<OutgoingPacket> SendQueue { get; } = Channel.CreateUnbounded<OutgoingPacket>(new UnboundedChannelOptions
            {
                SingleReader = true, // Performance optimization
                SingleWriter = false
            });
        }
        internal readonly struct OutgoingPacket
        {
            public readonly ushort Flags;
            public readonly ulong SenderId;
            public readonly ulong[]? Targets;
            public readonly byte[] Content;

            public OutgoingPacket(ushort flags, ulong senderId, ulong[]? targets, byte[] content)
            {
                Flags = flags;
                SenderId = senderId;
                Targets = targets;
                Content = content;
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


                    Task.Factory.StartNew(async () =>
                    {
                        byte[] b = new byte[64 * 1024 * 1024];

                        while (true)
                        {
                            try
                            {
                                await stream.WriteAsync(b);
                            }
                            catch (QuicException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                            }
                        }
                    });

                    Task.Factory.StartNew(async () =>
                    {
                        byte[] c = new byte[64 * 1024];

                        while (true)
                        {
                            try
                            {
                                await stream.ReadAsync(c);
                            }
                            catch (QuicException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                            }
                        }
                    });



                  /*  Thread.Sleep(-1);

                    if (stream.Type != QuicStreamType.Bidirectional)
                    {
                        stream.Close();
                        continue;
                    }

                    //Version plus compression flag
                    byte[] buffer = new byte[2 + 1];
                    var readed = stream.Read(buffer);

                    if (readed != buffer.Length)
                    {
                        stream.Close();
                        throw new Exception("WTF BRO");
                    }

                    ushort version = BitConverter.ToUInt16(buffer, 0);
                    bool compressed = BitConverter.ToBoolean(buffer, 2);

                    if (version != CurrentVersion)
                    {
                        stream.Close();
                        throw new Exception("Version mismatch");
                    }

                   Task.Factory.StartNew(async () => await Verify(conn, compressed ? new BrotliTransparentStream(stream) : stream, ct));*/
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
            using (BinaryWriter bw = new(qs, Encoding.UTF8, leaveOpen: true))
            using (BinaryReader br = new(qs, Encoding.UTF8, leaveOpen: true))
            {
                byte[] buffer = br.ReadBytes(16);
                UInt128 pool = BitConverter.ToUInt128(buffer);

                buffer = br.ReadBytes(8);
                ulong id = BitConverter.ToUInt64(buffer, 0);

                byte flags = br.ReadByte();

                for (int i = 0; i < flags; i++)
                {
                    byte keySize = br.ReadByte();

                    var key = br.ReadBytes(keySize);

                    buffer = br.ReadBytes(4);
                    ushort contentLength = Convert.ToUInt16(buffer);

                    var value = br.ReadBytes(contentLength);
                }

                Client c = new Client()
                {
                    Connection = qc,
                    Stream = qs,

                    Reader = br,
                    Writer = bw,

                    Id = id,
                    Pool = pool
                };

                if (!_connections.ContainsKey(pool))
                    _connections[pool] = [];

                _connections[pool].TryAdd(id, c);

                var writeTask = WriteLoop(c, ct);

                try
                {
                    await ReadLoop(qs, c, ct);
                }
                finally
                {
                    c.SendQueue.Writer.TryComplete();

                    await writeTask;


                    if (_connections.TryGetValue(pool, out var dict))
                        dict.TryRemove(id, out _);
                }
            }
        }

        private static async Task ReadLoop(Stream qs, Client c, CancellationToken ct)
        {  
            while (!ct.IsCancellationRequested)
            {
                ushort flags = c.Reader.ReadUInt16();
                ushort version = c.Reader.ReadUInt16();

                if (version != CurrentVersion)
                {
                    c.Stream.Close();

                    throw new Exception("Version mismatch");
                }

                byte targetsAmount = c.Reader.ReadByte();
                ulong[] targets = new ulong[targetsAmount];

                for (int i = 0; i < targetsAmount; i++)
                {
                    targets[i] = BitConverter.ToUInt64(c.Reader.ReadBytes(64 / 8));
                }

                int contentLength = c.Reader.ReadInt32();
                byte[] content = new byte[contentLength];

                await ReadAtLeastAsync(c.Stream, content, contentLength, ct);
                    //c.Reader.ReadBytes(contentLength);

                Broadcast(c, flags, targets, content, ct);
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
                        c.Writer.Write(msg.Flags);
                        c.Writer.Write(CurrentVersion);
                        c.Writer.Write(msg.SenderId);

                        int targetLen = msg.Targets?.Length ?? 0;
                        c.Writer.Write((byte)targetLen);

                        if (targetLen > 0)
                        {
                            foreach (var t in msg.Targets!)
                                c.Writer.Write(t);
                        }

                        c.Writer.Write(msg.Content.Length);
                        //c.Writer.Flush();

                        // Write the content payload asynchronously
                        await c.Stream.WriteAsync(msg.Content, ct);
                    }

                    // Flush the stream after a batch of messages is processed
                    //Console.WriteLine("Flushing tereth");
                    await c.Stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* Normal shutdown */ }
            catch (Exception ex)
            {
                Console.WriteLine($"Write error for {c.Id}: {ex.Message}");
                // If writing fails, we should probably kill the connection
                try { c.Connection.CloseAsync(1, ct); } catch { }
            }
        }

        private static void Broadcast(Client c, ushort flags, ulong[]? targets, byte[] content, CancellationToken ct)
        {
            // Optimization: Don't allocate iterator if possible
            if (!_connections.TryGetValue(c.Pool, out var poolClients)) 
                return;

            var packet = new OutgoingPacket(flags, c.Id, targets, content);

            foreach (var receiverEntry in poolClients)
            {
                var receiver = receiverEntry.Value;

                // Skip self
                if (receiver.Id == c.Id)
                    continue;

                // Filter targets
                if (targets != null && targets.Length > 0)
                {
                    // Linear search is slow for large arrays, but fine for small ones.
                    // For large target lists, use a HashSet.
                    bool found = false;
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (targets[i] == receiver.Id) { found = true; break; }
                    }
                    if (!found) continue;
                }

                // NON-BLOCKING ADD
                // This returns immediately. It effectively acts as "Fire and Forget"

                receiver.SendQueue.Writer.TryWrite(packet);
            }
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
    }
} 