---
name: svn-workspace-info
description: 当用户通过 `$svn-workspace-info` 或 `/svn-workspace-info` 查看 SVN 项目工作区时，读取指定项目的配置、任务记录和当前 GUI 映射状态并简洁报告；只读，不改变代理编辑上下文。
disable-model-invocation: true
---

# SVN 工作区信息

这是一个只读的显式命令 Skill。它用于查看管理器当前保存的项目根目录、任务根目录、任务映射目录、普通/任务映射模式、活动映射任务和任务列表。

调用环境 PATH 中的 CLI。单项目或当前目录能唯一匹配时可以省略 `--project`；多项目无法唯一匹配时必须指定项目根目录：

```powershell
SvnWorkspaceManager.Cli.exe codex workspace-info --json
SvnWorkspaceManager.Cli.exe --project "D:\Work\Azure" codex workspace-info --json
```

报告项目根目录、任务根目录、任务映射目录、当前模式、活动映射任务、每个任务映射目录的实际 junction 目标和有效性、可用任务，以及是否存在未完成操作。

此 Skill 不切换任务、不建立或删除 junction、不更新 SVN、不恢复目录，也不改变 `svn-task-workspace` 已建立的代理编辑上下文。

如果 CLI 失败，原样报告关键错误，并说明没有修改任何工作区状态。
