# LinkTool 项目状态

## 定位
Windows 桌面工具：将文件夹迁移到其他位置，并在原路径创建 Junction 目录链接，使旧路径仍可访问新位置。支持单个与批量操作。

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
MainWindow.xaml(.cs)       导航 Shell + 页面容器 + 管理员警告条
Cli/
  CliRunner.cs             命令行入口（migrate/link/retreat/batch-*）
Dialogs/
  ConfirmDialog.xaml(.cs)  现代化确认弹窗
  MessageDialog.xaml(.cs)  现代化信息/警告/错误弹窗
Services/
  AppSettings.cs           路径历史持久化（%LOCALAPPDATA%\LinkTool\settings.json）
  AdminHelper.cs           管理员检测/提权/可见终端执行
  DialogHelper.cs          统一弹窗辅助（Confirm/Info/Warn/Error）
  JunctionHelper.cs        Junction 创建（Win32→非管理员时重试→mklink /J 降级）
  FileLockHelper.cs        占用检测（Restart Manager/模块枚举）+ 权限/锁定检测
  FileOps.cs               复制(.NET/robocopy)/统计/删除源/终止占用
  DirectoryLockManager.cs  目录独占锁定（CreateFile 句柄）
  MoveValidator.cs         迁移/行级安全校验
  PathHistoryHelper.cs     路径输入历史 + 文件夹选择对话框
  RetreatHelper.cs         退迁（反向迁移）：解析目标→复制回→删链接与旧目标
Pages/
  MigrationPage            单个迁移（含终端执行模式）
  BatchMigrationPage       批量迁移（多对一/多对多）
  LinkPage                 单个链接（链接位置→目标路径）
  BatchLinkPage            批量链接
  RetreatPage              单个退迁（反向迁移）
  BatchRetreatPage         批量退迁
  HelpPage / AboutPage     帮助 / 关于
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
- 锁定检测：FileShare.ReadWrite|Delete 打开，区分权限不足 vs 共享冲突（32/33）
- 目录独占锁：CreateFile GENERIC_WRITE|BACKUP_SEMANTICS，DirectoryLockManager 管理
- 路径历史：JSON 持久化，ComboBox 可编辑下拉，回车/使用/选择时记忆

## UI/交互（本次要求已落地）
- "移动"按钮改名为"迁移"
- 所有操作按钮均含 ToolTip 悬停提示
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
当前状态：Debug 构建通过（0 警告 0 错误），CLI 端到端验证通过。已完成一轮 Bug 修复（见下）。

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