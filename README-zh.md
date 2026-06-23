# SimpleAudioPlayer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

[English](README.md)

一个简单易用的跨平台音频播放库，基于 **SimpleAudioPlayer.Native (LGPL-2.1+)** 后端实现，支持多种音频格式和流媒体协议。

## 特性

- 🎵 支持常见音频格式（通过 FFmpeg 解码）
- 📁 多协议支持：本地文件、HTTP流、自定义流
- ⏯️ 基础播放控制：播放/暂停/停止/跳转
- ⏲️ 获取播放时长和当前进度
- 🔧 可扩展的流处理系统（支持自定义数据源）

## 2.0 更新

- Native 依赖升级到 **SimpleAudioPlayer.Native 2.0.0**。
- 播放失败会通过 `PlaybackFailed` 和 `PlaybackState.Error` 暴露给调用方。
- HTTP 流断开会报告 I/O 错误，不再静默当作正常 EOF。
- `ProgressiveHttpStreamHandle` 支持边下边播，并在下载完成后落到最终本地文件。
- `DiskCachedStreamHandle` 支持磁盘缓存，避免大文件全部放进内存。

## 安装

通过 NuGet 安装：
```bash
Install-Package SimpleAudioPlayer -Version 2.0.0
```

## 快速开始
```
// 创建播放器实例
var player = new AudioPlayer();

// 使用文件流（支持本地路径）
player.Load(new FileStreamHandle("song.mp3"));

// 获取音频总时长
TimeSpan duration = player.GetDuration();

// 播放控制
player.Play();
player.Stop();
player.Pause();

// 进度操作
var currentTime = player.GetTime();
player.Seek(30);
```

## 错误处理
```csharp
player.PlaybackStateChanged = state =>
{
    Console.WriteLine($"播放状态：{state}");
};

player.PlaybackFailed = args =>
{
    Console.WriteLine($"播放失败：{args.Result}");
    Console.WriteLine(args.Exception);
};
```

## HTTP 边下边播
```csharp
var handle = await ProgressiveHttpStreamHandle.CreateAsync(
    "https://example.com/song.mp3",
    "song.mp3",
    overwrite: true);

handle.ProgressChanged += (downloaded, total) =>
{
    Console.WriteLine($"{downloaded}/{total}");
};

handle.DownloadStateChanged += (_, args) =>
{
    Console.WriteLine($"下载状态：{args.State}");
};

player.Load(handle);
player.Play();
```

`ProgressiveHttpStreamHandle` 会把未完成数据保存在 `song.mp3.part`，并写入紧凑的二进制索引 `song.mp3.part.idx`。默认 `resume: true` 会复用有效缓存区间，包括跳转播放后下载到的 Range 区间；如果 URL 身份或远端校验信息不匹配，会丢弃旧缓存并重新下载。

## Stream 边下边播
```csharp
using var input = await httpClient.GetStreamAsync("https://example.com/song.mp3");
using var handle = new DiskCachedStreamHandle(input);

player.Load(handle);
player.Play();
```

`DiskCachedStreamHandle` 是基于 `Stream` 的边下边播路径。没有显式传入缓存路径时，它会写入临时 `.part` 文件，并在 Dispose 时删除。源 `Stream` 本身可 seek 时，它会向 FFmpeg 声明 seek 能力；对于不可 seek 的 Stream，只有确实需要数据到达后的缓存内 seek 时才显式传 `enableSeek: true`。

如果要把完整读取后的 Stream 保存成缓存文件，可以指定最终缓存路径并启用完成提交：

```csharp
using var handle = new DiskCachedStreamHandle(
    input,
    totalSize: contentLength,
    cacheFilePath: "song.mp3",
    commitCacheOnComplete: true);
```

读取过程中会写入 `song.mp3.part`。只有完整读取成功后才会移动为 `song.mp3`；失败或未达到 `totalSize` 的 Stream 会删除临时 `.part` 文件。

## 流处理支持

|处理器类型|描述|
|---------|----|
|FileStreamHandle|本地文件流|
|HttpStreamHandle|HTTP网络流|
|StreamHandle|通用流（需提供Stream对象）|
|CustomHandle|完全自定义实现|
|CachedStreamHandle|网络流缓存支持|
|DiskCachedStreamHandle|适合大文件的磁盘缓存流|
|ProgressiveHttpStreamHandle|HTTP 边下边播，并保存为最终本地文件|

## 依赖说明
- 后端使用 [miniaudio](https://github.com/mackron/miniaudio) 进行音频播放
- 音频解码通过 [FFmpeg](https://ffmpeg.org/) 实现
- Native组件使用 [SimpleAudioPlayer.Native 2.0.0](https://github.com/j4587698/SimpleAudioPlayer.Native) (LGPL-2.1+)

## 许可证
主项目采用 MIT License
Native组件部分采用 [LGPL-2.1+](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html)
注意：使用本库时需要遵守LGPL协议的相关要求

## 贡献
欢迎提交 Issue 和 PR，建议包括：

- 问题重现步骤
- 相关日志/错误信息
- 运行环境信息（OS/.NET版本等）

## License 
![license](https://img.shields.io/github/license/j4587698/SimpleAudioPlayer)
