# ArchoeraMvsync

开源、多平台音乐播放器：Flutter 前端 + 自研 C/Zig 音频内核 + 本地曲库/流媒体服务端。

- 本地曲库、离线播放与自建媒体服务
- 三端打包与发布流程见仓库根目录 [`README.md`](../README.md) 与 [`docs/`](../docs/)

## 开发

```bash
flutter pub get
dart analyze lib
flutter test
flutter run -d linux
```

更完整的构建/打包说明见 [`docs/user-build-from-source.md`](../docs/user-build-from-source.md)。
