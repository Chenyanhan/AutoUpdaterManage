# AutoUpdater UDP 协议 V1

控制端监听端口：`45677/UDP`。设备端监听端口：`45678/UDP`。
控制端从 `45677` 向设备 `45678` 发送发现和任务指令；设备将响应发送回请求数据包的来源端口。
发现请求使用局域网广播，其余消息使用单播。

## 数据包结构

| 偏移 | 长度 | 字段 | 说明 |
|---:|---:|---|---|
| 0 | 2 | Magic | 固定为 `0x41 0x55`（ASCII `AU`） |
| 2 | 1 | Version | 当前为 `0x01` |
| 3 | 1 | Command | 指令码 |
| 4 | 16 | RequestId | UUID，大端字节序 |
| 20 | 4 | PayloadLength | 载荷长度，大端 `Int32` |
| 24 | 4 | CRC32 | 载荷 CRC32，大端 `UInt32` |
| 28 | N | Payload | UTF-8 JSON，最大 60 KiB |

无载荷时 `PayloadLength` 和 `CRC32` 均为 `0`。

## 指令码

| 指令码 | 名称 | 方向 | 用途 |
|---:|---|---|---|
| `0x01` | DiscoverRequest | 管理设备 → 广播 | 检索局域网设备 |
| `0x02` | DiscoverResponse | 设备 → 请求方 | 返回设备编号、名称、IP、版本 |
| `0x10` | UpdateRequest | 管理设备 → 目标设备 | 下发更新来源 |
| `0x11` | UpdateAccepted | 目标设备 → 管理设备 | 返回接受或拒绝 |
| `0x12` | UpdateResult | 目标设备 → 管理设备 | 返回最终成功或失败 |
| `0x13` | CancelTask | 管理设备 → 目标设备 | 取消尚未安装的更新任务 |
| `0x14` | RollbackRequest | 管理设备 → 目标设备 | 回退到备份版本或指定版本 |
| `0x20` | Heartbeat | 双向 | 预留心跳指令 |
| `0x21` | StatusQuery | 管理设备 → 目标设备 | 查询当前任务状态 |
| `0x22` | StatusResponse | 目标设备 → 管理设备 | 返回版本和任务状态 |

响应必须沿用请求的 `RequestId`，便于并发任务关联。

## 载荷示例

### `0x02` DiscoverResponse

```json
{
  "deviceId": "001122AABBCC",
  "name": "OFFICE-PC-01",
  "ipAddress": "192.168.1.21",
  "version": "1.0.0.0",
  "listenPort": 45678
}
```

### `0x10` UpdateRequest

```json
{
  "senderId": "00AABBCCDDEE",
  "targetDeviceId": "001122AABBCC",
  "updatePath": "\\\\192.168.1.10\\updates\\v2"
}
```

### `0x11` UpdateAccepted

```json
{
  "deviceId": "001122AABBCC",
  "accepted": true,
  "message": "设备已接受更新"
}
```

### `0x12` UpdateResult

```json
{
  "deviceId": "001122AABBCC",
  "success": true,
  "message": "更新成功",
  "currentVersion": "2.0.0.0"
}
```

## 处理约定

- 收到错误 Magic、未知版本、未知指令、长度不符或 CRC32 错误的数据包时直接丢弃。
- 设备只处理 `targetDeviceId` 与自身编号一致的更新请求。
- 同一个 `RequestId` 的更新请求应只执行一次；持久化去重将在更新执行器阶段实现。
- UDP 不保证到达。正式版本应对 `0x01` 和 `0x10` 增加超时重试，但设备端必须通过 `RequestId` 防止重复执行。
- 更新包还需要增加 SHA-256 和数字签名校验，不能仅依赖传输层 CRC32。
