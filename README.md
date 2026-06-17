这是一个零分配、高性能面向UDP实时传输的前向纠错（FEC）库。它把原始业务包编码成系统源帧和修复帧，接收端在部分 FEC 帧丢失时仍可恢复原始数据，用来降低UDP丢包对业务层的影响。

## 1、FEC

#### 简单使用

```csharp
//初始化
LinkerFecOptions fecOption = new LinkerFecOptions
{
    SourceSymbolsPerBlock = 10,
    RepairSymbolsPerBlock = 4,
    SymbolSize = 1420 + LinkerFecEncodedSymbol.HeaderSize,
    RepairProfile = [
        new LinkerFecRepairProfilePoint(1, 2),
        new LinkerFecRepairProfilePoint(10, 4)
    ],
};
LinkerFecCodec fecEncoder = new LinkerFecCodec(fecOption);
LinkerFecCodec fecDecoder = new LinkerFecCodec(fecOption);
byte[] fecEncodeBuffer = new byte[fecOption.MaxEncodeBufferSize];
byte[] fecDecodeBuffer = new byte[fecOption.MaxDecodeBufferSize];

//编码
//packets [2 length][payload][2 length][payload]
if (fecEncoder.TryEncodePacket(packets, fecEncodeBuffer, out int bytesWritten, out int packetCount))
{
    var memory = fecEncodeBuffer.AsMemory(0, bytesWritten);
    for (int i = 0; i < packetCount; i++)
    {
        int packetLength = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span);
        Memory<byte> packet = memory.Slice(LinkerFecOptions.FrameLengthPrefixSize, packetLength);

        //发送packet

        memory = memory.Slice(LinkerFecOptions.FrameLengthPrefixSize + packetLength);
    }
}
//解码
if (fecDecoder.TryDecodeFrame(packet, fecDecodeBuffer, out int bytesWritten, out int packetCount))
{
    //[2 length][payload][2 length][payload]
    Memory<byte> packets = fecDecodeBuffer.AsMemory(0, bytesWritten);
}

```

#### 推荐配置

| 场景 | 推荐 profile | 说明 |
|---|---|---|
| 垃圾网 | `1:3,10:4` | 单包 3 冗余，满批 40% 冗余 |
| 高丢包 | `1:2,10:4` | 单包 2 冗余，满批 40% 冗余 |
| 省带宽 | `1:1,10:2` | 单包 1 冗余，满批 20% 冗余 |

## 2、KCP

原开源项目 https://github.com/skywind3000/kcp

#### 简单使用

```csharp
KcpConnection kcpConnection = new KcpConnection(12138, 1500, 8192, 1, 10, 2, 1,
 udpSocket, remoteEndPoint, false);

//发送端
await kcpConnection.SendAsync(packet, token).ConfigureAwait(false);

//接收端
kcpConnection.Input(packet);
//接收端独立线程
using IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(64 * 1024);
while (!cts.IsCancellationRequested)
{
    int length = await kcpConnection.ReceiveAsync(owner.Memory, cts.Token).ConfigureAwait(false);
    if (length <= 0)
    {
        return;
    }
    //[2 length][payload][2 length][payload]
    Memory<byte> packets = owner.Memory.Slice(0, length);
}

```

## STUN

RFC 5780 NAT类型测试

#### 简单使用

```csharp
StunClient stun = new StunClient();
StunNatBehaviorResult result = await stun.DiscoverNatBehaviorAsync("支持RFC 5780的服务器", 3478, new StunClientOptions
{
    AddressFamilyMode = StunAddressFamilyMode.Ipv6Preferred,
    MaxAttempts = 3
}, token).ConfigureAwait(false);
StunNatMappingBehavior mapping = result.MappingBehavior;
StunNatFilteringBehavior filtering = result.FilteringBehavior;
```
