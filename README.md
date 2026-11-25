# SVN 工作区管理器

这是一个 Windows 本地工具，用独立 SVN 工作区承载并行开发任务，并把当前任务的代码目录映射到原项目路径。它不创建 SVN 服务器分支，也不负责提交；提交仍由开发者使用现有 SVN 工具完成。

## 运行环境

- Windows 10/11。
- 本机可执行的 `svn.exe` 和 `svnversion.exe`。
- 开发或运行源码需要 .NET 10 SDK；也可以发布为自包含可执行文件。
- 测试额外需要 `svnadmin.exe`。

## 构建与启动

```powershell
dotnet build Tools\SvnWorkspaceManager\SvnWorkspaceManager.slnx
dotnet run --project Tools\SvnWorkspaceManager\src\SvnWorkspaceManager.Gui
```

CLI 为 `SvnWorkspaceManager.Cli.exe`，首次运行会在 `%LOCALAPPDATA%\Azure\SvnWorkspaceManager` 创建配置、状态和日志。开发和测试时可以设置 `SVN_WORKSPACE_MANAGER_HOME`，把运行数据放到指定目录。

## 首次配置

程序现在按“项目”管理配置。一个项目对应一个本地 SVN 工作副本根目录、任务根目录和任务记录；不同项目可以同时打开，同一个项目只能有一个 GUI 窗口。启动 GUI 后从项目选择器导入已有 SVN 工作副本，程序不会创建空项目。

CLI 可以显式指定项目：

```powershell
SvnWorkspaceManager.Cli.exe --project D:/W_client/Azure status
```

只读 CLI 查询可以和 GUI 同时运行；写操作会按项目串行执行。

GUI 底部“配置”区域可以填写项目根目录、任务根目录、SVN trunk URL 和任务映射目录。等价的 `config.json` 示例：

```json
{
  "version": 1,
  "projectRoot": "D:/W_client/Azure",
  "taskRoot": "D:/W_client/AzureTasks",
  "svnTrunkUrl": "svn://example/W_client/trunk/Azure",
  "svnExecutable": "svn.exe",
  "isolatedDirectories": ["Output/Lua", "Source/Azure"],
  "monitoredProcesses": []
}
```

任务映射目录必须是项目根目录内的相对路径。默认只映射 `Output/Lua` 和 `Source/Azure`，公共资源仍由外层 SVN 工作副本管理。

## 两种模式

**普通模式**：原项目的任务映射目录是普通 SVN 目录，和平时一样更新、修改、测试。

**任务映射模式**：原项目的任务映射目录是 junction，指向当前任务工作区；测试代码时仍使用原项目路径。

进入任务映射需要满足“映射资格”：任务工作区的每个任务映射目录 URL 与配置一致，工作副本 revision 与对应 trunk revision 相同，并且没有冲突、缺失、目录受阻或锁定。任务落后于 trunk 时，先在任务上执行更新。

父级任务映射目录必须完全干净才能直接进入映射。这里的“干净”包括未版本化和被 SVN 忽略的文件：建立 junction 后原目录会被排除并替换，这些内容不会再出现在原路径。工具会在报错中逐项列出已跟踪修改、未版本化或忽略内容；如果只有未版本化或忽略内容，可以使用“从当前修改进入映射”把它们一并迁移。

## 日常流程

1. **新建任务**：只从 trunk 检出独立任务工作区，不自动建立映射。
2. **从当前修改进入映射**：把普通模式下任务映射目录里的已跟踪修改、未版本化和忽略文件迁移到新任务，随后建立映射。任务映射目录之外的公共资源修改保留在原目录。
3. **切换任务**：任务映射模式下可以切换。当前任务即使有未提交修改也可以切换，任务工作区内容保持不变。
4. **更新工作区**：先更新公共父工作区，再更新活动任务的任务映射目录；任一命令失败会停止后续更新。
5. **退出任务映射**：只移除 junction，并把每个任务映射目录恢复到其直接父目录当前的 SVN revision。任务内容不会复制回原目录，任务工作区会保留。
6. **恢复到原目录**：把当前任务的代码放回原项目。工具以任务目录的统一 `svnversion` revision 为目标：移除 junction、把父级任务映射目录更新到该 revision，再同步任务的新增、删除、修改、SVN 属性和未版本化文件。任务工作区保留。
7. **删除任务**：在任务操作菜单中选择“删除任务”，任务目录会移入 Windows 回收站，任务记录同时移除；需要找回时可从回收站恢复。

退出映射和恢复到原目录是两件不同的事。前者适合“先结束映射，晚点再处理任务”；后者适合“这个任务要放回原目录继续测试或提交”。

任务列表先按状态排序，顺序为当前任务、可进入映射、任务已就绪但父目录待清理、需迁移本地文件、需处理冲突、需更新、目录不完整；同一状态内按任务名称排序。前三档都表示任务内容已经对齐，区别只在父目录：“可进入映射”可以直接进入，“先清理父目录”只需处理父目录中已跟踪的修改或冲突，“需迁移本地文件”可以一键把未版本化内容迁入任务。“需处理冲突”表示任务工作区存在 SVN 冲突、缺失、目录受阻或锁定，更新会保留冲突内容，必须先在 SVN 工具中处理后再刷新状态。

任务卡片只显示任务名和状态，悬停可查看任务目录；右侧主图标会按状态变化，分别用于进入/切换映射、直接更新到 trunk、迁移本地未版本化文件。删除图标直接放在卡片操作区，当前任务映射中会自动禁用；`⋯` 菜单保留打开目录、退出映射和移除记录等操作。

## 恢复资格

“可恢复到原目录”同时要求：

- 当前处于任务映射模式，且所有任务映射目录都指向活动任务；
- 任务目录 URL 与配置的 trunk 子目录一致；
- 任务目录的 `svnversion` 是单一 revision，且没有 switched、稀疏工作副本标记；
- 任务工作区没有冲突、缺失、目录受阻或锁定。

任务 revision 不需要等于 trunk HEAD。恢复到原目录时使用任务自己的 revision，父目录会被对齐到该版本；之后可以正常执行 `svn update`。

## 中断与修复

退出或恢复过程中会持续记录阶段、当前目录、目标 revision 和已完成目录。文件被占用、SVN 命令失败或进程中断时：

1. 关闭占用文件的程序；
2. 在 GUI 恢复卡片点击“继续…”按钮，或执行 `mapping repair`；
3. 工具会从上次阶段继续，不会只清除状态。

日志位于 `%LOCALAPPDATA%\Azure\SvnWorkspaceManager\logs\workspace-manager.log`。

如果进入映射或回滚过程中断，父级任务映射目录可能显示 `D`、`L`、`C` 状态，并且 `svn info` 显示 `Schedule: delete`。这类状态来自 `svn update --set-depth exclude` 没有完整结束，优先使用本地 pristine 恢复，不需要连接 SVN 服务器：

1. 关闭占用目录的 GUI、编辑器或编辑器子进程；
2. 在 GUI 的“当前工作区”卡片点击“修复父级工作副本”，或执行 `mapping repair-parent`；
3. 工具先 `svn cleanup`，再只对 `Schedule: delete` 的目录执行 `svn revert`，保留未版本化和忽略文件。

如果目录已经完全不存在的 `excluded` 状态，修复命令会按直接父目录的 revision 执行 `svn update --set-depth infinity`，此时需要能访问 SVN 服务器。所有网络命令都有明确超时，失败会终止子进程并保留可继续操作的状态。

GUI 执行操作时会显示当前阶段和已用时间，并每 10 秒把心跳写入日志。首次“从当前修改进入映射”需要从 trunk 检出完整的任务映射目录，网络较慢时可能持续数分钟。如果阶段长时间不变化，点击消息卡片右上角的“取消操作”；取消会终止当前 SVN 子进程或文件复制，不会把界面永久留在“执行中”。随后可点击“打开日志”，把最后一条 `GUI 操作心跳` 附近的日志发出来排查。

任务目录里的内容不会因为父目录恢复而丢失。

## CLI

```text
status
task list
task create <name>
task delete <name>
task remove <name>
mapping enable <name>
mapping migrate <name>
switch <name>
update
mapping disable
mapping restore
mapping repair
mapping repair-parent
```

### Codex 与 Cursor 集成

Codex 使用环境 PATH 中的 `SvnWorkspaceManager.Cli.exe` 获取编辑上下文，不要求 Skill 目录携带 CLI 二进制：

```powershell
SvnWorkspaceManager.Cli.exe --project D:/W_client/Azure codex context TASK002 --json
SvnWorkspaceManager.Cli.exe --project D:/W_client/Azure codex context --normal --json
SvnWorkspaceManager.Cli.exe --project D:/W_client/Azure codex workspace-info --json
```

上下文以项目根目录为工作根；配置的任务映射目录通过 `pathMappings` 指向任务工作副本或项目目录。`codex context --normal` 只恢复 Codex 编辑上下文，不切换 GUI 映射、不修改 junction 或 SVN 状态。可运行 `powershell -ExecutionPolicy Bypass -File .\install-agent-skills.ps1` 把仓库中的两个 Skill 同时安装到 Codex 的 `~/.agents/skills` 和 Cursor 的 `~/.cursor/skills`；脚本会检查 CLI 是否已在 PATH 中。

`task delete` 会把任务目录移入 Windows 回收站并移除任务记录；`task remove` 只移除记录，磁盘上的任务文件保留。

## 测试

```powershell
powershell -ExecutionPolicy Bypass -File Tools\SvnWorkspaceManager\run-tests.ps1
```

测试在工具目录的 `.test-runs` 下用 `svnadmin create` 创建真实本地 SVN 仓库，并使用真实 junction。不会读取或修改 Azure 项目。当前覆盖：

- 普通模式、任务创建和映射资格；
- 进入、切换、退出任务映射；
- 退出映射按外层目录 revision 恢复，不取 HEAD；
- 从当前修改迁移，公共资源修改保留；
- 从当前修改迁移未版本化和忽略内容；
- 恢复到原目录时的新增、删除、修改、属性和未版本化文件；
- 恢复到原目录使用任务 revision，即使 trunk 已经前进；
- 复制中断后保留状态并继续恢复；
- 文件占用导致进入映射回滚失败后，解锁并继续回滚；
- 删除任务时校验活动任务保护，并把任务目录交给 Windows 回收站处理；
- 任务列表按状态优先、名称次序排序；
- 中断排除留下 `Schedule: delete` 和忽略内容时，用本地 revert 修复；
- SVN 命令超时后会终止子进程并返回明确错误；
- `svn.exe` 不可用时返回明确的失败；
- CLI 的 status、task create/list、mapping enable/disable。

测试暂不接入 Git hook 或 GitHub Actions，按需手动运行。

## 注意

- 工具不执行 `svn commit`，也不会自动解决冲突。
- 恢复到原目录会把任务内容镜像到任务映射目录，因此任务中删除的文件也会从原目录删除；操作前请确认任务工作区。
- 映射模式下不要手工递归删除或移动原项目中的 junction；使用工具的退出/修复流程。
