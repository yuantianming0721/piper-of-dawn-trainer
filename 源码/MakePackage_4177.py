# -*- coding: utf-8 -*-
"""打包 v4.17.7 分享版：staging + zip（改编自 E:\tools\PiperTrainerDev\MakePackage.py）"""
import os, shutil, io, zipfile

GAME = r'E:\SteamLibrary\steamapps\common\The Piper Of Dawn'
PKGROOT = r'E:\AAAIDEA\吹笛人修改器\_pkg_stage'
VER = 'v4.17.7'
STAGE = os.path.join(PKGROOT, u'吹笛人内置修改器_' + VER)
ZIP = os.path.join(r'E:\AAAIDEA\吹笛人修改器', u'发布', u'吹笛人内置修改器_' + VER + u'_分享版.zip')

if os.path.exists(PKGROOT):
    shutil.rmtree(PKGROOT)
os.makedirs(STAGE)

def cp(src, dst):
    s = os.path.join(GAME, src)
    d = os.path.join(STAGE, dst)
    if os.path.isdir(s):
        shutil.copytree(s, d)
    elif os.path.isfile(s):
        shutil.copy2(s, d)
    else:
        print('MISS:', src)

cp('winhttp.dll', 'winhttp.dll')
cp('doorstop_config.ini', 'doorstop_config.ini')
cp('dotnet', 'dotnet')
cp(os.path.join('BepInEx', 'core'), os.path.join('BepInEx', 'core'))
cp(os.path.join('BepInEx', 'unity-libs'), os.path.join('BepInEx', 'unity-libs'))
cp(os.path.join('BepInEx', 'interop'), os.path.join('BepInEx', 'interop'))
cp(os.path.join('BepInEx', 'cache'), os.path.join('BepInEx', 'cache'))
os.makedirs(os.path.join(STAGE, 'BepInEx', 'patchers'), exist_ok=True)

os.makedirs(os.path.join(STAGE, 'BepInEx', 'config'), exist_ok=True)
raw = io.open(os.path.join(GAME, 'BepInEx', 'config', 'BepInEx.cfg'), 'rb').read()
txt = raw.decode('utf-8-sig')
lines = txt.split('\n')
section = ''
for i, l in enumerate(lines):
    st = l.strip()
    if st.startswith('[') and st.endswith(']'):
        section = st
    elif section == '[Logging.Console]' and st == 'Enabled = true':
        lines[i] = l.replace('Enabled = true', 'Enabled = false')
txt = '\n'.join(lines)
io.open(os.path.join(STAGE, 'BepInEx', 'config', 'BepInEx.cfg'), 'wb').write(txt.encode('utf-8'))
print('console-off applied:', '[Logging.Console]' in txt and 'Enabled = false' in txt)

for f in ('PiperTrainerNpcNames.txt', 'PiperTrainerItemNames.txt', 'local.coldbrew.piper.trainer.cfg'):
    cp(os.path.join('BepInEx', 'config', f), os.path.join('BepInEx', 'config', f))

os.makedirs(os.path.join(STAGE, 'BepInEx', 'plugins'), exist_ok=True)
shutil.copy2(os.path.join(r'E:\AAAIDEA\吹笛人修改器', u'源码', 'bin', 'Release', 'net6.0', 'PiperTrainer.dll'),
             os.path.join(STAGE, 'BepInEx', 'plugins', 'PiperTrainer.dll'))
shutil.copy2(os.path.join(r'E:\AAAIDEA\吹笛人修改器', u'修改器使用说明.md'),
             os.path.join(STAGE, u'修改器使用说明.md'))

readme = u'''黎明门前的吹笛人 (The Piper of Dawn) 内置修改器 v4.17.7 分享版
========================================================

【安装（一步）】
把本文件夹里的全部内容（winhttp.dll、doorstop_config.ini、dotnet 文件夹、BepInEx 文件夹、
修改器使用说明.md、本说明）复制到游戏根目录——即 ThePiper.exe 所在的文件夹，
提示合并/覆盖时选"是"。装完直接正常启动游戏即可，无需其他任何操作。

【使用】
1. 启动游戏，读取存档进入游戏后，按键盘 Home 键呼出修改器菜单（再按一次关闭）。
2. 五个页面：
   金钱：加 / 减 / 翻倍（步长 1 ~ 1000 万可选）
   背包：物品数量修改、搜索（右侧白色输入框打字过滤）、分类筛选
   好感：NPC 好感、势力声望
   员工：理智实时回满（直接改运行时数据，游戏内立即生效；面板显示实时值）
   建筑：全部 312 种建筑/家具列表，未拥有的点[获得]——游戏内建筑/装饰菜单立即可见可摆放；
        页顶[图鉴全部解锁]一次开满图鉴
3. 修改即时生效，并随游戏存档一起保存。

【v4.17.1 更新】
修改器更名：原"派珀黎明"并非游戏中的名字，统一改为"吹笛人内置修改器"。
【v4.17.4 更新】
· 修复"重新读档后点回满没反应"（每次读档自动重新连接员工实体，写前强制同步）
· 修复"满值却失智"——回满现在同时解除失智状态（数值+状态一起还原）
· 修复个别员工回满后回落一截（同步内部时间戳，治"懒结算补扣"）

【v4.17.7 更新】
· 种子加入背包后可以正常种植（同时写入种子存档数据）
· 金币"加/减"改为真正累加，单位修正为 ×1000（与游戏 HUD 一致）
· 好感"加/减"改为累加，并显示游戏内的好感等级（Lv.）
· 新版配置表链路适配（物品 / 角色 / 语言 / 好感）

【v4.17.6 更新】
· 建筑页恢复：新版建筑表入口与名字段已重新定位（313 条，中文名正常）
· 文本解析修复：游戏文本入口改名，物品 / 角色 / 建筑名称一并恢复
· 图鉴解锁强制段扩到 7299

【v4.17.5 更新】
· 适配游戏 2026-10-21 更新（游戏混淆类型名整体变化，修复启动与页面报 TypeLoadException）
· 员工理智改为运行时自动识别字段，回满/失智复位在新版游戏实测通过
· 回满时同步游戏帧时间戳，避免懒结算补扣

【要求】
- 《黎明门前的吹笛人》游戏本体（64 位 Windows）。
- 游戏版本不同也没关系：首次启动会自动重新生成支持库（约 1-2 分钟），全程离线，不需要联网。

【常见问题】
- 杀毒软件可能误报 winhttp.dll（游戏注入组件），加入白名单/信任即可。
- 按 Home 没反应：确认文件复制到了 ThePiper.exe 同级目录；再看游戏目录下
  BepInEx\LogOutput.log 排查。
- 想换热键：改 BepInEx\config\local.coldbrew.piper.trainer.cfg 里的 KeyCode 数值
  （278=Home，其他值参考 Unity KeyCode 表），改完重启游戏。

【卸载】
删除游戏目录下的 BepInEx 文件夹、dotnet 文件夹、winhttp.dll、doorstop_config.ini 即可。
游戏本体和存档不受任何影响。

仅供学习交流，请于下载后 24 小时内删除并支持正版。
'''
io.open(os.path.join(STAGE, u'安装说明.txt'), 'wb').write(
    b'\xef\xbb\xbf' + readme.replace('\n', '\r\n').encode('utf-8'))

total = 0; count = 0
for root, dirs, files in os.walk(STAGE):
    for f in files:
        total += os.path.getsize(os.path.join(root, f)); count += 1
print('staging 完成：%d 个文件，共 %d MB' % (count, total // 1024 // 1024))

if os.path.exists(ZIP):
    os.remove(ZIP)
with zipfile.ZipFile(ZIP, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(STAGE):
        for f in files:
            full = os.path.join(root, f)
            rel = os.path.relpath(full, STAGE)
            z.write(full, os.path.join(os.path.basename(STAGE), rel))
print('zip 完成:', ZIP, os.path.getsize(ZIP) // 1024 // 1024, 'MB')
