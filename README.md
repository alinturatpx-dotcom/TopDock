# TopDock

A tiny transparent dock on the left edge of your Windows screen, for tidying up leftover desktop icons.

Single-file C#, no dependencies, near-zero CPU when idle.

[中文说明 ↓](#中文说明)

## Features

- Vertical bar on the left edge, auto-hide or pinned
- Transparent background, opaque icons
- Native icons, no shortcut arrow
- Drag to reorder icons
- One-click import of desktop icons
- System icons (This PC, Recycle Bin, Control Panel) and custom commands

## Build

Windows 10/11 only. No install needed, it uses the built-in .NET Framework.

1. Put `TopDock.cs` and `build.bat` in the same folder
2. Double-click `build.bat`
3. Run `TopDock.exe`

## Usage

- Right-click the bar to open all settings
- Right-click → Import desktop icons
- Move the mouse to the far left edge of the screen to show the bar

Custom commands: right-click → Edit custom commands, one per line as `name|command|args`.

```
Task Manager|taskmgr.exe|
Downloads|explorer.exe|shell:Downloads
```

Settings are stored in `%AppData%\TopDock\`.

## License

[MIT](LICENSE)

---

## 中文说明

Windows 屏幕左侧的透明收纳栏，用来收纳桌面残留的图标。单文件 C# 编写，无需安装依赖，空闲时几乎不占 CPU。

### 功能

- 左侧竖排，自动隐藏或固定常显
- 背景透明，图标不透明
- 原生图标，无快捷方式小箭头
- 拖动调整图标顺序
- 一键导入桌面图标
- 支持系统图标（此电脑、回收站、控制面板）和自定义命令

### 编译

仅限 Windows 10/11，使用系统自带的 .NET Framework，无需安装。

1. 把 `TopDock.cs` 和 `build.bat` 放在同一文件夹
2. 双击 `build.bat`
3. 运行 `TopDock.exe`

### 使用

- 右键小条打开全部设置
- 右键 → 导入桌面图标
- 鼠标移到屏幕最左边缘，小条滑出

自定义命令：右键 → 编辑自定义命令，每行一个，格式为 `名称|命令|参数`。

设置保存在 `%AppData%\TopDock\`。
