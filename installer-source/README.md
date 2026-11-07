# P5S v1.1.0 安装器源码

源码对应 v1.1.0 发布压缩包。安装包面向 Steam 原版游戏文件。

运行 Build.cmd 使用 Windows .NET Framework x64 编译器生成安装器。将发布包的 Core/patches 和 Core/components 目录复制到 build/Core 中，即可使用生成的脚本。

review/manifest.json 与发布包相同，src/P5SPatch.cs 固定校验该清单。review/release-file-hashes.json 记录发布包与资源校验值。

安装、修复、卸载共用资源区域处理及事务恢复。输入组件的源文件放在仓库 native-input-source 目录，相关第三方许可随组件提供。
