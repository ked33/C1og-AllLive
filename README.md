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
| `AllLive-UWP-Packages` | 同级的 `AllLive.cer` 和 `AllLive.UWP_<版本>_x64.msixbundle` | 日常安装、更新 |
| `AllLive-UWP-Dependencies-x64` | `x64/` 下的框架 `.appx` 包 | 首次安装缺少依赖、离线安装时使用 |
| `AllLive-UWP-Symbols-x64` | `.appxsym` 调试符号 | 开发者排查崩溃，普通安装不需要 |

安装和更新步骤：

1. 解压 `AllLive-UWP-Packages`，将本次构建的 `AllLive.cer` 导入“本地计算机 → 受信任的人”。
2. 如果缺少框架依赖，另外下载并解压同次构建的 `AllLive-UWP-Dependencies-x64`，先安装其中的 x64 `.appx` 包。
3. 双击 `.msixbundle` 安装或更新，无需先卸载旧版。包标识需一致，更新版本需高于已安装版本。

如需一次安装所附依赖，可在依赖产物的解压目录打开 PowerShell，执行：

```powershell
Get-ChildItem -LiteralPath './x64' -Filter '*.appx' -File | ForEach-Object { Add-AppxPackage -Path $_.FullName -ErrorAction Stop }
```

签名逻辑保持原样：未配置完整签名 Secrets 时，每次构建都会生成新的自签名证书，因此需要导入对应版本的证书。`AllLive.cer` 与原产物中的版本化 `.cer` 是同一份证书，精简包只保留前者。

三个 Artifact 均使用 ZIP 的最高压缩等级 `9`。主产物不包含重复证书、调试符号、其他架构依赖以及安装辅助脚本。MSIX 和依赖包本身已经压缩，主要体积收益来自精简文件；不要拆改已签名的 `.msixbundle` 内部内容。

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