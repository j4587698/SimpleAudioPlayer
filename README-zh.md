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

## 安装

通过 NuGet 安装：
```bash
Install-Package SimpleAudioPlayer
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

## 流处理支持

|处理器类型|描述|
|---------|----|
|FileStreamHandle|本地文件流|
|HttpStreamHandle|HTTP网络流|
|StreamHandle|通用流（需提供Stream对象）|
|CustomHandle|完全自定义实现|
|CachedStreamHandle|网络流缓存支持|

## 依赖说明
- 后端使用 [miniaudio](https://github.com/mackron/miniaudio) 进行音频播放
- 音频解码通过 [FFmpeg](https://ffmpeg.org/) 实现
- Native组件使用 [SimpleAudioPlayer.Native](https://github.com/j4587698/SimpleAudioPlayer.Native) (LGPL-2.1+)

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
