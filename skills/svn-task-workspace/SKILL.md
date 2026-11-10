---
name: svn-task-workspace
description: 当用户用 `$svn-task-workspace <任务名>` 指定 Codex 当前要编辑的 SVN 项目任务时，读取指定项目中的任务实际工作目录并建立本会话的任务编辑上下文；不切换 GUI 映射。
---

# SVN 任务工作区

这是一个显式命令 Skill。它只负责让 Codex 在指定任务上下文中编辑代码，不操作项目目录的 junction，也不切换 SVN 工作区管理器的 GUI 状态。

## 命令

绑定任务：

```text
$svn-task-workspace TASK002
```

恢复项目上下文：

```text
$svn-task-workspace --normal
```

多项目环境下，如果当前目录不能唯一确定项目，使用项目根目录限定上下文：

```text
$svn-task-workspace --project D:\Work\Azure TASK002
```

## 绑定任务

调用环境 PATH 中的 CLI；项目明确时把 `--project <项目根目录>` 放在命令前：

```powershell
SvnWorkspaceManager.Cli.exe codex context "TASK002" --json
SvnWorkspaceManager.Cli.exe --project "D:\Work\Azure" codex context "TASK002" --json
```

普通上下文调用：

```powershell
SvnWorkspaceManager.Cli.exe codex context --normal --json
SvnWorkspaceManager.Cli.exe --project "D:\Work\Azure" codex context --normal --json
```

以 CLI 返回的 `workspaceRoot` 和 `pathMappings` 为唯一依据。项目根目录是工作根；访问配置的任务映射目录时使用对应映射的 `targetPath`。不要根据任务名、当前目录名称或 junction 目标自行拼接路径。

项目公共文件使用 `workspaceRoot` 下的真实路径。配置任务映射目录使用 `pathMappings` 中的 `targetPath`。任务模式下目标位于任务工作副本，普通模式下目标位于项目目录。

任务名只是 Codex 的编辑上下文，不要求 GUI 切换到该任务。当前 GUI 映射到哪个任务不影响本 Skill 的编辑路径。

如果存在多个项目且当前目录无法唯一匹配，必须要求用户提供 `--project` 对应的项目根目录；不要从 SVN URL、任务名或项目名称猜测项目。

执行 `--normal` 时完全替换之前的任务上下文，旧任务路径不再作为当前编辑路径。

## 失败处理

如果 CLI 找不到任务、任务目录不存在、任务映射目录不完整，或无法明确返回编辑路径，停止后续修改并报告错误；不要猜测路径，也不要退回项目目录继续编辑。
