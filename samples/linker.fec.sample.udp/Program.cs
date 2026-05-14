using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace linker.fec.sample.udp
{
    internal class Program
    {
        static void Main(string[] args)
        {
            bool client = args.Any(a => a.Contains("client"));
            bool server = args.Any(a => a.Contains("server"));
            bool fec = args.Any(a => a.Contains("fec"));
            IPEndPoint ep = IPEndPoint.Parse((args.FirstOrDefault(a => a.StartsWith("ep")) ?? "127.0.0.1:12345").Replace("ep", ""));

            if (client)
            {
                RunClient(fec, ep);
            }

            if (server)
            {
                RunServer(fec, ep);
            }

            Console.ReadLine();
        }
        static async void RunClient(bool fec, IPEndPoint ep)
        {
            LinkerFecCodec encoder = new LinkerFecCodec(new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 1,
                SymbolSize = 1433,
            });
            byte[] encodeBuffer = new byte[encoder.Options.MaxEncodeBufferSize];

            LinkerFecCodec decoder = new LinkerFecCodec(new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 1,
                SymbolSize = 1433,
            });
            byte[] decodeBuffer = new byte[decoder.Options.MaxDecodeBufferSize];
            LinkerFecDecodedPacketKind[] kinds = new LinkerFecDecodedPacketKind[decoder.Options.SourceSymbolsPerBlock + decoder.Options.RepairSymbolsPerBlock];

            UdpClient udp = new UdpClient();
            udp.Connect(ep);

            int row = 10, col = 10;
            string[] array = new string[row * col];

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var result = await udp.ReceiveAsync().ConfigureAwait(false);
                        if (fec)
                        {
                            if (decoder.TryDecodeFrame(result.Buffer, decodeBuffer.AsMemory(), kinds, out int bytesWritten, out int packetCount))
                            {
                                Memory<byte> packets = decodeBuffer.AsMemory(0, bytesWritten);
                                for (int i = 0; i < packetCount; i++)
                                {
                                    int packetLength = BinaryPrimitives.ReadInt32LittleEndian(packets.Span);
                                    Memory<byte> packet = packets.Slice(sizeof(int), packetLength);
                                    string recv = Encoding.UTF8.GetString(packet.Span);
                                    if (kinds[i] == LinkerFecDecodedPacketKind.Recovered)
                                    {
                                        recv = $"{recv}-FEC";
                                    }
                                    array[int.Parse(recv.Split('-')[0])] = recv;
                                    packets = packets.Slice(sizeof(int) + packetLength);

                                    Console.Write($"{recv},");
                                }
                            }
                        }
                        else
                        {
                            Console.Write($"{Encoding.UTF8.GetString(result.Buffer)},");
                            array[int.Parse(Encoding.UTF8.GetString(result.Buffer).Split('-')[0])] = Encoding.UTF8.GetString(result.Buffer);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error receiving UDP packet: {ex.Message}");
                    }
                }
            });


           
            for (int index = 0; index < row * col; index++)
            {
                {
                    if (fec)
                    {
                        byte[] source = Encoding.UTF8.GetBytes($"{index}");
                        byte[] rawPacket = new byte[sizeof(int) + source.Length];
                        BinaryPrimitives.WriteInt32LittleEndian(rawPacket.AsSpan(0, sizeof(int)), source.Length);
                        source.CopyTo(rawPacket.AsSpan(sizeof(int)));

                        if (encoder.TryEncodePacket(rawPacket, encodeBuffer.AsMemory(), out int bytesWritten, out int packetCount))
                        {
                            var memory = encodeBuffer.AsMemory(0, bytesWritten);
                            for (int i = 0; i < packetCount; i++)
                            {
                                int packetLength = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
                                Memory<byte> packet = memory.Slice(sizeof(int), packetLength);

                                await udp.SendAsync(packet).ConfigureAwait(false);

                                memory = memory.Slice(sizeof(int) + packetLength);
                            }
                        }
                    }
                    else
                    {
                        await udp.SendAsync(Encoding.UTF8.GetBytes($"{index}")).ConfigureAwait(false);
                    }
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            await Task.Delay(5000).ConfigureAwait(false);
            Console.WriteLine();
            var sb = new StringBuilder();
            sb.AppendLine($"|{string.Join(" | ", Enumerable.Range(0, col).Select(j => $"{j}"))}|");
            sb.AppendLine($"|{string.Join("|", Enumerable.Repeat("---", col))}|");
            for (int i = 0; i < row; i++)
            {
                sb.AppendLine($"|{string.Join("|", Enumerable.Range(0, col).Select(j => string.IsNullOrWhiteSpace(array[i * col + j])?"MISS": array[i * col + j]))}|");
            }
            Console.WriteLine(sb.ToString());

        }
        static async void RunServer(bool fec, IPEndPoint ep)
        {
            LinkerFecCodec encoder = new LinkerFecCodec(new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 1,
                SymbolSize = 1433,
            });
            byte[] encodeBuffer = new byte[encoder.Options.MaxEncodeBufferSize];

            LinkerFecCodec decoder = new LinkerFecCodec(new LinkerFecOptions
            {
                SourceSymbolsPerBlock = 10,
                RepairSymbolsPerBlock = 1,
                SymbolSize = 1433,
            });
            byte[] decodeBuffer = new byte[decoder.Options.MaxDecodeBufferSize];
            LinkerFecDecodedPacketKind[] kinds = new LinkerFecDecodedPacketKind[decoder.Options.SourceSymbolsPerBlock + decoder.Options.RepairSymbolsPerBlock];

            UdpClient udp = new UdpClient(ep);
            while (true)
            {
                var result = await udp.ReceiveAsync().ConfigureAwait(false);
                if (fec)
                {
                    if (decoder.TryDecodeFrame(result.Buffer, decodeBuffer.AsMemory(), kinds, out int bytesWritten, out int packetCount))
                    {
                        Memory<byte> packets = decodeBuffer.AsMemory(0, bytesWritten);
                        for (int i = 0; i < packetCount; i++)
                        {
                            int packetLength = BinaryPrimitives.ReadInt32LittleEndian(packets.Span);
                            Memory<byte> packet = packets.Slice(sizeof(int), packetLength);

                            string recv = Encoding.UTF8.GetString(packet.Span);
                            if(kinds[i] == LinkerFecDecodedPacketKind.Recovered)
                            {
                                recv = $"{recv}-FEC";
                            }

                            byte[] source = Encoding.UTF8.GetBytes(recv);
                            byte[] rawPacket = new byte[sizeof(int) + source.Length];
                            BinaryPrimitives.WriteInt32LittleEndian(rawPacket.AsSpan(0, sizeof(int)), source.Length);
                            source.CopyTo(rawPacket.AsSpan(sizeof(int)));
                            if (encoder.TryEncodePacket(rawPacket, encodeBuffer.AsMemory(), out int bytesWritten1, out int packetCount1))
                            {
                                var memory = encodeBuffer.AsMemory(0, bytesWritten1);
                                for (int j = 0; j < packetCount1; j++)
                                {
                                    int packetLength1 = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
                                    Memory<byte> packet1 = memory.Slice(sizeof(int), packetLength1);

                                    await udp.SendAsync(packet1, result.RemoteEndPoint).ConfigureAwait(false);

                                    memory = memory.Slice(sizeof(int) + packetLength1);
                                }
                            }

                            packets = packets.Slice(sizeof(int) + packetLength);
                        }
                    }
                }
                else
                {
                    await udp.SendAsync(result.Buffer, result.RemoteEndPoint).ConfigureAwait(false);
                }
            }
        }
    }
}
