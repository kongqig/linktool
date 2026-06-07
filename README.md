# LinkTool - 文件夹移动与目录链接工具

将文件夹移动到其他位置，并在原路径创建 Junction 目录链接，使程序仍能通过原路径正常访问。

## 功能

- **移动文件夹**：将源文件夹内容复制到目标位置，删除源文件夹后创建 Junction 链接
- **终端执行**：通过可见终端窗口执行 robocopy/rmdir/mklink 命令
- **创建目录链接**：单独创建 Junction 目录链接
- **管理员权限**：支持以管理员身份运行，解决权限不足问题
- **进程占用检测**：执行前检查文件占用和访问权限

## 操作流程

移动操作按以下阶段执行：

1. **前置检查** — 验证路径合法性、文件访问权限、文件占用情况
2. **复制目录** — .NET API 复制 → robocopy 降级
3. **删除源文件夹** — 标准删除 → 终止占用进程 → 管理员终端删除
4. **创建目录链接** — Win32 API → 管理员权限重试 → cmd mklink /J 降级
5. **清理** — 释放锁定、重置状态

## 技术栈

- .NET 8.0 / WPF
- Win32 API（Junction 创建、Restart Manager 文件占用检测）
- robocopy / mklink 命令行工具

## 构建

提供三个一键构建脚本：

| 脚本                     | 说明                     | 输出位置                                      |
| ---------------------- | ---------------------- | ----------------------------------------- |
| `build-debug.bat`      | 调试版                    | `bin\Debug\net8.0-windows\linktool.exe`   |
| `build-release.bat`    | 发行版                    | `bin\Release\net8.0-windows\linktool.exe` |
| `build-singlefile.bat` | 单文件版（自包含运行时，无需安装 .NET） | `publish\linktool.exe`                    |

也可手动构建：

```bash
# 调试版
dotnet build -c Debug

# 发行版
dotnet build -c Release

# 单文件版
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o publish
```

## 使用

1. 在 "From" 中选择要移动的文件夹
2. 在 "To" 中选择目标位置
3. 点击 "移动" 或 "使用终端执行"

> 建议以管理员身份运行，可避免大部分权限不足问题。

## 协议

本软件使用 MIT 协议开源。
