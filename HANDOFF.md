# NovReader 交接文档

## 1. 项目目标
面向 Lumia 950 / Windows 10 Mobile 的 UWP 小说阅读器，核心是**兼容 Legado（阅读）书源格式**：
书源管理 → 在线书源广场（支持一键全部导入）→ 并行搜索 → 书籍详情 → 多线程下载 → 阅读 → 多格式导出。

## 2. 平台约束（真机目标）
- 设备：Lumia 950；系统：Windows 10 Mobile
- UI：旧版 UWP XAML，不使用 WinUI 3 / Windows App SDK
- 语言：C#

**本机开发环境与真机目标不一致，必须分清：**
- 本机**没有** 10.0.10586.0 SDK（磁盘上只有 10.0.10240.0、15063.0 等）。
- 本机 VS2015（14.0.25420.1）**缺少 UWP 项目系统**，devenv 迁移报告始终报错找不到项目类型
  `{A5A43C5B-DE2A-4C0C-9213-0A381AF9435A}`。
- 因此**桌面验证统一用 VS2017 MSBuild 15.9 + UWP framework package 6.2.14 + TargetPlatformVersion 10.0.15063.0**。
- 真机 10586 部署仍未验证，属于遗留风险（见第 11 节）。
- 当前源码已用 VS2017 MSBuild 15.9 成功生成 ARM Debug AppX：`NovReader/AppPackages/NovReader_1.0.0.0_ARM_Debug_Test/NovReader_1.0.0.0_ARM_Debug.appx`；ARM EXE 与 appxsym 同目录生成。包已添加开发签名，证书文件为 `NovReader/NovReader.cer`，但证书是否被 Lumia 950 信任仍需真机侧确认。
- 针对 Lumia 950 安装后点击无响应，已移除 `App` 构造函数中 `CodePagesEncodingProvider.Instance` 的启动期调用。该类型来自较新的 .NET/UWP facade，在 Windows 10 Mobile 10586 上可能触发启动阶段 TypeLoadException；ARM 包已重新编译并签名。最近一次 ARM AppX SHA-256 为 `B6B262730D59D50777790CAF7A02FD8F39C329DDD301BB9B25378657C6D13F78`。

### 2.1 真机（Lumia 950）“装得上、点不开”的真正原因（已定位并修复）

真机实测系统为 **Windows 10 Mobile 15254.603（feature2_rs3svc，即 1709 / RS3）**，不是 10586。
通过 Windows Device Portal（`http://192.168.3.159`）拿到关键证据：

1. 应用能安装成功，但启动调用返回
   `HRESULT 0x8027025B` / “The app didn't start”。
2. 手机上**没有产生任何 NovReader 崩溃转储**（`/api/debug/dump/usermode/dumps` 里只有别的包），
   说明失败发生在**激活阶段**，应用代码根本没跑起来——所以之前改 `App` 构造函数是无效方向。
3. 解包 AppX 检查生成的 `AppxManifest.xml`，发现 CoreCLR 版本声明了：
   `<PackageDependency Name="Microsoft.NET.CoreRuntime.1.1" MinVersion="1.1.27004.0" .../>`，
   而 Lumia 950 上没有满足该版本的核心运行时，依赖不满足 → 系统直接拒绝激活。

**修复方式：ARM Release 打开 .NET Native 工具链**（`NovReader.csproj`）：

```xml
<PropertyGroup Condition="'$(Configuration)|$(Platform)' == 'Release|ARM'">
  ...
  <UseDotNetNativeToolchain>true</UseDotNetNativeToolchain>
</PropertyGroup>
```

.NET Native 版本不再依赖 `Microsoft.NET.CoreRuntime.1.1`，改为依赖
`Microsoft.NET.Native.Framework.1.7` / `Microsoft.NET.Native.Runtime.1.7` / `Microsoft.VCLibs.140.00`。

部署时**必须把依赖一起装上**（只装主包仍会激活失败）：

```powershell
curl.exe -sS -X POST `
  -F "NovReader_1.0.0.0_ARM.appx=@NovReader_1.0.0.0_ARM.appx;type=application/octet-stream" `
  -F "Microsoft.NET.Native.Framework.1.7.appx=@Dependencies\arm\Microsoft.NET.Native.Framework.1.7.appx;type=application/octet-stream" `
  -F "Microsoft.NET.Native.Runtime.1.7.appx=@Dependencies\arm\Microsoft.NET.Native.Runtime.1.7.appx;type=application/octet-stream" `
  "http://192.168.3.159/api/app/packagemanager/package?package=NovReader_1.0.0.0_ARM.appx"

# 启动（appid 必须是 base64 的 AUMID）
$b=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('NovReader_7xxdx2kd68cm6!App'))
Invoke-WebRequest -Uri "http://192.168.3.159/api/taskmanager/app?appid=$b" -Method Post -UseBasicParsing
```

修复后实测：启动调用返回成功，`/api/resourcemanager/processes` 能看到
`NovReader_1.0.0.0_arm__7xxdx2kd68cm6` 进程（PID 6892），且没有新增崩溃转储。

当前 Release ARM 包：`NovReader/AppPackages/NovReader_1.0.0.0_ARM_Test/NovReader_1.0.0.0_ARM.appx`
（.NET Native，已签名，SHA-256 `4C89E4306CD3EBE359E435060166BA9CEAB74F2C8F5C1EE7854B453A9C5F80DF`）。

**注意：真机部署必须用 Release|ARM（.NET Native）。Debug 包是 CoreCLR 版，依赖手机没有的
`Microsoft.NET.CoreRuntime.1.1`，一定会“点不开”。**

## 3. 本机构建 / 部署 / 测试（已验证可用）

```powershell
# 1) 构建（必须带这两个开关，否则 AppX 打包阶段会 MSB3816 失败）
cd H:\项目\UWP\NovReader\NovReader
& 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe' NovReader.csproj `
    /p:Configuration=Debug /p:Platform=x64 `
    /p:GenerateAppxPackageOnBuild=false /p:AppxGeneratePrisForPortableLibrariesEnabled=false `
    /t:Build

# 2) 部署：不用签名安装包，直接注册 loose layout（AppX 安装会因开发证书不受信任报 0x800B0109）
Add-AppxPackage -Register H:\项目\UWP\NovReader\NovReader\bin\x64\Debug\AppxManifest.xml -ForceUpdateFromAnyVersion

# 3) 启动
Start-Process 'shell:AppsFolder\NovReader_7xxdx2kd68cm6!App'

# 4) 离线单元测试（164 项，不联网）
cd H:\项目\UWP\NovReader\_devtest
& 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe' Harness.csproj /p:Configuration=Debug
.\bin\Harness.exe

# 5) 附加：外网冒烟 + 在线书源广场（真实网络）
.\bin\Harness.exe --network
# 6) 附加：扫真实在线书源（大多已失效，仅作诊断）
.\bin\Harness.exe --live
.\bin\Harness.exe --scan .\sources-backup.json
# 7) 诊断单个书源（按阅读器真实调用方式走搜索→目录→正文，打印前几章与警告）
.\bin\Harness.exe --probe .\某书源.json 斗破苍穹
```

测试台输出是 UTF-8。在 PowerShell 里用 `*>` 重定向会被按 GBK 再编码成乱码，
要落盘请用 `cmd /c "Harness.exe ... > out.txt"`。长时间扫描要放到独立后台进程里，
否则启动它的 shell 退出时会被一起结束。

**改 XAML 前先杀掉进程**，否则 `bin\x64\Debug\*.xbf` 被占用会报 MSB3021/MSB3027。

### 网络前提（很关键）
本机只能通过 `127.0.0.1:7897` 的本地代理访问外网，而 **UWP AppContainer 默认禁止回环**，
应用内所有网络请求都会失败（现象是 `HttpRequestException: 无法与服务器建立连接`）。已执行过：

```powershell
CheckNetIsolation LoopbackExempt -a -n=NovReader_7xxdx2kd68cm6
```

换机器或重装包后需要重新执行。

## 4. 目录结构

```
NovReader/
  App.xaml                       # 全局样式、配色画刷（深色模式改的就是这些画刷的 Color）
  App.xaml.cs                    # 启动、返回键、全局异常、注册 CodePagesEncodingProvider
  MainPage.xaml(.cs)             # 外壳：顶栏 + 汉堡菜单（SplitView）+ 内容 Frame；页面被缓存
  Models/
    BookSource.cs                # 书源 + SearchRule/BookInfoRule/TocRule/ContentRule
    Book.cs  Chapter.cs          # 书籍与章节
    ShelfBook.cs                 # 书架条目（含阅读进度）
    ReadingSettings.cs           # 阅读偏好 + 应用主题 + 下载/导出偏好
    TransferSettings.cs          # 下载线程数、导出格式与编码
  Services/
    AppState.cs                  # 全局共享状态（书源/书架/设置/引擎/缓存/日志）
    Nav.cs                       # 页面导航（ToLogged 会记日志）
    Dialogs.cs                   # ContentDialog 串行化（UWP 同时只允许一个）
    ThemeManager.cs              # 应用级深色模式
    SourceStore.cs ShelfStore.cs SettingsStore.cs SearchHistoryStore.cs
    ChapterCache.cs              # 正文缓存（内存 + 文件）
    TocCache.cs                  # 目录缓存（内存 + 文件）
    MultiSearch.cs               # 并行聚合搜索，结果流式回调
    DownloadService.cs           # 多线程下载全本
    ExportService.cs             # 多格式多编码导出
    Legado/                      # 规则引擎（不依赖 UWP API，可被控制台测试台直接编译）
      LegadoJson.cs Html.cs CssSelector.cs XPathQuery.cs JsonPath.cs
      RuleEngine.cs LegadoUrl.cs UrlRule.cs HttpFetcher.cs
      LocalTestSite.cs LegadoEngine.cs LegadoSourceJson.cs SourcePlaza.cs
  ViewModels/SearchResultItem.cs # 搜索结果的展示包装
  Views/
    ShelfPage.xaml(.cs)          # 书架（网格卡片、进度条、继续阅读、详情/移出）
    SearchPage.xaml(.cs)         # 搜索（聚合/指定书源、流式结果、排序、历史）
    BookDetailPage.xaml(.cs)     # 详情（简介、目录、下载全本、导出）
    ReaderPage.xaml(.cs)         # 阅读器（翻页/滚动、主题、字号、目录抽屉、进度记忆）
    SourcesPage.xaml(.cs)        # 书源管理 + 书源广场（含一键全部导入）
    SettingsPage.xaml(.cs)       # 外观、阅读、下载导出、数据、关于
    DebugPage.xaml(.cs)          # 最近请求、规则警告、操作日志、运行环境
_devtest/
  Harness.csproj Program.cs      # 控制台测试台（89 项离线 + --network/--live/--scan）
  common.ps1                     # UI Automation 共用函数（含陈旧窗口检测）
  drive*.ps1 drive-export.ps1    # 各场景驱动脚本
  loopback-server.ps1            # 本地 HTTP 夹具服务器
  sources-backup.json            # 一次真实导入后的书源快照
_shots/                          # 各阶段截图
```

## 5. 功能实现要点

### 书源广场 + 一键全部导入
- 从 `yuedu.miaogongzi.net/gx.html`、`jh.html` 解析 `yuedu://…?src=…` 导入链接，实测发现 **18 个合集**。
- 「一键全部导入」逐个下载解析、边导入边落盘，单个合集失败只记录不影响其它。
  实测：**16/18 成功，解析 1384 个书源，去重后新增 375 个**。
- 站点不可达时回落到内置地址列表，并在状态栏显示回落原因。

### 并行搜索 + 流式显示
- `MultiSearch` 按可调并发（默认 8 路，设置页可改 1~16）同时请求多个书源。
- **结果边搜边回传**：每完成一个书源就把新结果推给界面，不等全部跑完。
- 单书源失败/超时不影响其它书源；同一本书在多个书源的命中合并为一条（可换源）。
- 默认按相关度排序，可切到达顺序 / 书名 / 书源数，支持精准搜索与随时停止。

### 阅读器
- 翻页模式按**行高整数倍**滚动，页面边界永远落在行与行之间，不会切断文字；滚动模式为连续滚动。
- 主题：日间 / 羊皮纸 / 护眼绿 / 夜间 / 纯黑；字号、行距、页边距、字体、首行缩进、段间空行可调。
- 目录抽屉、章节滑块、上一章/下一章、沉浸阅读（隐藏状态栏）、阅读进度记忆。
- 键盘：← → 翻页，↑ ↓ 滚动，Esc 退出。

### 本地缓存（重点）
三级缓存，目标是**重新打开一本书不再重新加载**：
1. **目录缓存**（`TocCache`）：详情页与阅读器都优先读缓存，命中时状态栏显示「（本地缓存）」。
2. **正文缓存**（`ChapterCache`）：内存 + 文件两级，阅读时自动写入。
3. **相邻章节后台预取**：进入某一章后自动预取下一章、上一章、下下章。

### 下载与导出
- 「下载全本」用可调线程数并发抓取全部章节写入缓存，支持跳过已缓存章节、随时取消。
  实测：18 章 → 成功 13、跳过已缓存 5。
- 「导出」支持 **TXT / HTML / Markdown / EPUB** × **UTF-8 / UTF-8 BOM / UTF-16LE / GBK / Big5**。
  EPUB 为完整结构（`mimetype` 不压缩 + `container.xml` + `content.opf` + `toc.ncx` + `nav.xhtml` + 每章 xhtml）。
  导出后可直接「另存为」到用户指定位置。

### 应用级深色模式
`ThemeManager` 直接改 `App.xaml` 里画刷的 `Color` 实现整体换肤，同时设置
`FrameworkElement.RequestedTheme` 让系统控件模板（文本框、滚动条、对话框）也跟着变。
与阅读器内的「纸张主题」相互独立，但选深色时若纸张还是日间会自动切到夜间。

## 6. 已修复的坑（都值得记住）

| 现象 | 根因 | 处理 |
| --- | --- | --- |
| 点搜索就闪退 | XAML 里 `ModeAll.IsChecked="True"` 在 `InitializeComponent` 期间就触发 `Checked`，此时 `ModeOne` 字段仍是 null | 加 `_uiReady` 标志，初始化完成前忽略事件 |
| 正文显示一堆 `<p>` 标签 | 书源用 `@html`/`@all` 时规则返回 HTML 字符串，没转纯文本 | `LegadoEngine.SanitizeContent` 在**引擎输出、缓存写入、缓存读出、导出**四处都做清理 |
| 书名/分类后粘着一整页文本 | 空规则被当成“返回整个文档”，而 Legado 是返回空串 | 空规则直接返回空，详情页未配置的字段保留搜索页的值 |
| 简介显示 `{{@@div.description@html}}R15` | 含 JS 模板的规则无法求值，取到的是规则原文 | `LooksLikeRawRule` + `CleanField` 把这类值当空处理并记警告 |
| 偶发崩溃，日志有 `Only a single ContentDialog can be open at any time` | 导出完成提示与其它对话框叠加 | 所有对话框走 `Dialogs.ShowAsync` 串行化 |
| UI 自动化读到陈旧界面 | `ApplicationFrameHost` 在应用退出后仍保留窗口，UIA 返回上一次的内容 | `common.ps1` 里校验窗口的 `CoreWindow` 属于活着的 NovReader 进程 |
| 搜索按钮点不动 | 顶栏图标按钮的 `AutomationProperties.Name` 也叫「搜索」，和页面搜索按钮重名 | 顶栏改名「快速搜索」「我的书架」 |
| 正文里出现 HTML 标签 | 早期缓存文件是在清理逻辑之前写入的 | 缓存读出时也清理，兼容旧数据 |
| 搜索结果点进去目录为空 | 详情页在“搜索结果已有简介”时跳过详情请求，`tocUrl` 一直为空，拿详情页当目录页解析 | `GetChaptersAsync` 在 `tocUrl` 为空时先补请求详情；详情页只按 `tocUrl` 是否为空决定 |
| 目录全是“第 N 章”、正文为空 | 约七成书源用 Legado 默认（JSoup）语法 `tag.a@text` `class.x` `id.x` `text.下一页` `.0` `!0`，被当成 CSS | `JsoupRule.cs` 移植 `getElementsSingle` + `findIndexSet`，不带 `@css:` 的规则走它 |
| 正文为空或只剩广告词 | `##正则` 在 Legado 里是“删除匹配”，旧实现当成“提取”；且用了 Singleline 让 `.` 跨行 | 按 Legado 语义：删除 / 全部替换 / `###` 取第一个；去掉 Singleline |
| GBK 站点乱码、搜不到书 | UWP 没注册代码页；关键字按 UTF-8 编码 | 首次解码时注册 `CodePagesEncodingProvider`；`{{key}}` 按选项里的 `charset` 编码 |
| 正文缓存里出现“（抓取失败…）” | 引擎失败时返回提示文字，被当成正文写入缓存 | 失败返回空串（原因在 `LastError`），缓存读到旧失败文本时视为未命中 |
| 打开设置页字号/行距/页边距被改成最小值 | Slider 的 Minimum &gt; 0，`InitializeComponent` 时 ValueChanged 先于抑制标志触发 | 抑制标志初始为 true，加载完控件才放开（设置页与阅读器设置面板） |
| 从详情页返回就回到书架、搜索结果丢失 | MainPage 未缓存，返回时重建外壳 | MainPage / SearchPage 设 `NavigationCacheMode=Required`，返回时通过 `IShellPage` 刷新 |

## 7. 尚未完成
- 真机（Lumia 950 / 10586）部署验证。
- 书源编辑（新增/修改单条规则）UI；目前只有导入/导出/删除/启用停用。
- 已导入的书源是旧版保存的，不含 `nextTocUrl`；需要目录分页的书源要从书源广场重新导入一次。
- `@put` / `@get`、`<js>`、`@js:` 等脚本能力仍不支持。
- 书架分组/分类、阅读页的字体加粗/字重选项。
- `Package.appxmanifest` 只有 `internetClient`，真机访问局域网需再加 `privateNetworkClientServer`。

## 8. 真实在线书源现状
用 `--scan` 扫真实合集：341 个书源中 118 个为纯规则可测，**完整跑通 5 个**，另有 75 个能拿到 HTTP 200。
失败原因**不在引擎**，已逐个抓页面确认：域名停放/变成影视站、大量 502/503/超时、
**JS 反爬挑战页**（如 `router.parklogic.com`，需浏览器执行 JS 才跳转到真实内容）、
以及站点正常返回但该关键词确实没有结果。
纯规则引擎实现是正确的；要覆盖 JS 类书源需要额外的 JS 引擎或内嵌 WebView，属于后续大工程。

## 9. 调试手段
- 应用内「调试」页：最近请求详情、规则警告、操作日志、运行环境。
- **`app.log`**：`%LOCALAPPDATA%\Packages\NovReader_7xxdx2kd68cm6\LocalState\app.log`，
  记录搜索/导入/打开书籍/缓存命中/异常，应用崩溃后也能回看。
- 测试台：`Harness.exe --network --live --scan <合集文件>`。

## 10. 接手检查清单
- [x] 检查所有已存在文件的实际内容
- [x] 修正工程为可编译（VS2017 MSBuild 15.9 + 15063 SDK）
- [x] 实现 Legado 兼容规则引擎 + 离线测试台（89 项）
- [x] 拆分 Models / Services / ViewModels / Views
- [x] 实现书架、搜索、详情、阅读器、书源、设置、调试七个页面
- [x] 实现书源广场与一键全部导入
- [x] 实现深色模式、多线程下载、多格式多编码导出
- [x] 实现三级本地缓存与相邻章节预取
- [ ] 补齐图标等 Assets 资源（当前为最小 PNG）
- [ ] Lumia 950 真机部署测试
- [ ] 视需要引入 JS 执行能力以覆盖 JS 类书源

## 12. 2026-10-11 本轮改动摘要

**界面（Windows 10 Mobile 设计规范）**
- 外壳改为汉堡菜单（SplitView：手机 Overlay，≥720px CompactOverlay），设置/调试放菜单底部；去掉底部 5 标签。
- 各页操作改为底部 `CommandBar`（AppBarButton，次要操作进「…」），不再横排一排按钮（手机宽度会溢出）。
- 直角、扁平、无卡片；浅色纯白 / 深色纯黑；强调色读系统 `SystemAccentColor`；正文 15px、说明 12px；边距 12px；可点区域 ≥ 48px。
- 搜索页：`AutoSuggestBox`（历史记录做建议）、顶部细进度条、结果带封面 / 来源数；无结果时显示最近搜索。
- 详情页：Pivot「详情 / 目录」，目录是整页虚拟化列表（旧版 ListView 套在 ScrollViewer 里，长目录卡顿），支持倒序；「换源」「下载全本」进应用栏。
- 书架：整行列表 + 进度条，点按继续阅读，长按 / 「…」打开菜单。
- 桌面模式标题栏显示返回按钮；手机返回键：先收菜单 → 回书架 → 退出。

**搜索逻辑**
- 同一本书（书名+作者归一化：去空格/符号、去“作者：”前缀）在多个书源的命中合并为一条，记住全部来源，详情页可直接换源。
- 默认按相关度（完全匹配 > 前缀 > 包含 > 作者匹配），同分按收录书源数；可切换到达顺序 / 书名 / 书源数；「精准」只留书名或作者含关键字的结果。
- 每次搜索有代号，旧搜索的迟到回调直接丢弃；停止后立即返回，不等在途请求超时。
- 搜索直接跳到详情页（唯一结果时站点 302）的情况按详情页解析。
- 书架里的书「换源」：在全部书源里按书名查找，最多 25 秒或找到 8 个就停。

**引擎**
- `nextTocUrl` 目录分页（单个“下一页”逐页翻，或一次给出全部页码并发抓取），`-` 倒序在合并后整体反转。
- JSON 响应自动识别，`data.list` 这类不带 `$` 的规则按 JSONPath 求值；`ruleBookInfo.init` 支持 JSON。
- 书籍 / 目录 / 章节地址里的 `,{"method":...}` 选项保留并生效。
- 正文翻页最多 12 页，遇到下一章地址即停（调用方传入下一章 URL）。
- XPath 支持 `position()` / `last()` 表达式与 `text()='…'`、`contains(text(),…)`。
- 目录缓存目录改为 `toc-cache-v2`，旧的（可能是空章节名）目录缓存自动丢弃。

**验证**
- 离线测试台 164 项全过（新增约 75 项）。
- 真实书源 `ryw笔趣阁78`：修复前目录 1932 章全是“第 N 章”、正文为空；修复后章节名、正文都正确。
- `--scan .\sources-backup.json`（同一批 341 个书源、同一关键字）：完整跑通 **5 → 20** 个（118 个纯规则可测）。
  剩下的多为站点改版/下线、VIP 章节、JS 渲染页面。
- x64 Debug、ARM Release（.NET Native）均编译通过。UI 冒烟脚本：`_devtest\drive15.ps1`、`drive16.ps1`（手机尺寸窗口）。

## 13. 2026-10-12 第二轮（真机反馈后）

**真机部署（已验证，当前手机上是 1.0.2.0）**
```powershell
# 打包（Release|ARM，.NET Native）
MSBuild NovReader.csproj /p:Configuration=Release /p:Platform=ARM /p:GenerateAppxPackageOnBuild=true `
  /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false `
  /p:AppxGeneratePrisForPortableLibrariesEnabled=false /t:Build
# 签名：pfx 有密码，用证书存储里的同一张证书（CN=NovReader）按指纹签
signtool sign /fd SHA256 /sha1 183E74E21F0C7977BC9ACE4E8AFA2503D2637515 /s My NovReader_x.y.z.0_ARM.appx
```
- 每次部署前把 `Package.appxmanifest` 的版本号加一，才能覆盖更新并保留手机上的数据。
- 本机 `HTTP_PROXY=127.0.0.1:7897`，curl 访问手机必须加 `--noproxy '*'`，否则代理返回 502。
- WDP 的 POST 接口没有请求体时要带 `-H "Content-Length: 0"`（否则 411）。
- 手机熄屏会断 Wi-Fi，请求前先轮询 `/api/os/info` 直到 200。
- 手机侧载名额上限 20 个，曾报 0x80073D11；更新失败时先用 `/api/filesystem/apps/file` 备份
  `LocalState` 再卸载重装。备份在 `_devtest\phone-backup\`。

**这一轮修掉的问题**
- 书源 `header` 是 JSON，旧代码按行拆导致 323 个书源的 UA / Referer / Cookie 从未发送。
- 模板规则 `{{$.id}}` / `{$..id}` / `{{source.bookSourceUrl}}` / `{{baseUrl}}`；只有 `##正则` 的规则作用于元素 HTML；
  取不到地址的搜索结果直接丢弃（以前回落成搜索页地址，点进去目录永远为空）。
- 换源“先试后换”：新书源拿到目录才提交，失败保持原书源；书架条目合并去重；书架写临时文件后改名、串行保存。
- 详情页 / 阅读器的请求放进导航参数，从返回栈回来能恢复（以前详情页回来是空白）；800ms 内重复打开同一页面忽略。
- 阅读器：章节加载代号（连点不串章）、离开后不再写进度和申请常亮、按真实布局恢复滚动位置、重试会接着打开章节、
  滑块松手才跳章、上一章从末页进入、错误页加「换源 / 详情」。
- 字体：XAML 只认英文字体族名，改为 `FontCatalog`（显示中文名、保存族名、只列本机存在的字体）。
- 打开设置页会把字号 / 行距 / 页边距写成最小值；设置加载时漏拷搜索 / 下载线程数。
- 搜索：默认 16 路并发（上限 32）、搜索请求 10 秒超时、按书源健康度排序、跳过连续 3 次连不上的书源 2 天
  （`source-health.json`，设置页可关闭或清空）。
- 漫画：正文 `<img>` 保留为图片行，阅读器图片模式带书源请求头和 Referer 下载；有声书源用 MediaElement 播放。
  注意：61 个漫画书源里只有 3 个是纯规则，其余靠 JS 解出图片列表，仍不可用。
- W10M 15254 的 Segoe MDL2 Assets 缺少 E736 等后加字形（显示成方框），图标改用 `Symbol` 枚举。

测试台 190 项全过。

## 14. 2026-10-12 第三轮：JS 引擎接入（漫画/复杂书源）

手机日志实锤：59/61 个漫画书源靠 JS 解出图片列表（学外漫画 = Dean Edwards packer；G社漫画全程 JS + AES）。
方案：**后端可插拔的 JS 运行环境**（`Services/Legado/Js/`）：

- `LegadoJs`（共享）：求值编排 + `java.put/get` 变量 + 当前页面上下文（src/baseUrl/book/source/key/page）。
  `AsyncLocal` 保证并行搜索各流独立。规则侧接点：`<js>`/`@js:` 规则、`{{js}}` 模板、`@js:` 分段、
  URL 整条 JS、header 里的 JS（G社漫画的 Referer）、`@put:{}`/`@get:{}`。
- `JintBackend`（**只在测试台**）：Jint 3.1.6（ES6+ 全支持，含模板字符串/箭头函数/eval）。
  UWP 工程不引用 Jint——netstandard2.0 与 MinVersion 10240 冲突（NU1202），且强引 netstandard
  facade 会把 VS2017 的 XAML 编译器搞崩（WMC9999/WMC1006 级联）。
- `JscBackend`（**只在 UWP**）：P/Invoke `JsBridge.dll`——`JsBridge/JsBridge.cpp` 用 C ABI 包 JavaScriptCore，
  静态链接 Apotheosis（`build-clang-gpu` 的 JavaScriptCore.lib/WTF.lib/bmalloc.lib + C:\icu-arm-uwp），
  产物 11.4MB，作为 Content 打包。构建：`JsBridge\build-jsbridge.ps1`（clang-cl + arm shim + /MD + c++23 透传，
  stubs 补 WTFCrashWithInfoImpl / Gigacage）。DLL 加载失败自动回退为“JS 不可用”，应用照常跑。
- 桥的宿主函数（java.ajax/post/get/getString/getCookie/base64/md5/sha/aes）经函数指针回调 C#，
  AES/CBC/PKCS5 在 C# 本地实现（DES 不支持）；Cookie 用共享 CookieContainer；prelude 内置 packer 解包 `unpack` 和 `bhost`。

**实测（Jint 后端）**：学外漫画全流程通（搜索 → 11 章 → 正文 = 图片列表带绝对地址）；
G社漫画 JS 已开始执行但其 API 站点失效，目录仍空（站点问题）。

**待真机验证（2026-10-12）**：1.0.3.0 包（含 JsBridge.dll）已构建签名，用户自装
（`AppPackages\NovReader_1.0.3.0_ARM_Test\`，覆盖 1.0.2.0 保留数据）。装好后：
- 用「学外漫画」搜《撒野》验证漫画图片模式；JSC 桥若在 .NET Native 上初始化失败，
  日志会记「JS 引擎不可用」，按需再排查（可能要补 rd.xml 或换桥实现）。
- 部署脚本 `JsBridge\deploy-watch.ps1`（等设备可达→备份→安装→启动→拉日志）。

## 11. 真机部署提醒
1. 需要在 `Package.appxmanifest` 增加设备所需能力（至少 `internetClient`，局域网再补 `privateNetworkClientServer`）。
2. 真机若要装测试包，必须先把 `NovReader.cer` 装到设备受信任根
   （`CurrentUser\TrustedPeople` 在本机因需要管理员权限而失败，真机通常用设备门户或侧载工具处理）。
3. 真机装完先用「书源广场」导入合集，再用内置示例书源做一次离线自检，确认 UI 与解析链路正常，
   最后再测外网站点。
4. 真机性能有限：建议把搜索并发与下载线程数调到 4 左右。

## 15. 2026-10-12：1.0.3.0 启动失败修复（1.0.4.0）

用户反馈 1.0.2.0 正常，1.0.3.0 打开立即退出。手机当时不在可访问的局域网，未读取真机 dump。
通过直接检查两个 AppX 的 PE 导入表确认了足以导致激活失败的打包缺陷：
1.0.3.0 的 `NovReader.dll` 普通启动导入表直接引用 `JsBridge.dll`；后者引用 `icuuc78.dll` /
`icuin78.dll`，而 ICU 又引用 `icudt78.dll`。三者全部漏打包。ICU 的 `.lib` 实际是导入库，
此前文档里“ICU 静态链接”的描述不正确。缺库时失败发生于托管代码运行之前，`IsAvailable` 的 catch 无效。

**1.0.4.0 改动**
- `JscBackend` 使用 `LoadPackagedLibrary` + `GetProcAddress` + 明确 Cdecl 委托首次加载桥，
  移除对桥的直接 DllImport。检查已构建的 .NET Native 导入表，确认不再强制加载 JsBridge；
  MCG 已生成 InitFunction / EvaluateFunction / NativeCallback 的正向和反向互操作代码。
- ARM 包包含 ICU 三个 DLL；x64 Debug 不再携带 ARM 原生 DLL。
- VCLibs 最低版本升为 14.0.26706.0，并随更新包携带微软签名的 ARM 框架包；
  已逐一核对桥和 ICU 导入符号，包括 `MSVCP140_2_APP.dll!__std_smf_hypot3`。
- 桥的 `MakeHostFn` 不再释放后使用 nameRef；UTF-8 长度查询不再恒返回 -1；
  C# 使用全局锁保护宿主、求值与返回缓冲区读取，避免并行书源串用数据；修正 java.get 请求头参数。
- 仍需要真机验证 Windows 10 Mobile 的实际 JS 引擎初始化及漫画流程。显式加载能回退的是加载失败，
  不保证能捕获引擎内部 fastfail / 访问违规等原生崩溃。

**验证与构建**
- 原测试台 213 项全部通过（Jint 后端；不等同于 ARM JSC 真机测试）。
- `_devtest/JscBackendSmoke.cs` 在未打包控制台执行原生产 JscBackend：
  捕获 LoadPackagedLibrary 的 Win32=15700（APPMODEL_ERROR_NO_PACKAGE），失败后重复/并发访问均安全回退。
  编译命令：VS2017 Roslyn csc，引用 `_devtest/bin/Harness.exe`，同时编译测试文件和生产 JscBackend.cs。
- `_devtest/verify_arm_package.py <ARM.appx>` 检查文件 block map、ARM 架构、启动导入表、
  框架依赖版本和递归导入符号；已证实拒绝旧 1.0.3.0、接受新 1.0.4.0。
- `JsBridge/build-arm-package.ps1` 已实际执行通过：构建原生桥和托管 ARM 包、携带 ARM VCLibs、校验、签名。
  该脚本使用 `JsBridge/Dependencies/arm/Microsoft.VCLibs.ARM.14.00.appx`，不要再省略此依赖。
- 主包仍用原证书指纹 `183E74E21F0C7977BC9ACE4E8AFA2503D2637515`；通过 CMS 密码学签名验证。
  本机 signtool /pa 的证书信任检查会因自签名根未信任报错，不要为此改系统证书存储。
- 主包：`NovReader/AppPackages/NovReader_1.0.4.0_ARM_Test/NovReader_1.0.4.0_ARM.appx`。
  SHA-256：`39A4DCD0F62F2C3432FE2DF935780F8FBC5E623D87209955D2A32815D2890BF2`。
- 便于远程安装的 ZIP：`NovReader/AppPackages/NovReader_1.0.4.0_ARM_Install.zip`，仅包含主包、
  三个 ARM 框架依赖、原公钥证书和中文安装说明。用户自行覆盖安装，无需卸载，不做自动部署。

## 16. 2026-10-12：漫画章节闪退进一步修正（1.0.5.0）

用户真机反馈：1.0.4.0 可以启动、小说正常，进入漫画章节后立即退出。用户指出
E:/Apotheosis 的 JSC/JIT 可以正常使用，要求参考该项目；此轮未获取真机 dump。

对照当前实际源码和构建配置发现：Apotheosis 的清单包含 `codeGeneration`，而 NovReader 缺少。
微软 VirtualProtectFromApp 文档明确要求该 capability 才可设置可执行页面。
该 JIT 构建的 VM 即使执行 LLInt，也会通过 jitStubs 调用原生宿主；可执行池失败不是安全回退，
VM.cpp 的 port 注释记录过 jitStubs 空指针导致的真机崩溃。此为明确配置缺陷，尚未用真机日志确认
是否为本次漫画闪退的全部原因，不要把本地 Jint 测试当成 ARM JSC 验证。

**实现**
- 保留 build-clang-gpu 的 JSC，增加 codeGeneration；不切换到无 JIT 的旧库。
- 桥与库同步 `WK_WINUWP_JIT=1`、生成 cmakeconfig.h、PrivateHeaders、NDEBUG、
  STATICALLY_LINKED_WITH_bmalloc，并对编译后的 ENABLE(JIT)/ENABLE(C_LOOP) 做预处理检查。
- 链接改为 Apotheosis 使用的 `/OPT:NOICF`，同时生成并保留 JsBridge/JsBridge.pdb。
- 初始化前检查 RW→RX 权限，拒绝没有可执行池的 VM；使用 JSC::initialize 的 options callback，
  在首个 VM 创建前固定为 Apotheosis 当前默认 mode 1（useJIT=true、useBaselineJIT=false），
  forceRAMSize=512 MB。不能宣称已经验证 baseline JIT 在此桥中工作。
- JsBridgeInit ABI 增加诊断路径参数，C# 签名同步；.NET Native MCG 已生成对应的委托互操作代码。
- 原生 WTF crash hook + WTFCrashWithInfoImpl 保存位置和载荷，托管与原生阶段同步落盘，
  包括漫画抓取返回与图片解码。DebugPage 可直接读取上次退出前的 js-native.log。
- 未改图片渲染策略；若继续闪退，可通过日志区分 JSC 初始化、脚本求值和图片解码。

**验证与产物**
- JsBridge、ARM Release/.NET Native、完整构建签名脚本通过。
- 213 项离线规则测试与原生加载失败回退测试通过。
- 包检查器增加 codeGeneration 检查，拒绝旧 1.0.4.0、接受新 1.0.5.0；
  原生依赖、导出符号、ARM 架构和 block map 全部通过。
- 主包：`NovReader/AppPackages/NovReader_1.0.5.0_ARM_Test/NovReader_1.0.5.0_ARM.appx`。
  SHA-256：`CB51B5669D010A960E0C48B169DB685FD98C8C32DE4DB99C33268197C7F246E0`。
- 安装 ZIP：`NovReader/AppPackages/NovReader_1.0.5.0_ARM_Install.zip`。
  依赖与 1.0.4.0 相同，用户远程自行覆盖安装；漫画的真机结果待反馈。

## 17. 2026-10-12：日志与 USB 真机排查（1.0.6.0–1.0.8.0）

用户提供根目录 js-native.log，并连接 Lumia 950。USB Device Portal 可用：
`http://127.0.0.1:10080`（绕过本机代理），Wi-Fi 地址仍是 192.168.3.159。
已备份手机 1.0.5.0 的 sources/shelf/settings/app.log/js-native.log 到
`_devtest/phone-backup/v105-usb/`，验证书源 1127 个、书架 8 本。更新均为覆盖，没有卸载。

日志证明 1.0.5.0 的 JSC 初始化成功，并有正常脚本执行；当时的主要问题已经不是启动失败。
文件中 442 字符脚本有 20 次正常返回错误（ok=1），74 次其他调用成功，图片解码开始次数为 0。
手机 app.log 明确报告了 14P/11P 章节的全部图片下载失败；没有新的 NovReader crash dump。

**脚本错误已精确重现并修复（1.0.6.0）**
- 学外漫画 / 撒野 / read-2.html 的“序话（50P）”使用 Dean Edwards packer 的 radix=62。
  旧 unpack 调用 Number.toString(62)，实际异常 `radix must be between 2 and 36`。
  原生/Jint prelude 同步改为支持 0-9a-zA-Z 的递归编码，包括多位 base62。
- 正文规则以前先 SelectNodes、再 SelectStrings，导致 JS 重复执行。现在 Eval 一次再按类型处理。
- 空正文保留 JS 异常到 Engine.LastError，原生/托管日志保存异常文本。
- 实际网站抓取测试：14P/11P/50P 分别得到 14/11/50 个图片地址；PC 首张图片均可下载。
- 用户在已覆盖安装的 1.0.6.0 上确认章节打开、不退出但图片不显示；手机日志确认 50 张图片解析成功。

**图片传输与 DNS（1.0.7.0–1.0.8.0）**
- 手机 System.Net.Http 对 res.taozi.icu 报无法连接，图片尚未进入解码。
  为图片增加 NetBridge.dll 的 curl/OpenSSL 备用通道；独立 LoadPackagedLibrary，不形成启动强依赖。
  CA bundle 来自 Apotheosis，peer/hostname 校验保持开启，自动跟随 HTTP/HTTPS 重定向，32 MB 单响应上限。
- libcurl.dll/libssl-3-arm.dll/libcrypto-3-arm.dll/z.dll 与 Apotheosis 0.2.5.24 AppX 中的文件 SHA-256 完全相同。
  1.0.7.0 真机加载了该通道，但连接仍超时（curl=28，HTTP=0），尚未建立连接。
- 手机 WLAN DNS 为 192.168.3.1；直接向此路由器解析 res.taozi.icu 得到 211.137.54.227，
  阿里/Google 的 HTTPS DNS 均得到 61.111.250.215。PC 经自身网络通道请求该域名返回 302，
  最终图片地址在 img.shadouyou369.com，返回 200 JPEG。当前推断手机受本地 DNS 错误结果影响。
- 1.0.8.0 的图片备用通道启用 `https://dns.alidns.com/dns-query`，缓存 DNS，
  仍校验证书，不修改手机全局 DNS/代理。已构建签名并发送 USB 覆盖更新，真机图片结果待验证。
- 222 项离线测试全过（含 base62、正文一次求值、图片 fallback、请求头保持、回退域名记忆及异常吞回）。
  `_devtest/XuewaiProbe.cs` 可针对准确的漫画 URL 测试，避免原 --probe 搜到另一部同名作品。
- 最新主包：`NovReader/AppPackages/NovReader_1.0.8.0_ARM_Test/NovReader_1.0.8.0_ARM.appx`。
  SHA-256：`F3A31116C0362230C81CDF570CE081B2008F4FBEE7AE2492C79811CE31F7A6D7`。
- 不要把 PC Jint 测试成功等同于手机图片显示成功。后续先拉 1.0.8.0 的 js-native.log，
  确认 curl 返回 HTTP=200 和 image decode complete，再确认用户看到图片。

**1.0.8.0 反馈及 PC 图片格式实测**
- 用户确认仍无图片，要求先在电脑请求检查是否 WebP。USB 新日志 23:36:58–23:39:40
  为 `curl=6 HTTP=0 ... Could not resolve host: res.taozi.icu`，未进入图片解码。
  1.0.8.0 的 DoH 并未在手机上成功，不能宣称 DNS 已修复。
- PC 请求 `https://res.taozi.icu/19bce751159b50b009bde7efc3f02b88bd83e223`：
  默认 Accept 返回 302→200、image/jpeg、52871 字节、JPEG 魔数 FF D8 FF、750×586，Pillow 完整解码成功。
  Accept 包含 image/webp 时同一个最终 .jpg URL 返回 image/webp、44870 字节、RIFF/WEBP 魔数、750×586，完整解码成功。
  该站确实按 Accept 协商格式，不能凭 URL 后缀判断实际编码。
- 按生产 HttpFetcher 的 Accept 请求手机失败日志中的 `.../1cfe6d0b72677fc09c49aaa8ddd15bcfb27086d4`，
  PC 返回 200 JPEG、77131 字节、750×586，完整解码成功。学外漫画 header 为空，生产默认头没有宣告 WebP 支持。
- 请求响应头/原文件留在 `_devtest/pc-image-default.*`、`pc-image-webp.*`、`pc-phone-image.*`。
  绕过显式代理的请求仍出现 198.18.0.24 fake-IP，说明 PC 仍有透明网络代理；PC 下载成功不能证明手机直连可达。
- 当前手机图片不显示的直接证据为下载前 DNS 失败，尚无手机 WebP 解码失败证据。下一步排查 DoH 自身连接/证书/解析。

## 18. 2026-10-13：1.0.9.0 多 DNS 与 W10M 图片格式处理

用户要求多个备用 DNS，并指出 W10M 无原生 WebP；拔掉 USB 后要求 Wi-Fi 部署。
Wi-Fi 设备门户仍为 http://192.168.3.159（请求绕过电脑代理）。

对照 Apotheosis/port/WebCoreDriver.cpp:4624，确认该项目实际工作路径为
WebCoreSetCACertBlob：原生 OpenSSL 在手机 AppContainer 内通过文件载入 CA 会失败，
不能仅照搬 DLL 和 CA 文件路径。1.0.8.0 的 curl 内置 DoH 错误隐藏在 curl=6 中；
此差异是可疑原因，尚不能用 PC 测试证明手机根因已消除。

实现：
- NativeImageFetcher 用托管 File.ReadAllText 读取随包 PEM、缓存内容，并传入原生桥；
  NetBridge 对图片与 DNS HTTPS 请求全部设置 CURLOPT_CAINFO_BLOB，校验 peer/hostname。
  原 C ABI 参数布局没变，但第三参数语义从文件路径变为 PEM 内容；托管/native 必须同步更新。
- 改为自己请求/解析标准 DNS 二进制 HTTPS 响应，依次备用 Ali / DNSPod / Cloudflare。
  引导域名通过 CURLOPT_RESOLVE 指定各服务两个 IP，仍以服务域名进行 TLS 校验。
  不依赖路由器先解析 DoH 主机，不使用 curl 内置 DoH 的隐藏子请求。
- DnsWire.h 验证 DNS 响应/问题/rcode、边界、压缩指针和 CNAME 链；取 A 记录和最小 TTL，
  TTL 上限 300 秒；缓存 32 个域名，命中不会延长原 TTL，失败不负缓存。
- 图片跳转最多 8 次，每跳单独解析；同域可用多个地址，连接失败清缓存/换提供商。
  总超时最多 60 秒（正常调用 30 秒），单 DNS 5 秒，单连接 6 秒；成功提供商下次优先。
- 系统 HttpClient/native 图片请求均覆盖 Accept 为 JPEG/PNG/GIF；不宣告 WebP，
  ReaderPage 识别 RIFF/WEBP 并明确记录不支持；此版没有新增 WebP 解码器。
  解码异常不再只静默计数，保留错误详情；native 日志扩至 4096 字节。

验证：
- 222 项 Harness 测试全部通过。
- _devtest/NetBridgeProbe.cpp 直接包含生产 NetBridge.cpp，在 PC 上链接 Git OpenSSL curl，
  检查压缩 CNAME、多 A/TTL、所有截断长度、错问题/NXDOMAIN/TC/指针环，以及实际三个 DNS。
  主提供商改到不可连接的本地 HTTPS 地址，实际 curl=7 后切到 DNSPod，重定向下载 JPEG；
  source Accept:image/webp 也返回 JPEG。再请求命中缓存，过期缓存不能使用。
  构建运行：_devtest/build-netbridge-probe.ps1 -Network -FailPrimary。
  日志 _devtest/netbridge-probe-v109.log；PC 仍有网络代理，不替代 ARM 真机验证。
- ARM 原生桥与 .NET Native 主包编译、block map/递归原生依赖闭包通过，原证书签名通过 CMS 验证。
- 主包 SHA256：0CCA31F2BE7D21CD6611BBB808084527F55A2ED2F2EDA1082AD6BA531ECE7AF5。
- 主包 NovReader/AppPackages/NovReader_1.0.9.0_ARM_Test/NovReader_1.0.9.0_ARM.appx。
- 已通过 Wi-Fi 覆盖安装：WDP install state Success=true，启动 HTTP200，
  进程 NovReader_1.0.9.0_arm__7xxdx2kd68cm6，首次 PID 6652；后观察 PID 9496。
  app.log 00:03:18 与 00:05:41 均载入书源 1127 个、书架 8 本。
- **手机网络已有成功证据**：00:03:36 和 00:03:56 的 native transport 日志显示
  Ali HTTPS DNS curl=0 HTTP200 得到 res.taozi.icu=61.111.250.215；手机实际连接此 IP 返回302。
  最终域 img.shadouyou369.com 解析出 Cloudflare 三个 IPv4，实际连接 104.26.4.66 /172.67.73.23
  返回 curl=0 HTTP200 image/jpeg，下载80196字节（2d90d895...jpg）。这次不是仅 PC 推断。
- 用户在此间切页/搜索，以上两次下载后还没有观察到 image decode complete；
  不要把传输成功写成 UI 显示已修复。异步提问已发送，等待用户留在章节测试的显示反馈。
- 大 js-native.log 在 Wi-Fi WDP GET 偶尔200/204/404，列表仍能列出文件；不要用空失败响应覆盖已有完整快照。

## 19. 2026-10-13：漫画并发设置与图片持久缓存（1.0.10.0–1.0.11.0）

用户最终确认 1.0.9.0 的图片不显示是等待下载的问题，要求漫画独立并发设置、
核对 JS 引擎和原版功能差距，随后指出漫画章节未缓存图片。
目标保持：网络部署（手机已拔 USB）、修复漫画体验，并给出 JS/功能核对结果。

1.0.10.0 已通过 Wi-Fi 部署，用户明确反馈“图片加载明显变快”。
- Settings → 漫画：ComicParallelDownload（默认 true）和 ComicImageThreads（默认4，1–8）。
  SettingsStore 保存/读取，ReadingSettings.Clone 同步字段。
- ComicImageDownloads：有限预取窗口，图片网络请求并行、UI 解码串行按原顺序；
  离页/切章后停止显示旧结果和派发新任务，已发出的请求仍等待当前请求结束。
  不触碰 JSC 的全局互操作执行锁。
- ReaderPage OnNavigatedTo 重置 _leaving，设置页关于说明更新 JS 能力。
- 231 项测试全过，ARM 构建签名/依赖检查通过，主包 SHA256
  1858C5ED36B219E7741845CB482A58D2C6765213AF3CAC39F6EB79733306ED82。
- 手机日志 _devtest/phone-backup/v110-wifi/js-native.log 确认 concurrency=4 和多图片同秒完成。
  1.0.9.0 的 50P 整章 0–49 均 decode complete，用户已经看到图片。

缓存旧逻辑的结论：ChapterCache 只保存含 img URL 的正文文本；ReaderPage 每次 GetBytesAsync，
图片未落盘；DownloadService 只缓存文字、存在文字就 skipCached，因此之前“下载全本”不保证漫画离线可读。

1.0.11.0：
- ComicImageCache 为纯托管文件服务，AppState 注入 LocalState/comic-image-cache 路径。
  文件名是 sourceURL+imageURL 的 SHA256，原始图片压缩字节存 .img，临时 .part 写完后移动；
  保留原图片 URL，不把正文改成设备路径；不持有常驻图片 byte[] 缓存。
  同一图片请求合并，下载失败/HTML/WebP 等不作为有效文件缓存，32MB 单图上限，
  清空缓存递增代数，旧下载不可重新写回；损坏文件可由解码失败路径删除后重试。
- 阅读优先文件缓存；DownloadBookAsync 对文字已缓存但缺图片的章节只补图片，
  图片全部存在才 skip；补图失败会计为章失败，重试只下载缺失图片。
  下载全本的漫画章节 worker 并发受漫画偏好限制，单章内部图片顺序下载，避免 N×N 请求。
- Settings 数据栏显示真实图片文件数/容量；清理覆盖图片；关于版本号读取包实际版本。
- 发现并修正 1.0.10.0 漫画偏好在 AppState.CopySettings 启动时漏拷的问题：
  此前自定义设置虽落盘，重启会恢复默认；1.0.11.0 同步复制开关和并发数。
- 244 项测试全过，包括新实例断网读文件、并发去重、清空与旧下载竞态、
  实际生产 DownloadService 的文字已存在仍补图/完整章跳过/失败再补。
  Harness 只替换 UWP storage/UI 边界；真实 ComicImageCache/ComicImageDownloads/DownloadService 全部编译执行。
- ARM 构建/依赖闭包/签名通过，CMS 签名指纹仍183E74E21F0C7977BC9ACE4E8AFA2503D2637515。
- 主包 SHA256 B0937F2D07B3CFF073888CF0439398F6D53BA029FA595FCC29E086BD206B8F01。
- WDP 覆盖安装 Success=true；NovReader_1.0.11.0_arm__7xxdx2kd68cm6 已启动，PID5988。
  异步请求用户“加载后离开章节再打开”，缓存文件/命中真机验证仍待结果。

真机缓存已取得落盘证据：LocalState/comic-image-cache 在 23:13 后列出 42 个 .img，
共 2654861 字节。WDP 下载其中一份 70197 字节文件，PC Pillow 验证 JPEG 750×586 完整解码成功。
js-native.log 有 cache saved 与 decode complete；还有一次实际连接超时后改用 DNSPod 并成功取图的记录。
尚未观察到用户重开后的 cache hit，异步显示反馈仍待用户回答。
安装 ZIP NovReader/AppPackages/NovReader_1.0.11.0_ARM_Install.zip 已验证，共6个文件、27686943字节。

JS/原版核对已写入 LEGADO_GAP_AUDIT.md（21 项对照与后续顺序）。
原版主仓库当前删除了源码，因此采用官网与公开 fork 的原版历史提交
b30ae2ef9e858f24d459e2e22cc5e86bbec625ae（2021-10-12），不冒充最后发行版本完整审计。
当前 JS 引擎实机可用，但宿主/公共脚本和全局作用域隔离不完整；不能宣称兼容所有书源。

## 20. 2026-10-13：阅读器工具补全（1.0.12.0）

用户授权自行选取阅读体验功能；本轮选了书签/备注、本章查找、漫画单图放大和失败重试。
- 新模型 ReadingBookmark 和纯托管 ReadingBookmarkStore，AppState 注入 LocalState/reading-bookmarks.json。
  保存章节 URL/名称/序号、滚动比例和备注，按书籍 URL 隔离；串行写入，.tmp/.bak 恢复；暂不跨换源迁移。
- ReaderPage.Tools.cs 作为同页 partial，更多菜单提供保存/列表/备注编辑/删除/跳转。
- ChapterTextSearch 按原文查找，中英/标点、大小写无关、UTF16 偏移，最多500匹配。
  XAML 文本 Run.ContentStart + GetCharacterRect 定位，查找栏独立占一行，不遮挡匹配；不做全文高亮/全书/OCR。
- 漫画图片有加载提示和失败重试按钮；可单张或当前章批量重试。
  重试与初始加载共用按当前偏好建立的 SemaphoreSlim，避免并发翻倍，切章不写回旧结果。
- 长按已加载图片/更多菜单打开单图查看，ScrollViewer 1–5倍缩放，双击复位，复用原 bitmap。
  返回/新章节/离页清除放大引用；普通文字和音频模式也清理漫画 slot 引用。
- App 的 BackRequested 先交 Dialogs.TryClose 和 ReaderPage.HandleBack 处理面板，再导航返回。
  输入框键盘事件不触发正文翻页，漫画书签等图片布局完成后再按比例恢复（用户触摸则取消等待恢复）。
- 新增 Properties/Default.rd.xml，定向保留 ReadingBookmark 公共属性给 XAML Binding，
  不对整个应用扩大动态元数据；Native 的 ILT0027 缺 rd.xml 警告已消失。
- 260项 Harness 全过；ARM Debug XAML/C# 和 Release/.NET Native 构建、依赖闭包、签名通过。
- 最终主包 SHA256 AD3996420F9A385B0661A928221A83DD8C4F0B04B696B606BF07A2B2F43AB3F2。
  早期未完成版 SHA1C2A...已被覆盖，不可拿旧摘要验证最终包。
- 发布功能与操作说明 READING_FEATURES.md；差距表 LEGADO_GAP_AUDIT.md 已更新。
- 手机 Wi-Fi 门户192.168.3.159目前连接超时，尚未部署1.0.12.0；异步请用户亮屏/填写新地址已发出。
  之前1.0.11.0的图片缓存落盘验证已完成，当前不要声称新工具已在手机交互验证。

## 21. 2026-10-14：1.0.12.0 Wi-Fi 部署完成

- 用户明确要求“部署”，门户192.168.3.159恢复可达；覆盖安装最终1.0.12.0 ARM包。
- 部署前 SHA256 与第20节最终包一致；备份设置和书架后停止旧版，未卸载应用。
- WDP 安装返回202，等待状态由204转为200且 Code=0、Success=true 后启动，启动HTTP200。
- 已安装版本及进程包名均为 NovReader_1.0.12.0_arm__7xxdx2kd68cm6，PID6804。
  门户进程字段 IsRunning=false，应用日志仍确认本次启动载入数据并进入阅读页、成功读取第8章正文6236字。
- 启动日志确认书源1127个、书架9本；设置文件与部署前哈希一致。
  书架仍为相同9本，章节序号及滚动比例保留，当前书籍 lastReadTime 随启动更新。
- 部署状态、进程及前后设置/书架/启动日志保存于 _devtest/phone-backup/v112-wifi。
- 新工具的手机交互仍待用户实际使用验证；不将启动成功等同于书签/查找/缩放已实机验证。
