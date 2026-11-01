# NovReader 与原版阅读功能核对

核对时间：2026-10-13。范围：NovReader 1.0.12.0 的代码、Lumia 950 的运行日志、原版官网和保留的原版源码。
原版 GitHub 主仓库目前仅保留公告，因此源码对比采用其公开 fork 中的历史提交
`b30ae2ef9e858f24d459e2e22cc5e86bbec625ae`（2021-10-12）。这是一份基础功能差距核对，
不声称覆盖原版最后发行版或后续第三方分支的全部功能。

## JS 引擎实际状态

手机上的 JavaScriptCore 已正常初始化并执行真实书源脚本。1.0.9.0 日志中，
22:48:54–22:51:25 的 50P 漫画逐张完成 0–49 的解码；1.0.10.0 的四路下载也已由用户确认明显变快。
学外漫画的打包脚本解析、图片 URL 生成和 JPEG 显示已跑通。

目前宿主接口仍只实现部分原版能力：网络请求、规则字符串提取、变量、读取部分 Cookie、
Base64、MD5/SHA、部分 AES。原版扩展提供的响应对象、DOM 对象、文件/ZIP、更多编码与加密方法
还没有完整对应实现。[原版保留的 JS 扩展接口](https://github.com/ThorJsbox/gedoor-YueDu3.0/blob/b30ae2ef9e858f24d459e2e22cc5e86bbec625ae/app/src/main/java/io/legado/app/help/JsExtensions.kt)

手机持久日志确有 JavaImporter、org、cache、cookie 以及若干书源自定义函数缺失，
也有重复词法变量和 Map 调用错误。缺失函数可能来自未载入公共脚本、未实现的宿主接口或书源自身问题，
不能逐项都归因于引擎。当前仍共用一个 JS 全局上下文；执行锁保证桥调用串行，但不等同于按书源隔离全局变量。
书源间上下文隔离、脚本超时/终止和更完整的错误定位尚需补齐。

## 对照清单

| 能力 | NovReader 现状 | 仍缺少的部分 |
|---|---|---|
| 网络小说基本流程 | 已有聚合搜索、详情、目录、正文、换源、阅读进度 | 更完整的规则覆盖和失效书源诊断 |
| 常用规则 | 有 CSS/Jsoup 风格、XPath、JSONPath、正则、JS 和分页 | 自写解析器与原版 Jsoup/JSONPath 的完整语义仍有差距 |
| 书源导入和导出 | 有 JSON、URL、书源广场、启用和删除 | 未识别字段不会保留；再导出可能丢失原版扩展配置 |
| JS 公共脚本与宿主 | 已有真实 JSC 和部分 java.* 回调 | jsLib 载入、cache/cookie 扩展对象、Java/Jsoup 兼容对象与完整响应语义 |
| 登录和网页验证 | 可带请求头、复用部分系统 HTTP Cookie | 缺登录界面、登录检测脚本、WebView 页面/验证码流程；native 图片和系统请求 Cookie 也未完整共享 |
| 原版书源附加字段 | 保存常用搜索、详情、目录和正文规则 | 未实现登录、按源限速、发现规则等高级字段。[原版书源结构](https://github.com/ThorJsbox/gedoor-YueDu3.0/blob/b30ae2ef9e858f24d459e2e22cc5e86bbec625ae/app/src/main/java/io/legado/app/data/entities/BookSource.kt) |
| 正文附加处理 | 有 HTML 清理、图片 URL 规范化和规则内正则替换 | 缺独立正文替换、网页 JS 和图片样式等配置。[原版正文结构](https://github.com/ThorJsbox/gedoor-YueDu3.0/blob/b30ae2ef9e858f24d459e2e22cc5e86bbec625ae/app/src/main/java/io/legado/app/data/entities/rule/ContentRule.kt) |
| 漫画网络加载 | 图片请求头、原生备用下载、多 DNS、1–8 路并发；1.0.12.0 支持单张/批量失败重试 | Native 请求尚无及时取消；已发出的旧章节请求会等当前请求结束 |
| 漫画本地缓存 | 1.0.11.0 加入图片文件缓存、命中优先、相同请求合并；全本下载会补齐图片；手机已确认生成图片文件 | 之前 1.0.10.0 只缓存图片地址；重开章节的真机显示仍待反馈；缓存容量上限和按书管理尚未提供 |
| 漫画内存和交互 | 可连续滚动、按屏翻动、按宽度解码；1.0.12.0 加入单图缩放查看 | 整章图片控件仍全部常驻，缺视口回收、横向阅读、裁边/双页等功能 |
| 图片格式 | JPEG/PNG/GIF 等系统格式；请求不宣告 WebP | Win10M 没有原生 WebP 解码；本项目还没有 WebP/AVIF 软件解码器 |
| 发现与订阅 | 保存 exploreUrl，但没有完整入口和规则执行流程 | 缺发现分类/榜单和 RSS。[原版官网功能](https://gedoor.github.io/intro) |
| 本地书籍阅读 | 网络章节缓存可以重读；可导出多种格式 | 缺本地 TXT/EPUB 导入、扫描、目录规则及 EPUB 排版。导出 EPUB 不代表能阅读 EPUB。[原版本地导入](https://gedoor.github.io/docs/get-started-quickly/add-book) |
| 阅读排版 | 字号、行距、边距、字体、纸张主题、沉浸、常亮 | 缺自定义背景、细化段距/字距/加粗、简繁转换；只有按屏滚动和连续滚动，缺仿真/覆盖动画。[原版官网](https://gedoor.github.io/intro) |
| 内容净化 | 有规则内正则和基础 HTML 清理 | 缺用户管理的全局/按书替换规则，与基础清理不是同一功能。[原版官网](https://gedoor.github.io/intro) |
| 朗读/有声 | 有声地址可由 MediaElement 基础播放 | 缺正文 TTS、网络朗读引擎、朗读控制和定时。[原版 TTS 服务](https://github.com/ThorJsbox/gedoor-YueDu3.0/blob/b30ae2ef9e858f24d459e2e22cc5e86bbec625ae/app/src/main/java/io/legado/app/service/TTSReadAloudService.kt) |
| 书架 | 列表、进度、继续阅读、移除和换源 | 缺网格切换、分组/标签和批量追更管理；自动追更尚无完整实现。[原版官网](https://gedoor.github.io/intro) |
| 书签和笔记 | 1.0.12.0 加入书签、备注、跳转/编辑/删除和本章查找 | 缺选文高亮、全书搜索和跨书源书签迁移；新 UI 待手机验证。[原版书签结构](https://github.com/ThorJsbox/gedoor-YueDu3.0/blob/b30ae2ef9e858f24d459e2e22cc5e86bbec625ae/app/src/main/java/io/legado/app/data/entities/Bookmark.kt) |
| 备份和同步 | 书源、书架和设置保存在手机本地 | 缺应用内完整备份恢复和 WebDAV 云备份。[原版备份说明](https://github.com/ThorJsbox/gedoor-YueDu3.0/blob/b30ae2ef9e858f24d459e2e22cc5e86bbec625ae/app/src/main/assets/help/webDavHelp.md) |
| 外部集成 | 尚无对应入口 | 缺原版 Web 管理接口和 legado:// 导入。Android Content Provider 是平台接口，不宜直接复制到 UWP。[原版 API](https://github.com/ThorJsbox/gedoor-YueDu3.0/blob/b30ae2ef9e858f24d459e2e22cc5e86bbec625ae/api.md) |
| 导出 | TXT/HTML/Markdown/EPUB，支持多种编码 | 仍以章节文本为中心，漫画导出未把本地图片打包为完整离线电子书 |

## 建议的后续顺序

1. 验证新增阅读工具，并补视口图片回收和按书缓存管理；失败图片重试已在1.0.12.0加入。
2. 补公共 JS 脚本与常见宿主对象，隔离书源作用域；保留不认识的书源字段，避免导入/导出丢配置。
3. 完整共享 Cookie，增加登录和必要的网页验证流程；补按书源的限速与请求取消。
4. 增加发现、本地 TXT/EPUB、用户替换规则，以及更完整的书签/选文功能。
5. 再扩展 TTS、备份恢复/WebDAV、RSS 和外部管理接口。

本核对主要依据代码和当前手机记录；除已说明的漫画流程外，不代表这些功能都逐项做过真机测试。
原版参考文件下载在 `_devtest/legado-reference/`，未将其代码移植进应用。
