# DSH Web Launcher 2.0 技术与 UI 设计

## 设计结论

采用现代 .NET WPF 重建界面壳，保留现有 DSH 引擎的行为和数据边界。首发目标为 Windows x64 自包含发布；核心代码不写死 x64 路径，以便后续增加 ARM64 构建。

不建议继续在当前 WinForms 单文件中叠加页面。现有 `src/Program.cs` 已同时承载配置模型、进程控制、更新、绘制和事件处理，继续追加设置和更新页会放大布局与生命周期风险。

## 建议的模块边界

```text
src/
  DshLauncher.Core/       配置、状态机、DSH 进程控制、健康检查、版本比较
  DshLauncher.App/        WPF 窗口、页面、主题、命令、托盘适配
  DshLauncher.Updater/    独立更新进程，下载、校验、替换、重启
  Installer/              每用户安装、卸载、快捷方式和 Release 打包
```

第一阶段可以先把现有 `DshEngine` 原样抽到 Core，再逐步替换内部实现；不在迁移时重写 DSH 启动协议。

## Core 层

### 状态机

```text
Unknown -> Detecting -> NotInstalled
                    -> Stopped
                    -> Starting -> Running
                              -> Error
Running -> Stopping -> Stopped
Error -> Detecting / Stopped
```

每次状态变更携带可读阶段、错误代码、可用命令和时间戳。UI 只根据状态模型决定显示什么，不能自行通过多个按钮的 `Visible` 状态推断业务状态。

### 配置模型

配置继续存放在 `%LOCALAPPDATA%\\DSHWebManager\\config.json`，增加版本号和迁移逻辑：

- `Workspace`
- `Port`
- `Language`
- `Theme`：system/light/dark
- `CloseBehavior`：ask/tray/exit
- `AutoOpenBrowser`
- `AutoCheckLauncherUpdates`
- `StartAtSignIn`
- `LaunchMinimized`（首版可保留字段，默认关闭）

旧版缺失字段使用默认值，不覆盖用户已有工作区、端口、语言和主题。

### DSH 引擎

保留并测试现有能力：安装发现、受管运行时安装、HTTP 健康检查、PID/启动时间/命令行校验、安全停止、npm DSH 版本检查和受管 DSH 更新。

DSH CLI 更新仍使用 npm 官方 registry；启动器更新只使用 GitHub Releases，两个状态在 UI 中分开。

## WPF UI 结构

主窗口由窗口标题栏、页面导航、内容区和状态提示组成。内容区不使用绝对坐标：

- 外层 `Grid` 负责标题栏、导航和内容区。
- 首页使用 `Grid` + 自适应列，窄窗口时切成单列。
- 卡片只用于状态、配置摘要和更新信息，页面本身不再层层套卡片。
- 主操作固定在状态区附近，避免用户在页面底部寻找启动按钮。
- 关闭、最小化、最大化/恢复顺序保持 Windows 常见顺序。

页面：

- `HomePage`：安装、启停、打开 Web、状态和当前运行信息。
- `SettingsPage`：常规、DSH Web、外观、更新、数据目录。
- `AboutPage`：三种版本、Release 信息、更新检查和手动安装。

## 关闭与托盘

使用 `NotifyIcon` 适配器隔离 Windows 托盘 API。关闭拦截流程：

1. 读取 `CloseBehavior`。
2. `ask` 时显示二选一对话框和“始终如此”。
3. 勾选后保存选择。
4. `tray` 隐藏窗口，`exit` 退出启动器。

退出启动器不停止 DSH Web。托盘菜单的“停止 DSH Web”仍需走 Core 层的安全进程校验。

## 启动器更新

### 数据源

- GitHub `releases/latest` API。
- Release 资产包含 x64 Setup、便携 ZIP 和 SHA-256 校验文件。
- 版本使用 SemVer 比较，忽略 `v` 前缀。

### 流程

```text
检查 -> 展示版本/说明 -> 用户确认 -> 下载临时文件
     -> SHA-256 校验 -> 启动 Updater -> 关闭主程序
     -> 替换安装目录 -> 保留配置/日志 -> 重启主程序
```

Updater 必须位于安装目录之外运行，避免 Windows 文件锁导致自更新失败。更新过程记录到 `%LOCALAPPDATA%\\DSHWebManager\\logs`，失败时保留旧 exe，并能打开 Release 页面完成手动更新。

## 主题与本地化

- 主题使用 WPF `ResourceDictionary`，不在代码中散落颜色。
- `system/light/dark` 三态；系统主题变化时只刷新资源，不重建业务状态。
- 中文和英文文本集中在资源文件，不在事件处理器中拼接长文案。
- 图标采用内置几何路径或现有鲸鱼资源，保持矢量渲染；深色主题使用白色鲸鱼，浅色主题使用黑色鲸鱼。

## 测试策略

- Core 单元测试：版本比较、配置迁移、状态转换、端口冲突、进程归属校验、更新资产校验。
- WPF UI 测试：360/640/1024/1440 宽度、浅色/深色、中英文、键盘焦点、关闭弹窗和托盘菜单。
- 端到端测试：未安装、首次安装、启动、自动打开浏览器、停止、未知端口占用、启动器更新成功/失败、卸载。
- 发布前检查：安装包 SHA-256、无管理员权限安装、旧版升级、`.dsh` 与工作区不变、x64 Windows 10/11。

## 迁移与回滚

保留当前 WinForms 构建和 1.5.0 Release 作为回滚基线。新版本使用独立安装目录或带版本的发布目录进行灰度验证；确认启动、停止、安装和升级后再切换默认 Release。任何阶段失败都可以回到旧版，不需要恢复用户数据。
