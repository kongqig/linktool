# LinkTool 项目状态

## 定位

Windows 桌面工具：将文件夹移动到其他位置，并在原路径创建 Junction 目录链接，使旧路径仍可访问新位置。

## 技术栈

- C# / .NET 8.0（net8.0-windows）/ WPF
- P/Invoke：kernel32（CreateFile/DeviceIoControl/CloseHandle）、rstrtmgr（Restart Manager）
- 外部工具：robocopy、rmdir、mklink /J、taskkill
- 依赖：System.Drawing.Common（提取 exe 图标）
- app.manifest：请求管理员权限

## 文件结构

| 文件                                 | 职责             |
| ---------------------------------- | -------------- |
| App.xaml(.cs)                      | WPF 入口         |
| MainWindow\.xaml                   | 界面布局           |
| MainWindow\.xaml.cs                | 核心逻辑（\~1500 行） |
| app.manifest                       | 管理员权限清单        |
| linktool.ico / icon.svg            | 图标             |
| build-debug/release/singlefile.bat | 一键构建脚本         |
| move-plan.md                       | 移动功能设计文档       |
| Project.md                         | 本文档            |

## 功能与流程

1. **移动文件夹**（6 阶段）
   - 阶段0 前置检查：路径/Junction/权限/占用校验
   - 阶段1 复制：.NET API 完整复制 → 失败降级 robocopy（提取权）
   - 阶段3 删源：标准删除 → 终止占用进程 → 管理员 rmdir /S /Q；失败仅提示不阻断
   - 阶段4 建链：Win32 API → 管理员重试 → mklink /J 降级
   - 阶段5 清理：释放锁、重置 UI、恢复按钮
2. **使用终端执行**：可见终端顺序 robocopy → rmdir → mklink /J
3. **创建目录链接**：仅建 Junction，不复制不删除
4. **管理员权限**：启动检测、runas 提权重启、各降级自动提权

## 关键实现

- Junction 创建：构造 MOUNT\_POINT 重解析点，FSCTL\_SET\_REPARSE\_POINT
- 占用检测：Restart Manager（RmStartSession/RmRegisterResources/RmGetList）
- 占用检测（备）：枚举进程主模块/模块列表路径
- 锁定检测：FileShare.ReadWrite|Delete 打开，区分权限不足 vs 共享冲突（32/33）
- 目录独占锁：CreateFile GENERIC\_WRITE|BACKUP\_SEMANTICS，\_lockHandles 管理，SafeHandleWrapper 释放

## 安全校验

源/目标非空合法；源存在；源非 Junction/符号链接；源≠目标；目标非源子目录；两端驱动器存在。

## 构建

```bash
dotnet build -c Debug                     # 调试
dotnet build -c Release                   # 发行
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false -o publish      # 单文件自包含
```

## 界面操作

| 按钮       | 功能         |
| -------- | ---------- |
| 以管理员身份运行 | runas 提权重启 |
| 移动       | 完整流程       |
| 使用终端执行   | 可见终端三步命令   |
| 创建目录链接   | 仅建链接       |

## 约束

禁止移动系统/驱动关键目录、"Microsoft" 目录、C:\Users\用户名 目录。

# 项目状态追踪

。
