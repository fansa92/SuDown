# SuDown

一个用 .NET 10 + [Spectre.Console](https://spectreconsole.net/) 写的命令行下载器，可执行文件名是 `su`。

它不追求功能列表的长度，追求两件事：**下完的文件一定是完整的**，以及**需要鉴权的资源也能下得动**。

## 关于

这个项目的定位很窄：**把 HTTP(S) 上的文件可靠地拿到本地**。

常见的选择里，GUI 下载器功能全，但要安装、要配置；`curl` 够轻，却不管分片、续传和进度；`aria2` 很强大，但配置文件与 RPC 参数有学习成本。SuDown 想做中间那一个：单文件可执行、开箱即用，把「多线程分片 + 断点续传 + 全局限速 + 鉴权请求」这几件最常用的事做对，不往上堆 BT、HLS 这类属于另一套体系的功能。

适合的场景：

- 在脚本或 CI 里批量下载产物，需要失败重试和完整性校验；
- 下载带防盗链、需要 Cookie 或 Token 的资源；
- 网络不稳定，中断后不想从头再来。

它不是 aria2 的替代品，也不打算做成通用下载引擎。功能边界写在[已知限制](#已知限制)里。

## 特性

**下载内核**

- **分片多线程**：大于 1 MB 的文件按连接数切分，默认 4 连接并行下载。
- **可恢复的断点续传**：同目录生成 `<文件名>.sudown` 状态文件，逐分片记录已完成字节和远端 ETag/Last-Modified。进程被强杀、断网、Ctrl+C 中断后重新执行同一命令即可接着下，不会把"大小对但内容是空洞"的文件误判成已完成。
- **全局限速**：`-s` 是令牌桶对所有连接的总限速，不是每连接各限一次。
- **多源镜像**：`-m` 挂多个镜像 URL，分片按源调度，单源故障自动切到其他源。
- **多文件并发**：`-j N` 同时下载多个文件，限速仍是全局共享。
- **完整性校验**：结束时核对文件长度；`--sha256` 可再比对摘要，不匹配直接判失败。
- **自适应探测**：HEAD 被拒（405/403）时回退 `GET Range: bytes=0-0`；服务器不支持 Range 时自动降级单线程，不会写出错位文件。

**请求能力（HTTPie 风格）**

- `-H` 自定义请求头，解决防盗链的 Referer / Cookie / API Key。
- `-a` + `-A` 认证：Basic、Bearer、Digest、JWT、OAuth。
- `--session` 跨进程复用 Cookie、自定义头和认证；自动读取 `~/.netrc`。
- `--verbose` 打印真实请求/响应头与重定向链，敏感值自动打码。
- `--offline` 只打印将要发出的请求，不联网。
- 手动跟随重定向并记录每一跳，**跨主机跳转时自动移除凭据**。

**工程细节**

- 文件名优先取 `Content-Disposition`（含 RFC 5987 的 `filename*`），其次 URL 末段，再按 `Content-Type` 推断扩展名；清洗非法字符并处理 Windows 保留名；同名不覆盖，自动加 `(1)`。
- 4xx 直接失败不重试，只有 5xx / 超时 / IO 异常才重试。
- 连接超时与读空闲超时分离，不受 HttpClient 整体超时限制，大文件慢速网络不会被拦腰砍断。
- 跨平台：Windows / Linux / macOS，x64 与 arm64。

## 安装

### 下载预编译版本

到 [Releases](https://github.com/fansa92/SuDown/releases) 下载对应平台的压缩包，解压即可得到 `su`（Windows 是 `su.exe`）。

单文件发布已内置 .NET 运行时，目标机器**不需要安装 .NET**。

### 从源码构建

需要 .NET 10 SDK。

```bash
git clone https://github.com/fansa92/SuDown.git
cd SuDown
dotnet build -c Release
# 产物：bin/Release/net10.0/su.exe（Windows）或 bin/Release/net10.0/su（Linux/macOS）
```

发布为当前平台的单文件：

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 快速开始

```bash
# 单个文件
su https://example.com/file.zip

# 多个文件、指定目录
su https://a.com/1.zip https://b.com/2.zip -d D:\Downloads

# 从文件批量读取（每行一个 URL，# 开头为注释）
su -l urls.txt -d D:\Downloads

# 限速 500 KB/s（所有连接合计）
su https://example.com/big.iso -s 500000

# 需要防盗链头的资源
su https://cdn.example.com/video.mp4 -H "Referer: https://example.com/"

# Basic 认证
su https://example.com/private.zip -a user:pass

# Bearer Token，并把 Cookie 持久化到会话
su https://api.example.com/export -a "your-token" -A bearer --session work

# 精确输出路径
su https://example.com/data.json -o ./data/2026-11-23.json

# 下载前先确认请求头对不对
su https://example.com/file.zip -H "Referer: https://example.com/" --offline

# 下载并校验 SHA-256
su https://example.com/file.zip --sha256 <64位十六进制摘要>
```

## 命令选项

`su` 的默认命令就是 `download`，所以可以省略：`su <url>` 等价于 `su download <url>`。

| 选项 | 说明 |
| --- | --- |
| `[URLS]` | 要下载的 URL，可传多个，空格分隔 |
| `-l, --list` | 从文件批量读取 URL，每行一个，`#` 开头为注释 |
| `-d, --dir` | 保存目录，默认当前目录 |
| `-o, --output` | 精确输出文件路径（仅限单文件；与 `-d` 冲突时以本项为准） |
| `--log` | 日志文件输出路径 |
| `-s, --max-speed` | 总限速，字节/秒（例如 `102400` = 100 KB/s） |
| `-c, --connections` | 单文件最大连接数，覆盖配置 |
| `-j, --parallel-files` | 同时下载的文件数，默认 1 |
| `-m, --mirror` | 镜像源 URL，可重复传入，与主 URL 下载同一文件 |
| `--no-resume` | 禁用断点续传，从头下载 |
| `-f, --overwrite` | 强制重新下载，覆盖已存在文件 |
| `--single` | 强制单线程 |
| `--retries` | 失败重试次数，覆盖配置 |
| `--idle-timeout` | 读空闲超时秒数，覆盖配置 |
| `--config` | 指定配置文件路径 |
| `--sha256` | 下载完成后校验 SHA-256，不匹配则删除文件并返回失败 |
| `-H, --header` | 自定义请求头 `Name: Value`，可重复 |
| `-a, --auth` | 凭据：`user:pass`（Basic）或 token（Bearer） |
| `-A, --auth-type` | 认证方式：`basic` / `bearer` / `digest` / `jwt` / `oauth`，默认 `basic` |
| `--session` | 会话名或 `.json` 路径，持久化 Cookie、自定义头与认证 |
| `--no-netrc` | 禁用自动读取 `~/.netrc` |
| `--max-redirects` | 最多跟随多少次重定向，默认 10，`0` 表示不跟随 |
| `--verbose` | 打印请求头、响应头和重定向链，敏感值打码 |
| `--offline` | 只打印请求，不发起网络请求 |
| `-v, --version` | 显示版本（注意：`-v` 是版本，详细模式用 `--verbose`） |
| `-h, --help` | 显示帮助 |

**优先级**：命令行 `-H` > `--session` > `~/.netrc` > 配置文件。

## 配置文件

默认读取 `appsettings.json`，查找顺序为：当前目录 → 可执行文件所在目录 → 用户配置目录。可用 `--config` 指定。

```json
{
  "Download": {
    "MaxConnections": 4,
    "MaxParallelFiles": 1,
    "MinSizeForMultiThread": 1048576,
    "ConnectTimeoutSeconds": 30,
    "ReadIdleTimeoutSeconds": 60,
    "MaxRetries": 3,
    "RetryDelaySeconds": 2,
    "UserAgent": "SuDown/2.0",
    "EnableResume": true,
    "EnableMultiThread": true
  },
  "Proxy": {
    "Enabled": false,
    "Address": "http://127.0.0.1:7890",
    "Username": "",
    "Password": ""
  },
  "Request": {
    "Headers": {},
    "Auth": "",
    "AuthType": "",
    "Session": "",
    "UseNetrc": true,
    "Verbose": false,
    "MaxRedirects": 10
  }
}
```

配置逐字段读取，缺少某项时只回退该项默认值，不会因为一个字段写错就整体失效。

## 断点续传是怎么工作的

1. 探测远端长度、Range 支持情况和 ETag/Last-Modified。
2. 若存在 `.sudown` 状态文件且远端标识一致，按分片已完成的字节继续；远端已变化则丢弃旧状态重新下载。
3. 下载过程中状态文件按 1 秒或 4 MB 双阈值落盘，被强杀最多损失 1 秒进度。
4. 所有分片完成后核对文件长度，可选校验 SHA-256，通过后删除 `.sudown`。

```bash
# 中断后原样重跑即可
su https://example.com/big.iso
```

> 状态文件是纯文本 JSON，记录的是分片偏移和进度，不包含凭据。会话文件（`*.session.json`）包含 Cookie 和认证信息，注意不要提交到仓库或分享。

## 退出码

| 退出码 | 含义 |
| --- | --- |
| `0` | 全部成功 |
| `1` | 有文件下载失败或校验不通过 |
| `130` | 被 Ctrl+C 中断（进度已保存，可续传） |

## 开发与测试

项目自带一个零依赖的本地测试服务器，覆盖 Range、HEAD 405、忽略 Range、服务端限速、`Content-Disposition`、Basic/Bearer 认证、Cookie、重定向链等场景。

```bash
# 启动测试服务器（需要 Node.js）
node tests/server.js 8899

# 另开终端测试
dotnet run -c Release -- "http://127.0.0.1:8899/file/3000000/sample.bin" -d ./tmp
```

测试端点：

| 路径 | 用途 |
| --- | --- |
| `/file/:size/:name` | 普通文件，可用 `?mode=norange` / `nohead` / `drop` / `bps=` 模拟异常 |
| `/auth/basic/:size/:name` | 需要 Basic `user:pass` |
| `/auth/bearer/:size/:name` | 需要 `Bearer secret-token` |
| `/auth/header/:size/:name` | 需要 `X-Api-Key: expected-key` |
| `/setcookie/:size/:name` | 先下发 Cookie 再 302 跳转 |
| `/redirect/:size/:name?hops=N` | N 跳重定向 |
| `/sha256/:size/:name` | 返回对应内容摘要，用于校验测试 |

## 发布

推送 `v*` 标签会触发 [`.github/workflows/release.yml`](.github/workflows/release.yml)，用 GitHub Actions 构建六个平台的单文件并创建 Release：

```bash
git tag v1.0.0
git push origin v1.0.0
```

构建矩阵：

| 平台 | RID |
| --- | --- |
| Windows x64 | `win-x64` |
| Windows arm64 | `win-arm64` |
| Linux x64 | `linux-x64` |
| Linux arm64 | `linux-arm64` |
| macOS Intel | `osx-x64` |
| macOS Apple Silicon | `osx-arm64` |

也可以在 Actions 页面手动触发 `Release` 工作流并填入版本号。

## 已知限制

- **不解析 HLS/m3u8**：只处理直接返回文件字节的 URL。
- **不支持 BT / 磁力链**。
- **单文件多源**：`-m` 是手写镜像地址，不做自动测速和镜像发现。
- 未启用 NativeAOT；单文件依赖 .NET 运行时打包，体积约 30–70 MB。Spectre.Console 在 AOT 裁剪下需要额外适配，暂未处理。
