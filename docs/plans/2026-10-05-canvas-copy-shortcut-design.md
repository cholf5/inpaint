# 画布复制快捷键与右键菜单评估（2026-10-05）

## 背景与目标

复制图片的入口目前只有历史节点右键菜单（`CopyNodeCommand`，参数为任意节点）。两件事：

1. 给当前画布补 ⌘/Ctrl+C 快捷键（实施）。
2. 评估是否值得再给画布加右键菜单（Copy / Undo / Redo / …）（只评估，未实施）。

## ⌘/Ctrl+C（已实施）

- **走 `MainWindow.OnKeyDown` 的 `Control||Meta` 双认分支，与 ⌘V 粘贴并列**：复制/粘贴是成对操作；同一分支天然双认两修饰键，不必像 KeyBinding 方案（Ctrl+O/S/Z 那条路）那样再为 macOS 补一条 Meta 绑定。
- **复用 `CopyNodeCommand` 传 `vm.CurrentNode`**，不新增命令：CanExecute（`CanOperateNode` = 非繁忙 + 节点非空 + 有剪贴板）原样生效；手动触发先过 CanExecute（命令绑定才检查，`AsyncRelayCommand.Execute` 不检查）。
- 设置页「快捷键」新增一行：Translations `ShortcutCopy`（zh + en 双词典，覆盖测试把关）、`SettingsViewModel.CopyKeycap`（平台键帽 ⌘/Ctrl）。
- 测试 `MainWindowTests.CtrlC按键_复制当前画布图片到剪贴板_繁忙时禁用`：headless 按键（Meta 修饰，覆盖 macOS 路径）→ 命令执行 → headless 剪贴板读回 6×4 位图；置 `IsBusy` 后命令禁用、再按键剪贴板保持为空。

## 画布右键菜单（评估结论：暂不做）

现状：右键在画布上完全空闲（`ImageEditorControl.OnPointerPressed` 只处理中键平移与左键涂抹），挂 ContextMenu 无交互冲突——加是能加的，但逐项看不值得：

- **Copy**：与 ⌘C 是同一命令，菜单只多一个「发现性」入口，而快捷键页已列出 ⌘C。
- **Undo**：已有工具栏按钮 + ⌘Z，菜单再放一份是三重复制面。
- **Redo**：没有现成语义。历史是 git 式分叉树（无 redo 指针），「重做」= 在历史面板点击子节点（`SelectNode`）；给菜单提供 Redo 得先发明「前进到哪个子节点」的前进栈，是新功能而非菜单包装，且与「历史面板就是重做界面」的既有设计冲突。
- 成本小但非零：双词典词条、菜单数据流（照 `HistoryGraphView` 模式在 `ContextRequested` 时重设 Command/CommandParameter）、交互守卫（涂抹/平移进行中、繁忙、空画布须抑制弹出）、测试与 AGENTS.md 同步；此后画布每个交互特性都要多考虑一层菜单。

**YAGNI，暂不加。** 若后续用户反馈「找不到复制」，最小可行版本是两项菜单：复制图片 / 保存…（复用 `CopyNodeCommand` / `SaveNodeCommand`，参数 `CurrentNode`；`ContextRequested` 时接线 + `_stroking||_panning` 时抑制），仍不建议放 Undo/Redo。
