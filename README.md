# 吹笛人内置修改器

《黎明门前的吹笛人》(The Piper of Dawn) 游戏内置修改器 —— BepInEx 6 (IL2CPP) 插件。
按 **Home** 键在游戏内呼出菜单，修改即时生效并随存档保存。**免费 · 开源 · 完全离线**，学习版 / Steam 版都支持。

![封面](scr/img/cover.jpg)

> 介绍视频（B站）：https://www.bilibili.com/video/BV1afJ46jEnp

## 功能

| 页面 | 功能 |
|---|---|
| **员工（人柴）** | **理智实时回满**：直改游戏运行进程，数值 + 状态一起还原（失智一键解除），游戏内立即生效 |
| 金钱 | 加 / 减 / 翻倍（步长 1~1000 万） |
| 背包 | 654 种物品全中文名、打字搜索、分类筛选、数量修改 / 复制 / 还原 |
| 好感 | 32 位可攻略角色好感度、势力声望（等级 / 经验 / 奖励领取） |
| 建筑 | 312 种建筑/家具一键获得（游戏内立即可见可摆放）、减少、销毁；图鉴一键全解锁 |

![员工理智](scr/img/staff.jpg)

## 下载与安装

1. **下载**：见本仓库 [Releases](../../releases)，分享版压缩包（含 .NET 运行时，离线可用）
2. **安装**：解压后把里面的全部文件（`BepInEx/`、`dotnet/`、`winhttp.dll`、`doorstop_config.ini`）复制到 `ThePiper.exe` 所在目录，提示合并时选"是"
3. **使用**：正常启动游戏，读取存档进入场景后按 **Home** 呼出菜单
4. **卸载**：删除游戏目录下的 `BepInEx/`、`dotnet/`、`winhttp.dll`、`doorstop_config.ini` 即可，游戏本体与存档不受影响

## 构建（源码）

- 方式一（.NET SDK）：`cd 源码 && dotnet build -c Debug`，需把 `PiperTrainer.csproj` 里的 `BepInExCore` / `BepInExInterop` 指向目标游戏的 `BepInEx` 目录（首次启动游戏后由 BepInEx 自动生成）
- 方式二（无 SDK，Windows + VS BuildTools）：运行 `源码\build.cmd`，用 Roslyn csc 直编并自动部署到游戏 `BepInEx\plugins\`

## 实现说明

- 游戏：Unity 2022.3.38f1 + IL2CPP（元数据 v31），加载器 BepInEx 6.0.0-be.788
- 游戏符号被混淆，且**学习版 / Steam 版混淆名不同**：插件对全部混淆成员走**运行时反射**（零编译期直连），未混淆的存档树 API（`SaveLoadSystem.*`）直接使用，因此两个版本通用
- 员工理智改动采用"运行时实体直改 + 写前强制重同步"：旧方式改存档树会被自动存档覆盖，等于无效；详细机制与修复过程见 [修改器问题分析_代码依据.md](修改器问题分析_代码依据.md)

## 目录

```
源码/                     插件完整源码（C# / .NET 6 / BepInEx 6 be.788）
  源码/*.cs               插件本体（Plugin/TrainerUI/PiperBridge/StaffLive/ManagerSweep 等）
  源码/build.cmd          无 SDK 构建脚本（Roslyn csc 直编）
修改器使用说明.md          功能与使用文档
修改器问题分析_代码依据.md  逆向分析文档（存档结构、员工理智机制、修复记录）
```

## 声明

仅供学习交流，请于下载后 24 小时内删除并支持正版。
游戏本体 (C) Bone Nail 骨钉工作室。
