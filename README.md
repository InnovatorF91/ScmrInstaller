# StarCraft: Mass Recall Installer (C#)
一个用于 **自动下载 / 离线解压 / 修复结构 / 创建快捷方式** 的《星际争霸：重制战役（StarCraft: Mass Recall）》一键安装器。

本工具完全基于“复制粘贴式手动安装流程”实现——不修改游戏文件，只做 **文件复制**、**目录结构修复**、**解压 ZIP**、**寻找启动器地图** 等操作。

适用于：
- 想快速安装 Mass Recall 的玩家  
- 不想手动下载 5~6 个 ZIP / 拆包 / 复制几十个目录  
- 想要“失败时自动回退到本地安装包”的稳定安装体验的用户  

-----

## ✅ 功能特点

### **1. 自动识别 SC2 安装路径**
- 自动读取注册表（包含 WOW6432Node / Capabilities / Classes / Uninstall）
- 兼容非标准安装路径（例如：D:\ 或 E:\StarCraft II）
- 若找不到，将提示玩家先安装 SC2

### **2. 支持「自动下载 + 离线安装」模式**
- 在线时可从 `packages.json` 中的 ForgeCDN 直链自动下载 ZIP  
- 下载失败（超时/断线/403 等）会立即中止并自动切换为 **本地 ZIP 安装**（Fail-safe）
- 离线状态下将从安装器目录查找本地 ZIP

### **3. 全自动解压所有 ZIP**
每个 ZIP 解压到独立文件夹，并自动识别结构中的：

- `Starcraft Mass Recall` 主目录  
- `Extras/8. Enslavers Redux`  
- `Maps/`  
- `Mods/Assets/Localization/Cinematics`  

并执行正确的目标复制路径。

### **4. 自动修复问题文件结构**
Mass Recall 的 ZIP 包结构常存在细微差异，本安装器会自动修正：

- `.SC2Map` 被错误复制到 `E:\StarCraft II\Maps` 根目录  
- Redux 内容未放入 `Extras`  
- Assets/Cinematics 未覆盖旧文件  

最终确保结构符合 Mass Recall 官方推荐布局。

### **5. 自动创建桌面快捷方式**
自动寻找：
{SC2}\Maps\Starcraft Mass Recall\SCMR Campaign Launcher.SC2Map

并为你创建桌面图标：
StarCraft Mass Recall.lnk

快捷方式参数为：
SC2Switcher_x64.exe "<launcher-map>"

无需玩家手动通过编辑器开启。

### **6. 完整的 Fail-Fast 下载逻辑**
如果任一压缩包下载失败：

- 立即抛出错误  
- 放弃整个下载阶段  
- **自动改为使用本地 ZIP 解压**  

这保证玩家至少能成功安装“已有的包”，不会因为部分下载失败而完全无法启动游戏。

-----

## ✅ 使用方式

### **方式 A：直接运行（推荐）**
1. 下载本项目 Release 中的 `ScmrInstaller.exe`  
2. 放到任意文件夹  
3. 双击运行（建议“右键 → 以管理员身份运行”）  

> 若系统未以管理员身份运行，安装器将尝试自动重新以管理员身份启动。

### **方式 B：自动下载模式**
在同目录放置 `packages.json`：

```json
{
  "packages": [
    "https://mediafilez.forgecdn.net/files/3246/208/SCMR_v8.0.1.zip",
    "https://mediafilez.forgecdn.net/files/3239/552/SCMR_assets_v8.0.1.zip",
    "https://mediafilez.forgecdn.net/files/3147/425/SCMR_localization_v8.0.1.zip",
    "https://mediafilez.forgecdn.net/files/2819/339/SCMR_cinematics_v8.0.1.zip",
    "https://mediafilez.forgecdn.net/files/3246/278/Enslavers_Redux_v8.0.1.zip"
  ]
}
```

启动安装器后即可自动下载安装。

方式 C：离线 / 手动安装

将 ZIP 文件放在安装器同目录中。
安装器会自动扫描 ZIP 并继续执行安装。

## ✅ 运行环境 Requirements

Windows 10 / 11

.NET 8 运行时或自带的独立 EXE

安装有《星际争霸 II》

安装器需管理员权限（用于复制到 Program Files）

## ✅ 目录结构（生成后）

安装器会确保最终结构如下：

StarCraft II/
 ├─ Maps/
 │   └─ Starcraft Mass Recall/
 │       ├─ SCMR Campaign Launcher.SC2Map
 │       ├─ 1. Rebel Yell/
 │       ├─ 2. Overmind/
 │       ├─ 3. The Fall/
 │       ├─ Extras/
 │       │   └─ 8. Enslavers Redux/
 │       └─ （其他战役与地图包…）
 └─ Mods/
     ├─ SCMRMod.SC2Mod
     ├─ SCMRAssets.SC2Mod
     ├─ SC_MR_Local.SC2Mod
     ├─ SCMRCinematics.SC2Mod
     └─ （其他 Mod 文件…）

## ✅ 故障与回滚逻辑
1. 任何下载失败 → 自动回退到本地 ZIP 模式

保证玩家始终可以完成安装。

2. 任何解压失败 → 立即终止并提示错误

不会造成 SC2 目录损坏。

3. 任何文件复制失败 → 显示黄色提示，但不会中断整个安装

这是故意设计的：尽可能让玩家能启动游戏。

## ✅ 编译方式
dotnet build -c Release


生成的执行文件在：

bin/Release/net8.0/

## ✅ 免责声明

本安装器仅自动化 公开可获取内容的复制流程：

不包含任何 SC2 或 Mass Recall 的内容

不提供、修改或分发游戏资源

所有文件均由玩家自行下载或从官方/ForgeCDN 获取

本项目仅作为自动安装工具，不隶属于 Blizzard 或 Mass Recall 团队。

 ✅ License

MIT License
可自由使用、修改、分发。
