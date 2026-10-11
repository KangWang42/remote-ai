# Remote CLI 项目规则

- 产品由 Android App 和电脑端组成；Windows 电脑端以 EXE 安装包发布。`web/` 是 App 使用、由电脑端提供的内嵌页面和终端组件，不作为独立网页产品介绍。
- 用户要求（2026-10-11 起）：改动完成并通过相关检查后提交即可，不必每次发版，也不推送；用户说要发布时，再递增版本号，重新构建 EXE 和 APK，并发布对应的 GitHub Release。只改源码或重打同版本安装包不算发布完成。
- Android 的 `versionName`、Windows 程序和安装器版本、中转服务的 `VERSION`、打包脚本版本以及 Release 的 `vX.Y.Z` 标签必须一致；Android `versionCode` 也必须高于上一正式版。Rust/Linux 电脑端沿用各自版本约定。
- 发布时上传 `RemoteCli-Setup-X.Y.Z.exe`、`RemoteCli-Android.apk` 和与这两个文件匹配的 `SHA256SUMS.txt`。APK 签名证书必须与上一正式版一致，不直接使用未经比对的默认密钥；必要时用 `android/build.ps1 -SecretDir` 指定既有发布密钥目录。确认 Release 标签指向此次已验证的提交，公开版本和文件摘要与本地成品一致；不覆盖旧版附件，不改写远端历史。
- 用户要求：项目保持清晰、可扩展的结构，不做无意义的堆砌。新能力以职责单一、接口明确的模块加入（例如 `agent-windows/Relays.cs`、`public-relays.json`、`relay.Relay`）；状态放在可以有多个实例的对象里，不靠全局变量或重复加载模块；改动同时带上测试和文档（涉及三方接口的写进 `docs/PROTOCOL.md`），并去掉因此不再需要的代码。
- README 和 Release 说明直接描述功能、变化及必要的使用限制，使用自然、客观的中文；不加入助手自述或“未核验”“没有在手机上看过”等制作过程说明，不虚构测试证据。
