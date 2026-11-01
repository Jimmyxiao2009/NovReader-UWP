# NovReader

面向 **Windows 10 Mobile ARM32** 的原生 C#/XAML 小说与漫画阅读器。应用实现了部分 Legado（阅读）书源规则，并通过随包携带的 JavaScriptCore 原生桥在手机本地执行书源脚本。

## 当前功能

- JSON / URL 书源导入、书源管理和书源广场，聚合搜索、书籍详情、目录与换源。
- 小说正文阅读、阅读进度、主题与字体设置，章节缓存和全本下载。
- 漫画图片并发下载、文件缓存、失败图片重试和单图缩放查看。
- 书签与备注、本章文字查找，以及 TXT / HTML / Markdown / EPUB 导出。

规则引擎覆盖常用 CSS/Jsoup 风格、XPath、JSONPath、正则和 JavaScript，**不保证兼容全部 Legado 书源或宿主扩展**。完整差距见 [LEGADO_GAP_AUDIT.md](LEGADO_GAP_AUDIT.md)，阅读工具说明见 [READING_FEATURES.md](READING_FEATURES.md)。

## 平台与原生依赖

工程使用 Windows SDK **10.0.15063.0**，清单最低版本为 **10.0.10240.0**。历史交接记录包含 Lumia 950 / 15254 的运行验证；清单最低值不等同于已在每个旧版本手机上验收。

正式手机包使用 **Release / ARM / .NET Native**。当前 JSC 和原生网络组件只有 ARM32 版本，不能将桌面 x86/x64 编译当作完整书源兼容性验证。

以下 DLL 位于 `NovReader/`，由工程作为本地 Content 打包，不需要远程 JS 服务：

- `JsBridge.dll`、`icuuc78.dll`、`icuin78.dll`、`icudt78.dll`。
- `NetBridge.dll`、`libcurl.dll`、`libssl-3-arm.dll`、`libcrypto-3-arm.dll`、`z.dll`。

## 源码与构建

| 路径 | 内容 |
|---|---|
| `NovReader/NovReader.csproj` | UWP 应用工程 |
| `NovReader/Services/Legado/` | 规则、请求、书源与 JSC 托管桥 |
| `NovReader/Views/` | 书架、搜索、阅读、书源和设置界面 |
| `JsBridge/` | JavaScriptCore / 原生网络桥源码与开发机构建脚本 |
| `HANDOFF.md` | 历史构建、部署与问题定位记录 |

已有上述 ARM32 DLL 时，在仓库根目录使用 VS2017 MSBuild 15 与相应 UWP 工作负载构建：

```powershell
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'
& $msbuild 'NovReader\NovReader.csproj' /t:Restore,Build `
  /p:Configuration=Release /p:Platform=ARM /p:VisualStudioVersion=15.0 `
  /p:UseDotNetNativeToolchain=true /p:GenerateAppxPackageOnBuild=true `
  /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false `
  /p:AppxGeneratePrisForPortableLibrariesEnabled=false
```

输出位于 `NovReader/AppPackages/`；此命令生成的包尚需使用 Publisher 为 `CN=NovReader` 的开发证书签名。`JsBridge/build-arm-package.ps1` 还包含原生桥重编与签名流程，但依赖开发机上的 Apotheosis、LLVM、ICU、vcpkg 和证书路径，不是独立克隆后即可运行的一键入口。

## 获取与限制

从 [GitHub Releases](https://github.com/Jimmyxiao2009/NovReader-UWP/releases) 获取安装 ZIP，信任其中的公开开发证书，并安装所需 **ARM** 框架依赖后再安装主包。不要使用缺少手机运行时依赖的 Debug 包替代 Release ARM 包。

尚未完整实现书源登录/验证码、JS 上下文隔离与超时、WebP/AVIF 软件解码、本地 TXT/EPUB 导入、正文 TTS 或 WebDAV 同步；EPUB 导出不代表支持 EPUB 阅读。外部书源可能失效，脚本和图片格式也可能超出当前兼容范围。

2026-11-01 的更新已检查 ARM32 包、签名、依赖和图标；本轮未重新进行手机阅读、缩放或查找交互验收。
