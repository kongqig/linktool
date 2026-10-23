# LinkTool 项目状态

## 定位
Windows 桌面工具：将文件夹迁移到其他位置，并在原路径创建 Junction 目录链接，使旧路径仍可访问新位置。支持单个与批量操作。
当前版本：**2.1**（2.0 = 上一次完整重构；1.0 = 初始版本，当时未同步修改版本号）。

## 技术栈
- C# / .NET 8.0（net8.0-windows）/ WPF
- P/Invoke：kernel32（CreateFile/DeviceIoControl/CloseHandle）、rstrtmgr（Restart Manager）
- 外部工具：robocopy、rmdir、mklink /J、taskkill
- 依赖：System.Drawing.Common（提取 exe 图标）
- app.manifest：asInvoker（运行时自行判断提权）

## 架构（本次重构后）
- 主窗口 = 侧边栏导航 + 多页面容器 + 底部管理员警告条
- 页面懒加载缓存于字典，按 Tag 切换
- 共享服务层（linktool.Services）承载全部核心逻辑，页面层只做 UI/编排
- UI：现代极简浅色主题（App.xaml 集中样式资源）

## 文件结构
```
App.xaml(.cs)              全局样式资源 / 入口（含 CLI 分流与双击隐藏控制台）
MainWindow.xaml(.cs)       导航 Shell + 页面容器 + 管理员警告条 + 任务运行时关闭拦截
Cli/
  CliRunner.cs             命令行入口（migrate/link/retreat/batch-*）
Dialogs/
  ConfirmDialog.xaml(.cs)  现代化确认弹窗（内容可选中复制 + 复制全部）
  MessageDialog.xaml(.cs)  现代化信息/警告/错误弹窗（内容可选中复制 + 复制全部）
  ProcessListDialog.xaml(.cs) 占用进程列表弹窗（勾选强制结束，关闭后外层复检）
Services/
  AppSettings.cs           路径历史持久化（%LOCALAPPDATA%\LinkTool\settings.json）
  AdminHelper.cs           管理员检测/提权/可见终端执行
  BusyState.cs             全局任务占用标记（供主窗口关闭拦截）
  DialogHelper.cs          统一弹窗辅助（Confirm/Info/Warn/Error）
  JunctionHelper.cs        Junction 创建（Win32→非管理员时重试→mklink /J 降级）
  FileLockHelper.cs        占用检测（Restart Manager/模块枚举）+ ScanDirectory 综合扫描（权限/占用/大小一次遍历）
  FileOps.cs               复制(.NET/robocopy)/统计/删除源/终止占用
  DirectoryLockManager.cs  目录独占锁定（CreateFile 句柄）
  MoveValidator.cs         迁移/行级安全校验
  PathHistoryHelper.cs     路径输入历史 + 文件夹选择对话框（带初始目录记忆）
  PastePathParser.cs       批量粘贴解析（换行分行 / |分列 / 引号段提取）
  PreCheckHelper.cs        占用处理闭环（强制结束→复检）+ 空间检查（含免责确认）
  RetreatHelper.cs         退迁（反向迁移）：解析目标→复制回→删链接与旧目标
  SizeHelper.cs            文件夹大小计算（后台/可取消）+ 磁盘可用空间 + 字节格式化 + 1GB 安全余量常量
Pages/
  MigrationPage            单个迁移（含终端执行模式）
  BatchMigrationPage       批量迁移（多对一/多对多）
  LinkPage                 单个链接（链接位置→目标路径）
  BatchLinkPage            批量链接
  RetreatPage              单个退迁（反向迁移）
  BatchRetreatPage         批量退迁
  HelpPage / AboutPage     帮助（6 Tab）/ 关于
  LinkScanPage             链接扫描（递归列出 Junction/符号链接本体与目标，可跟随/限深）
```

## 页面/功能
| 页面 | 功能 |
| ---- | ---- |
| 迁移 | 单文件夹：复制→删源→建链接；含"使用终端执行"（robocopy/rmdir/mklink 可见终端） |
| 批量迁移 | 列表表单：通用目标目录(多对一) + 每行独立 To(多对多)；粘贴多行/选择/拖拽添加；后台逐行静默处理 |
| 链接 | 链接位置(Link)→目标路径(Target) 创建 Junction |
| 批量链接 | 列表表单：通用目标目录 + 每行独立 Target；粘贴多行/选择/拖拽添加 |
| 退迁 | 单链接反向迁移：解析目标→复制回原路径→删除链接与旧目标 |
| 批量退迁 | 列表表单，逐行退迁；粘贴多行/选择/拖拽添加 |
| 帮助 / 关于 | 静态说明 / 版本信息 |

## 关键实现
- Junction：构造 MOUNT_POINT 重解析点，FSCTL_SET_REPARSE_POINT；降级 mklink /J
- 占用检测：Restart Manager（RmStartSession/RmRegisterResources/RmGetList）+ 模块枚举
- 综合扫描：FileLockHelper.ScanDirectory 一次遍历同时产出 无权限文件/锁定文件/总大小/文件数（权限检查与空间统计零额外开销）
- 占用处理闭环：PreCheckHelper.ResolveLocksAsync（ProcessListDialog 强制结束 → 自动复检 → 通过继续/放弃终止）
- 空间检查：需要 = 内容大小 + SpaceMarginBytes(1GB)；交互场景不足弹免责确认可强行执行，批量静默直接判失败
- 锁定检测：FileShare.ReadWrite|Delete 打开，区分权限不足 vs 共享冲突（32/33）
- 目录独占锁：CreateFile GENERIC_WRITE|BACKUP_SEMANTICS，DirectoryLockManager 管理
- 路径历史：JSON 持久化，ComboBox 可编辑下拉，回车/使用/选择时记忆；浏览对话框初始目录 = 编辑框内容 → 历史最新一条
- 粘贴解析：换行分行；"|"分列（批量迁移 From|行To，批量链接 链接目录|Target）；"..."引号段提取；无效路径加入并标红（IsInvalid/IsInvalidFrom/IsInvalidTo/IsInvalidTarget）
- 大小计算：SizeHelper 后台遍历可取消；单页按钮（迁移 From 算大小/To 算可用空间，链接 Target 算大小，退迁 Link 算可用空间）；批量页行内"算"+"计算全部"（串行）
- 退出安全：BusyState 计数标记全部任务入口；MainWindow.OnClosing 拦截并弹窗确认
- 链接防护：枚举统一跳过 ReparsePoint（AttributesToSkip，FileLockHelper.EnumerationOptionsSkipReparse）——ScanDirectory/SizeHelper/GetAllFiles/CopyDirectoryFull 不跟随源内部链接（防循环无限递归、防跨盘重复复制）；FindReparseSubDirs 环安全枚举链接子目录并随 ScanResult.ReparseDirs 返回；单迁移检出链接时弹窗确认，批量状态列显示"跳过 N 个链接子目录"，CLI 打印提示；目标位置已是链接时 GUI/CLI/批量均硬拦截；CreateJunction 拒绝链接与目标重叠（相同/互为祖先，防 c:\a\loop→c:\a 构环）与 UNC 目标；robocopy 全部加 /XJ

## UI/交互（本次要求已落地）
- "移动"按钮改名为"迁移"
- 所有操作按钮均含 ToolTip 悬停提示
- 按钮点击反馈：三种按钮样式模板加 Overlay 叠加层，悬停/按下呈半透明黑叠加（按下 #33 透明度黑，明显可见）；禁用半透明
- 按钮宽度自适应：全部固定 Width 改 MinWidth + 样式内边距，文本随内容自动撑开（修复"算可用空间"末字截断）
- 弹窗消息区改为无边框只读 TextBox：可选中、Ctrl+C、右键复制（无需额外按钮）
- 页面说明/结果文字全部可选中复制：新增 App.xaml `SelectableHint` 样式（外观同 Hint 的只读 TextBox），应用于帮助页全部内容、各页简介/说明卡/安全警告、大小与可用空间结果、退迁目标解析、状态结果、关于页
- 关闭程序拦截：任务执行中点关闭/Alt+F4 弹窗确认（关闭程序 / 留在程序）
- 帮助页重构为 6 个 Tab：快速上手 / 功能详解 / 批量操作（含粘贴格式教程）/ CLI 命令 / 常见问题 / 安全警告（含免责声明）
- 单页新增按钮：迁移 From"算大小"、To"算可用空间"；链接 Target"算大小"；退迁 Link"算可用空间"（结果显示在路径框旁小字）
- 批量迁移页：通用目标目录"算可用空间"；列表新增"大小"列 + 行内"算"按钮 + 工具栏"计算全部"（串行逐行）
- 批量退迁页：列表新增"大小"列（解析链接目标后统计）+ 行内"算"按钮 + 工具栏"计算全部"
- 批量粘贴统一格式：换行分行、"|"分列（批量迁移 源|行To；批量链接 链接目录|Target；批量退迁取第1列）、引号段自动提取、无效路径加入并标红、主路径去重
- 路径编辑框（ComboBox）记忆下拉历史
- 迁移/链接保持精简；批量页用列表表单（首选多对一/多对多方案）
- 非管理员启动弹窗 → 主页底部警告信息条 + 侧边栏管理员状态，"提权运行"按钮
- 现代极简浅色主题，侧边栏导航
- 现代化确认弹窗（Dialogs/ConfirmDialog），替换全部 MessageBox 确认，批量操作前均有确认
- 现代化信息/警告/错误弹窗（Dialogs/MessageDialog），替换全部 MessageBox 提示
- 批量页添加区统一（粘贴多行/选择/拖拽），批量链接"添加"填入目标路径列
- 批量列表启用横向滚动（固定列宽 + 状态列自适应），显示完整错误信息

## 安全校验
源/目标非空合法；源存在；源非 Junction/符号链接；源≠目标；目标非源子目录；两端驱动器存在。

## CLI 支持
- 程序为**控制台子系统（OutputType=Exe）**，由终端启动时输出天然干净（无叠加/错乱）；双击启动 GUI 时隐藏控制台窗口
- 带命令行参数启动即进入 CLI 模式，非交互执行并退出；无参数启动 GUI
- 命令：`migrate`（--from/--to）、`link`（--link/--target，链接目录\目标同名）、`retreat`（--link）、`batch-migrate`（--from 分号分隔/--to）、`batch-link`（--target 分号分隔/--link-dir）、`help`
- 实现：Cli/CliRunner.cs；App.OnStartup 按参数分流，GUI 模式用 GetConsoleProcessList 判断是否双击以隐藏控制台

## 构建
```
dotnet build -c Debug                     # 调试
dotnet build -c Release                   # 发行
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false -o publish      # 单文件自包含
```
CLI 用法：`linktool help`、`linktool migrate --from <源> --to <目标目录>` 等。
当前状态：Debug 构建通过（0 警告 0 错误），CLI 端到端验证通过。已完成两轮功能更新（见下）。

## 本次更新 2.1（2026-10）
- 浏览按钮记忆目录：编辑框非空 → 打开该目录；为空 → 打开该键历史最新一条；均无效 → 系统默认
- 大小/空间：SizeHelper + 全部单页与批量页"算大小/算可用空间/计算全部"手动按钮（全手动触发，不自动遍历）
- 检测增强：单页执行前统一 权限 → 占用闭环（ProcessListDialog 强制结束→自动复检→通过继续/放弃终止）→ 空间检查（1GB 余量，不足弹免责确认可自担风险执行）；退迁页/链接页新增此前缺失的检测；批量页逐行静默检查直接标失败（原因含进程名/需要与可用空间）
- FileLockHelper 重构：CheckFileAccessibility/CheckFileLocks 合并为 ScanDirectory（权限+占用+大小一次遍历），CLI 同步更新
- 弹窗可复制：MessageDialog/ConfirmDialog 消息区改只读 TextBox（选中/Ctrl+C/右键）
- 文本可选中补丁：帮助页与各页说明/结果文字改用 SelectableHint（只读 TextBox），全程可选中复制
- 粘贴解析：PastePathParser 统一（换行/"|"分列/引号段/无效标红/去重），修复批量链接页无法填充两列问题
- 帮助页：6 Tab 重构，含批量粘贴格式教程与免责声明全文
- 退出安全：BusyState + MainWindow.OnClosing 拦截，任务运行时关闭需确认
- 链接防护补丁：枚举跳过 ReparsePoint 防循环/防跟出；目标位置是链接硬拦截（GUI/CLI/批量）；CreateJunction 重叠与 UNC 校验；robocopy /XJ；已用"回环+外部链接迁移"与三类重叠建链 CLI 端到端验证通过
- 链接扫描页（2.1 新增页面）：侧边栏"链接扫描"；扫描路径=历史下拉编辑框（回车记忆）+ 选择按钮（空时打开上次目录）；勾选框"启用链接递归"（默认不勾，遇到链接只记录不进入）；递归限制输入（0=禁用同未勾选；负数/非法/超过100 报错；跟随层数超过 10 警告循环链接）；遍历深度输入（目录层级上限，0=不限制，≤100，链接跳转后归零，与链接递归互不影响，超限目录计数并写入状态栏）；勾选框"显示隐藏/系统目录"（默认不勾——.NET 简单枚举重载不排除隐藏项，会扫出「开始」菜单/Application Data 等隐藏系统兼容链接，默认跳过，勾选后显示）；特殊链接目标列标注：自指链接/目标不存在/无访问权限（经链接路径探测，即"点进去"被拒）/非常规链接类型 0x……（reparse 标签码）/系统特殊链接无法解析；目标解析双通道：RetreatHelper.ResolveTarget（.NET LinkTarget）失败时回退 JunctionHelper.ReadReparseTargetRaw（FSCTL_GET_REPARSE_POINT 原始数据直读，解析 MOUNT_POINT/SYMLINK 的 SubstituteName，实测「开始」菜单/悬空链接/普通链接均正确，Application Data 类经链接探测标记无访问权限）；属性为 -1（全 1）的不可读条目（悬空/损坏链接，如 JianyingPro NoiseReductionCache——Test-Path 为 False 但本体仍在）单独列出标注"无法读取条目信息"，且枚举不做 AttributesToSkip 过滤（-1 条目会被过滤器误删），隐藏/系统/链接手动判断；结果列表=链接本体/目标位置/层数（列宽 * 自适应+换行+悬停完整提示，路径单元格为只读 TextBox——文本可直接选中/Ctrl+C/右键复制，弥补只读列表无法双击编辑查看的短板），回环路径去重（同一链接只记录一次），目标不存在时附加标记，支持一键复制；后台栈式遍历（回环天然有界，受 limit ≤ 100 约束）；布局紧凑单卡
- DataGrid 长文本可见性：三个批量页所有文本列（From/To/LinkPath/Target/大小/状态）加悬停完整提示（ToolTip 绑定字段），状态列失败原因悬停可看全

## 最近修复
- 批量链接页：重复运行时基于上次自动解析的链接位置生成嵌套路径 → 新增 `IsAutoResolved` 标记，运行前重置自动解析的链接位置
- 单迁移页：移除未使用的 `DirectoryLockManager`（激活会与 robocopy 降级冲突）与死代码取消令牌（`CancellationTokenSource`）
- 批量迁移：复制回调显示文件级进度 `[已完成/总数]`；复制前补充权限预检
- CLI `migrate`：链接创建失败时（文件已迁移、源已删除）返回 0 并给出手动修复提示，不再误报失败
- 删除源目录：`allowAdminFallback=false`（批量静默）时同时跳过强杀占用进程，避免未经确认终止用户程序
- 主窗口 `ResizeMode` 调整为 `CanResizeWithGrip`，允许调整大小

## 约束
代码校验（MoveValidator）不强制拦截系统关键目录，仅在迁移页以提示文案警示用户：请勿迁移系统关键目录/驱动目录、含 "Microsoft" 字样的目录、C:\Users\用户名 目录，以免造成系统损坏。

## 协议
MIT。