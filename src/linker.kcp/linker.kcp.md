# linker.kcp

## 需求

- 仅参考 kcp-go 实现 KCP 层
- 无 session、无加密、无 FEC
- 命名空间使用项目名，全小写
- 无需与 kcp-go 互通，按性能最高方式实现

## 性能目标

- 0 GC 托管分配
- 1 Gbps 以上吞吐

## 封装

业务层已经有 UDP socket 和 remoteEndpoint，直接实例化 `KcpConnection` 用于双向通信。

封装层只需要对业务层暴露类似 TCP 的使用语义：

- `SendAsync`：发送业务数据。业务层只提交 payload，不需要关心 KCP 分片、窗口、重传、flush 或 UDP 发送时机。发送侧出现背压时，`SendAsync` 像 TCP Socket `SendAsync` 一样异步阻塞，直到可以继续发送、连接关闭或被取消。
- `ReceiveAsync`：接收业务数据。返回内容是一个或多个连续业务记录，每条记录格式为 `[2字节little-endian payload长度][payload]`。业务层按长度头切割使用即可，不需要关心 UDP 包、KCP 包、ACK、重组或重传。没有可读业务数据时，`ReceiveAsync` 像 TCP Socket `ReceiveAsync` 一样异步阻塞，直到收到数据、连接关闭或被取消。
- `recv`：控制 UDP 接收归属。`recv=true` 时，`KcpConnection` 内部使用传入的 `udpSocket` 接收 UDP 数据并输入 KCP；`recv=false` 时，业务层负责接收 UDP 数据，并通过 `Input` 输入到 `KcpConnection`，封装层只使用 `udpSocket` 发送 UDP 数据。

伪代码：

```csharp
public sealed class KcpConnection : IAsyncDisposable
{
    public KcpConnection(
        uint conv,
        int mtu,
        int window,
        int nodelay,
        int interval,
        int resend,
        int nc,
        Socket udpSocket,
        EndPoint remoteEndpoint,
        bool recv = true)
    {
    }

    // recv=false 时由业务层把收到的完整 UDP datagram 输入进来。
    // recv=true 时通常不需要业务层调用 Input。
    public void Input(byte[] data, int offset, int length)
    {
    }

    public void Input(ReadOnlyMemory<byte> data)
    {
    }

    // 发送业务数据；背压时异步阻塞，语义对齐 TCP Socket SendAsync。
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
    }

    // 接收业务数据；返回一个或多个 [2字节长度][payload] 记录；无数据时异步阻塞。
    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
    }
}
```
