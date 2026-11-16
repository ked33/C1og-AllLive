<p align="center">
    <img width="128" src="/Assets/Logo.png" alt="All Live logo">
</p>
<h2 align="center">聚合直播</h2>
<p align="center">
聚合多个站点，简简单单看直播。
</p>


![Screenshot](/Assets/UWP2.png)


## 支持直播平台：

- 🐅 虎牙直播

- 🐟 斗鱼直播

- 📺 哔哩哔哩直播

- 🎵 抖音直播

## UWP安装

<p align="left">
    <a title="从 Microsoft Store 中获取" href="https://apps.microsoft.com/detail/9n1twg2g84vd" target="_blank">
        <img src="https://get.microsoft.com/images/en-US%20dark.svg" width=120 />
    </a>
</p>

可以在Microsoft Store中搜索“[聚合直播](https://apps.microsoft.com/detail/9n1twg2g84vd)”下载安装。

免费试用永不过期，购买应用视为无偿赞助作者。


系统要求：Windows 10 1903及以上版本。

支持平台：x86、x64、ARM、ARM64。

如果你想在其他系统上使用，请查看[Simple Live](https://github.com/xiaoyaocz/dart_simple_live)项目。

### GitHub Actions 安装包（x64）

本仓库的 Actions 产物与上面的商店安装方式独立。构建成功后，按需下载：

| Artifact | 内容 | 用途 |
| --- | --- | --- |
| `AllLive-UWP-Packages` | `AllLive.cer`、`Install-AllLive.ps1`、`AllLive.UWP_<版本>_x64.msixbundle` | 安装、更新（不含依赖） |
| `AllLive-UWP-Dependencies-x64` | `x64/` 下的框架 `.appx` 包 | 单独补充依赖时使用 |

从 2.3.65.0 起，Actions 的 Windows x64 包内置本地签名与 B 站流转发服务。直接打开 AllLive 即会启动服务，关闭最后一个窗口后自动退出；无需运行旧 PS 启动脚本，也无需另装 .NET。最小化不会停止服务，诊断仍由“运行日志”开关控制。参见[内置服务说明](docs/bundled-local-service.md)。

安装和更新步骤：

1. 将 `AllLive-UWP-Packages` 的三个文件完整解压到同一目录，右键 `Install-AllLive.ps1` →“使用 PowerShell 运行”（Windows 11 可能需先点“显示更多选项”）。无需安装 PowerShell 7；从 PowerShell 7 启动也会自动交给 Windows 自带 PowerShell 5.1 执行。
2. 缺少本地依赖文件时，脚本会提示从同次 Actions 构建下载 `AllLive-UWP-Dependencies-x64`。把其中的 `x64/` 目录解压到脚本旁，或放到 `Dependencies/x64/`，重新运行即可自动安装依赖。系统已安装所需依赖时可直接继续更新，无需重复下载。
3. 需要导入证书时确认管理员授权。脚本将证书导入“本地计算机 → 受信任的人”，随后自动为当前用户安装或更新 AllLive。更新会关闭正在运行的 AllLive 窗口，无需先卸载，已有设置和收藏保留。
4. 出现 `[OK] AllLive ... installed.` 即完成；同版本或已装更高版本会跳过，不会降级。结束时按回车关闭，失败时可查看具体错误；取消证书授权会停止后续安装。也可手动导入证书、双击 `.msixbundle`。

如执行策略阻止运行，可在解压目录打开终端，执行以下命令（仅本次进程绕过策略，不修改系统设置）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AllLive.ps1
```

需要跳过结束暂停时追加 `-NoPause`。如需手动补装依赖，可在依赖产物的解压目录打开 PowerShell，执行：

```powershell
Get-ChildItem -LiteralPath './x64' -Filter '*.appx' -File | ForEach-Object { Add-AppxPackage -Path $_.FullName -ErrorAction Stop }
```

签名逻辑保持原样：未配置完整签名 Secrets 时，每次构建都会生成新的自签名证书，因此需要导入对应版本的证书。`AllLive.cer` 与原产物中的版本化 `.cer` 是同一份证书，精简包只保留前者。

两个 Artifact 均使用 ZIP 的最高压缩等级 `9`。主产物不包含 CMD 启动器、依赖目录、重复证书和调试符号。MSIX 和依赖包本身已经压缩，主要体积收益来自精简文件；不要拆改已签名的 `.msixbundle` 内部内容。

## 项目结构

- `AllLive.Core` 项目核心库，实现获取各个网站的信息及弹幕。
- `AllLive.ConsoleApp` 基于AllLive.Core实现的控制台程序。
- `AllLive.UWP` 基于AllLive.Core实现的UWP户端。


## 参考及引用
[https://github.com/wbt5/real-url](https://github.com/wbt5/real-url)

[https://github.com/lovelyyoshino/Bilibili-Live-API/blob/master/API.WebSocket.md](https://github.com/lovelyyoshino/Bilibili-Live-API/blob/master/API.WebSocket.md)

[https://github.com/IsoaSFlus/danmaku](https://github.com/IsoaSFlus/danmaku)

[https://www.cnblogs.com/sdflysha/p/20210117-douyu-barrage-with-dotnet.html](https://www.cnblogs.com/sdflysha/p/20210117-douyu-barrage-with-dotnet.html)

[https://github.com/BacooTang/huya-danmu](https://github.com/BacooTang/huya-danmu)

[https://github.com/TarsCloud/Tars](https://github.com/TarsCloud/Tars)

## 声明

本项目的所有功能都是基于互联网上公开的资料开发，无任何破解、逆向工程等行为。

本项目仅用于学习交流编程技术，严禁将本项目用于商业目的。如有任何商业行为，均与本项目无关。

如果本项目存在侵犯您的合法权益的情况，请及时与开发者联系，开发者将会及时删除有关内容。

## 赞助

如果觉得此项目有用，可以考虑赞助我喝杯咖啡❤

[爱发电](https://afdian.net/a/xiaoyaocz)

[应用商店购买](https://apps.microsoft.com/detail/9n1twg2g84vd)