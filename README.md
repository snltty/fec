这是一个零分配、高性能面向UDP实时传输的前向纠错（FEC）库。它把原始业务包编码成系统源帧和修复帧，接收端在部分 FEC 帧丢失时仍可恢复原始数据，用来降低UDP丢包对业务层的影响。

## 1、冗余配置

#### 固定冗余

```csharp
new LinkerFecOptions
{
    SourceSymbolsPerBlock = 10,
    RepairSymbolsPerBlock = 2,
};
```

| source | repair | 输出 |
|---:|---:|---|
| 1 | 2 | `1 source frame + 2 repair frame` |
| 5 | 2 | `5 source frame + 2 repair frame` |
| 10 | 2 | `10 source frame + 2 repair frame` |

#### 策略冗余

```csharp
new LinkerFecOptions
{
    SourceSymbolsPerBlock = 10,
    RepairSymbolsPerBlock = 2,
    RepairProfile =
    [
        new LinkerFecRepairProfilePoint(1, 2),
        new LinkerFecRepairProfilePoint(10, 4)
    ]
};
```

#### 推荐配置

| 场景 | 推荐 profile | 说明 |
|---|---|---|
| 垃圾网 | `1:3,10:4` | 单包 3 冗余，满批 40% 冗余 |
| 高丢包 | `1:2,10:4` | 单包 2 冗余，满批 40% 冗余 |
| 省带宽 | `1:1,10:2` | 单包 1 冗余，满批 20% 冗余 |

## 2、丢包测试

服务端参数 `server ep0.0.0.0:12345` / `server ep0.0.0.0:12345 fec`

客户端参数 `server ep0.0.0.0:12345` / `server ep0.0.0.0:12345 fec`

以下结果中 ❌丢失、💚FEC算法恢复、其它正常


#### 局域网内

服务端双向丢包 10%

```
iptables -A INPUT -p udp --dport 12345 -m statistic --mode random --probability 0.1 -j DROP
iptables -A OUTPUT -p udp --sport 12345 -m statistic --mode random --probability 0.1 -j DROP

iptables -D INPUT -p udp --dport 12345 -m statistic --mode random --probability 0.1 -j DROP
iptables -D OUTPUT -p udp --sport 12345 -m statistic --mode random --probability 0.1 -j DROP
```

##### UDP

|0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9|
|---|---|---|---|---|---|---|---|---|---|
|❌|01|02|03|04|05|06|07|08|09|
|10|11|12|13|14|15|16|17|18|19|
|20|❌|22|23|24|❌|❌|27|28|29|
|30|❌|32|33|34|35|36|37|38|39|
|40|41|❌|43|44|45|46|47|48|49|
|50|51|❌|❌|54|55|56|57|58|59|
|60|61|62|63|64|65|66|67|❌|❌|
|70|71|72|73|74|75|76|77|78|79|
|❌|81|82|83|84|85|❌|87|88|89|
|❌|91|92|93|94|❌|96|97|98|99|

##### UDP + FEC

|0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9|
|---|---|---|---|---|---|---|---|---|---|
|00|01|02|03|04|💚|06|💚|💚|09|
|10|11|💚|13|14|15|16|17|18|💚|
|20|21|22|23|24|25|26|27|💚|29|
|30|31|💚|33|34|35|💚|37|💚|39|
|40|41|💚|43|💚|45|💚|47|48|49|
|💚|51|52|53|54|55|56|57|58|59|
|60|61|62|63|64|65|💚|67|💚|69|
|70|71|72|73|74|75|💚|77|78|79|
|80|81|82|83|💚|💚|86|87|88|89|
|90|91|92|💚|💚|💚|96|97|98|99|


## 3、性能测试

测试环境: .NET 8.0、BenchmarkDotNet 、win11 x64 、I9 9900KF、32GB

#### 独立性能 encode/decode 

| 操作 | 包长 | 平均耗时 | 吞吐 | 分配 | Gen0 | Gen1 | Gen2 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Encode | 64B | 33.92 ns/op | 15.09 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 64B | 25.91 ns/op | 19.76 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 128B | 35.06 ns/op | 29.21 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 128B | 27.18 ns/op | 37.67 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 256B | 36.50 ns/op | 56.11 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 256B | 28.07 ns/op | 72.96 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 512B | 40.34 ns/op | 101.54 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 512B | 29.40 ns/op | 139.32 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 1024B | 49.93 ns/op | 164.07 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 1024B | 40.19 ns/op | 203.83 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 1400B | 61.00 ns/op | 183.61 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 1400B | 36.11 ns/op | 310.16 Gbps | 0 B/op | 0 | 0 | 0 |

#### 整体性能 encode/decode 

| 操作 | 包长 | 平均耗时 | 吞吐 | 分配 | Gen0 | Gen1 | Gen2 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Encode+Decode | 64B | 69.74 ns/op | 7.34 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 128B | 71.36 ns/op | 14.35 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 256B | 75.63 ns/op | 27.08 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 512B | 83.96 ns/op | 48.79 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 1024B | 107.74 ns/op | 76.03 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 1400B | 115.09 ns/op | 97.32 Gbps | 0 B/op | 0 | 0 | 0 |
