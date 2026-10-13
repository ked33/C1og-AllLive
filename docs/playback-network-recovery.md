# 播放网络与断流恢复

## 网络入口

FFmpeg 播放源显式传入 `http_proxy=none`，HTTP、HTTPS 和 HLS 分片不再继承环境变量中的 HTTP 代理。连接交给系统路由：启用 TUN 时由 TUN 接管，否则按普通路由连接。应用不负责开启 TUN，也不修改 mihomo 分流规则或用户环境变量。本机 SignService 的 FLV 转发已使用 `UseProxy=false`。

此处不能使用空字符串：FFmpegInteropX 的 ParseOptions 会跳过空字符串，FFmpeg 的 HLS 选项复制也会跳过空值。FFmpeg 5.1 的 HTTP/TLS 实现优先读取显式 http_proxy，只有以 http:// 开头才启用代理；非空的 none 覆盖环境变量且能传递到 HLS 子请求。升级 FFmpeg/FFmpegInteropX 时应重新核对这一行为。

源码依据：
- https://github.com/FFmpeg/FFmpeg/blob/n5.1/libavformat/http.c
- https://github.com/FFmpeg/FFmpeg/blob/n5.1/libavformat/tls.c
- https://github.com/FFmpeg/FFmpeg/blob/n5.1/libavformat/aviobuf.c （ffio_copy_url_options）
- https://github.com/FFmpeg/FFmpeg/blob/n5.1/libavformat/hls.c

## 恢复与退出

- 关闭 FFmpeg HTTP 层 `reconnect`、`reconnect_at_eof`、`reconnect_streamed`，保留 12 秒网络 I/O 超时。
- EOF、播放错误、建源超时和缓冲超时进入同一个恢复任务。最多 6 次，退避 1、2、4、8、15、15 秒并加入 ±20% 抖动；一轮故障的恢复窗口最多 2 分钟。系统明确断网时暂停计时和请求，网络恢复后继续剩余预算。
- 每次恢复先查询直播状态；查询最短启动间隔为 15 秒，上层最多等 3 秒。查询超时、接口错误、字段缺失或未知状态均视为未知，保留有限恢复机会；只有明确下播才停止。旧查询未完成时不会启动第二个查询。
- 保留编码回退、换线和重新获取播放信息，但均在上述预算和退避后执行。恢复期间等候实际 Playing/失败事件，不定时强制废弃慢建源。
- 自动换线、URL 刷新和短暂 Playing 不清空预算。连续 Playing 30 秒且播放位置至少推进 15 秒才重置；缓冲或释放播放源会取消稳定性观察。
- 断网、最终失败或关闭页面时取消上层等待、清空待播队列、释放已建立的播放源和屏幕常亮。弹幕重连和心跳也会暂停；网络恢复并进入 Playing 后恢复弹幕。
- 播放信息请求串行执行，过期结果不回写清晰度、线路或播放源。API 不支持取消时，配额仍保留到真实请求完成。停止、刷新和新一轮房间加载都会使旧请求失效。
- 弹幕最多重连 6 次，连接保持 30 秒且没有关闭事件后才重置预算。Start 内同步触发的关闭、旧连接的迟到事件及停止后迟到的连接结果均有身份检查和清理。

### 原生建源取消边界

FFmpegInteropX 2.0.0 的建源在后台执行同步 `CreateFromUri`，未将 WinRT 取消接入底层 FFmpeg interrupt callback。取消 `AsTask(token)` 的等待不等于原生 I/O 已退出。不能据此立即释放配额并启动新的原生建源。

当前实现保留不带取消令牌的原始任务，只取消 UI 等待。每页最多 1 个、全进程最多 2 个未完成的原生建源。原任务真实完成，且结果已交给播放器或已销毁后，才释放配额。窗口切换、关闭和超时都遵循相同规则。等待配额最多 30 秒；长期无法结束时停止自动恢复并允许稍后手动刷新，避免继续累积任务。

参考实现：
https://github.com/ffmpeginteropx/FFmpegInteropX/blob/v2.0.0/Source/FFmpegMediaSource.cpp

## 日志

所有新增诊断复用“运行日志”开关，默认关闭。关闭时不收集新增诊断数据、不执行消息工厂、不写文件；恢复预算、并发限制和清理逻辑始终生效。

`[PlaybackRecovery]` 记录恢复代际、播放尝试、等待中的原生操作数、全进程配额和状态变化；`[DanmakuRecovery]` 记录弹幕退避、连接和稳定预算重置。这些新增事件不记录签名 URL、Cookie 或异常消息。故障诊断只读取本地状态，不额外发送 HEAD/Range 请求、下载流片段或做公网测速。

启用后当前日志为应用本地数据目录下的 `log/AllLive.log`，单文件 5 MiB 或跨天归档，最多保留 5 个滚动归档。旧版本的按日期日志不会自动删除。

## 验证边界与安装后检查

`.github/scripts/Test-PlaybackRecovery.ps1` 不依赖 UWP SDK，验证原生建源配额的并发、取消、超时、重复释放，以及直播状态解析和 C# 7.3 语法。它也在 GitHub Actions 打包前运行。完整 UWP/WinRT 编译和打包以 Actions 结果为准。

安装 2.3.64.0 后需要运行验证：

1. 直播中主播下播：明确下播后停止拉流，观察 mihomo 的 CPU、连接数和流量是否回落。
2. 持续断网、网络抖动、恢复网络：断网时停止活动；恢复时使用剩余预算；短暂 Playing 后再失败不会让次数归零。
3. 建源超时后快速换线、刷新和关闭窗口：旧结果不能重新播放；并发操作不超过限制。操作迟到结束后配额可回收。
4. 恢复停止后手动刷新，以及正常播放、手动切清晰度、弹幕和多窗口播放。
5. 关闭运行日志时不新增日志；打开后可见状态变化；诊断本身不发起额外网络请求。

这些改动修复了应用侧可确认的重试与生命周期缺口。当前没有异常现场的 mihomo CPU profile，不能据此断言其原生热点已经定位或运行态高 CPU 已完全消失。
