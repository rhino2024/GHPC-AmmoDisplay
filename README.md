# 战雷式弹药栏

Gunner, HEAT, PC! 的 MelonLoader 模组。屏幕最下面一条弹药栏，一种弹一格，格子上是图标和全车剩余发数。

## 你会看到什么

主炮能用的每种弹占一格，图标右下角是数字。数字是**全车这一类弹的总数**：弹架里存着的，加上已经在供弹机弹链里的，再加上膛里那一发，不是只数待发架。打空了的那一种不会消失，只是图标变暗，跟战雷一样。

当前选中的弹种会描一圈白框。装填或换弹的时候，整排底下有一条进度条。

机炮车也一样：双供弹的机炮两种弹各占一格，弹链里的几百发都算进去，不会显示 0。机炮的 APDS-T 用的是三发弹链图，跟主炮的单发穿甲弹一眼能分开。带导弹的车，反坦克导弹也放在这一排最前面，数字是发射架上还剩几发。

没有标题，没有底板，数字是白字描黑边，压在图标上不挡后面的画面。

`F3` 开关。

## 装

1. 先彻底退出游戏。游戏开着的时候 dll 是锁住的，覆盖不了。
2. 把 zip 解压到游戏根目录，就是放着 `GHPC.exe` 的那一层。解压出来应该看到 `Bin\Mods\AmmoDisplay.dll` 和 `Bin\Mods\AmmoDisplayIcons\`。
3. 开一次游戏。MelonLoader 会在 `Bin\UserData\MelonPreferences.cfg` 里写出 `[AmmoDisplay]` 这一段。

需要 MelonLoader v0.7.2（net35 运行时），下载包里没有它。它不改游戏任何文件，删掉 dll 和 `AmmoDisplayIcons` 文件夹就算卸载。

## 图标

包里自带六个图标（穿甲弹、脱壳穿甲弹、机炮弹链、破甲弹、榴弹、导弹），是随模组一起画的原创图形，不含游戏或任何第三方素材，用 `icons/generate-icons.ps1` 生成。想换成自己的：把同名 PNG 放进 `Bin\UserData\AmmoIcons`，会覆盖包里那几张。

## 能改的东西

| 名字 | 默认 | 作用 |
| --- | --- | --- |
| Enabled | true | 总开关 |
| ToggleKey | F3 | 任务中开关弹药栏的按键 |
| CellSize | 96 | 每个格子的边长（像素），上面那排机枪格子取它的 0.8 倍 |
| ShowMachineGuns | false | 额外显示同轴／车顶机枪的弹量，多一排会挡更多画面 |
| Anchor | BottomCenter | 弹药栏位置：BottomCenter、BottomLeft、BottomRight |
| MarginBottom | 8 | 离屏幕底边的像素数，填小就是贴在底边 |
| IconDirectory | UserData/AmmoIcons | 自己的图标文件夹，同名文件覆盖自带图标 |
| DebugLog | false | 出问题再开，会写 `Bin/Mods/AmmoDisplay.debug.log` |

## 先说清楚

它只显示数字，不改变任何弹药行为：不自动选弹，也不会让你多带弹。

数字的来源是游戏自己的弹架和供弹机队列，所以刚开局、或者游戏还没把弹装进供弹机的那一帧，可能短暂显示得比预期少，装好后会自己回到正确数字。进 AAR 回放时不显示。

认不出名字的弹种会画一块按类别着色的方块，上面写弹种短名，不会空着。

## 想自己编译

不用 .NET SDK，也不用 Unity 编辑器，一个 dll 而已，用任何 Roslyn `csc` 对着游戏自带的托管程序集编就行：

```bash
GHPC_DIR="/path/to/Gunner HEAT PC/Bin" bash src/build.sh deploy
```

`deploy` 会把编出来的 dll 覆盖到 `<游戏目录>\Bin\Mods\`，所以先关游戏。不加 `deploy` 就只编到 `src/bin/`。

## In English

A War Thunder style ammo bar along the bottom of the screen: one cell per shell type, the icon plus the vehicle-wide remaining count (racks, feed belts and the chambered round), empty types dimmed, the selected type framed, and a reload bar underneath. Autocannon belts are counted from the feed queues, guided missiles get their own cell, and the bundled icons are original vector-style artwork that ships with the mod. `F3` toggles it; cell size, position and icon folder are configurable.

MIT，与 Radian Simulations 无关。在当前 Steam 版上实测过，游戏更新有可能让它失效。
