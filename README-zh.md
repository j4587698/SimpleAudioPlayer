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
