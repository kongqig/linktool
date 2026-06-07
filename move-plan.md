# 移动按钮功能实现计划

## 核心流程（6个阶段）

### 阶段0：前置检查
- 禁用移动按钮
- 检查 from 是否为 Junction 或符号链接 → 是则终止，弹窗提示
- 遍历 from 下所有文件，检查读取权限 → 无权限则收集拒绝文件列表，弹窗显示，终止
- 检查 from 下是否有文件被占用（尝试以独占方式打开每个文件）→ 有占用则弹窗提示需要退出相关程序，终止

### 阶段1：复制 from 目录到 to 目录下
- 目标路径 = `to\from文件夹名`（如 from=C:\test, to=E:\to → 复制到 E:\to\test）
- 检查 to 下是否已存在同名文件夹 → 存在则检查是否为空 → 非空则弹窗终止，空则继续
- 使用 robocopy 级别的完整复制（属性、时间戳、ACL等）
- 实现方式：先尝试 .NET API 复制（保留属性），失败则降级到管理员权限调用 robocopy
- 复制过程中独占锁定目标文件夹，防止其他进程写入
- 更新 UI：进度条、总文件数、已完成数、当前文件名

### 阶段2：复制失败降级策略
- 如果 .NET API 复制失败：
  1. 先检查是否有管理员权限
  2. 没有管理员权限 → 请求提权重启
  3. 有管理员权限 → 解锁文件夹独占，以管理员权限调用可见终端执行 robocopy
  4. robocopy 命令：`robocopy "from" "to\from文件夹名" /E /COPYALL /DCOPY:T`

### 阶段3：删除源文件夹
- 复制完成后，先解锁目标文件夹
- 异步删除 from 目录（包括目录本身），效果等同 `rmdir /S /Q`
- 更新 UI 进度
- 删除前检查是否有进程占用 → 有则尝试终止
- 终止失败 → 以管理员权限调用终端终止进程或执行 rmdir /S /Q
- 仍然失败 → 弹窗告知用户源文件夹删除失败，让用户自行清理，**不阻断后续流程**

### 阶段4：创建目录链接
- from 删除完成后，在 from 原位置创建 Junction 链接指向 to 下的新目录
- 如 C:\test(Junction) → E:\to\test
- 创建方式：Win32 API → 管理员权限重试 → cmd mklink /J 前台终端
- 全部失败 → 弹窗告知用户

### 阶段5：完成清理
- 解锁所有文件夹占用
- 恢复 UI 状态（解锁按钮、重置进度条等）
- 释放本程序资源（不清理系统资源）
- 编辑框内容保留不清空

---

## 技术要点

### 文件独占锁定
- 使用 `FileStream` 以独占模式打开目标文件夹下的关键文件/目录
- 或使用 Win32 `CreateFile` 以独占方式打开目录句柄
- 需要维护一个锁列表，在完成或异常时全部释放

### 文件占用检测
- 遍历所有文件，尝试以 `FileShare.Read` 模式打开
- 如果无法以 `FileShare.None` 打开，说明被占用

### 完整属性复制
- .NET 方式：`File.Copy` + 手动复制属性（创建时间、修改时间、属性、ACL）
- robocopy 降级：`/E /COPYALL /DCOPY:T` 复制所有内容+属性

### 进程占用检测与终止
- 使用 `RestartManager` API 检测哪个进程占用文件
- 或使用 `NtQuerySystemInformation` 枚举句柄
- 简化方案：尝试独占打开失败即判定占用，用 taskkill 终止

### Junction 检测
- `DirectoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)` + 检查 reparse tag

### UI 更新
- 使用 `Dispatcher.Invoke` 在后台线程更新 UI
- 进度信息：`ProgressCountText`（已完成/总数）、`ProgressBar`、`CurrentFileText`

---

## 方法拆分

```
MoveButton_Click (入口)
  ├─ PreCheckAsync()           // 阶段0：前置检查
  ├─ CopyDirectoryAsync()      // 阶段1：复制目录
  │   └─ CopyWithRobocopy()    // 降级：robocopy
  ├─ DeleteSourceAsync()       // 阶段3：删除源
  │   └─ DeleteWithCmd()       // 降级：rmdir /S /Q
  ├─ CreateJunctionAsync()     // 阶段4：创建链接
  │   └─ CreateJunctionWithCmd() // 降级：mklink /J
  └─ Cleanup()                 // 阶段5：清理
```

## 辅助方法

- `CheckFileAccessibility(path)` → 检查所有文件可访问性，返回不可访问列表
- `CheckFileLocks(path)` → 检查文件占用，返回被占用的文件列表
- `IsJunctionOrSymlink(path)` → 检查是否为链接
- `LockDirectory(path)` → 独占锁定目录
- `UnlockAll()` → 释放所有锁定
- `UpdateProgress(current, total, currentFile)` → 更新 UI 进度
- `RunAsAdminWithTerminal(command, args)` → 以管理员权限调用可见终端

---

## 状态变量

```csharp
private List<FileStream> _lockStreams = new();  // 文件锁列表
private CancellationTokenSource? _cts;          // 取消令牌
```
