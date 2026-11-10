using SvnWorkspaceManager.Core;
using SvnWorkspaceManager.Core.Configuration;
using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Logging;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Process;
using SvnWorkspaceManager.Core.Svn;
using System.Text.Json;

var projectArgumentIndex = Array.FindIndex(args, argument => argument.Equals("--project", StringComparison.OrdinalIgnoreCase));
string? requestedProject = null;
var commandArgs = args;
if (projectArgumentIndex >= 0)
{
    if (projectArgumentIndex + 1 >= args.Length) throw new ArgumentException("--project 需要项目根目录。");
    requestedProject = args[projectArgumentIndex + 1];
    commandArgs = args.Where((_, index) => index != projectArgumentIndex && index != projectArgumentIndex + 1).ToArray();
}
var applicationDirectory = ProjectLocator.DefaultApplicationDirectory;
if (requestedProject is null && !File.Exists(Path.Combine(applicationDirectory, "config.json")) && new ProjectRegistry(applicationDirectory).List().Count == 0)
    throw new InvalidOperationException("尚未指定项目。请使用 --project 指定已导入的 SVN 工作副本。");
if (requestedProject is null && !File.Exists(Path.Combine(applicationDirectory, "config.json")))
{
    var current = ProjectIdentity.NormalizeRoot(Directory.GetCurrentDirectory());
    var candidates = new ProjectRegistry(applicationDirectory).List()
        .Where(item => current.Equals(item.ProjectRoot, StringComparison.OrdinalIgnoreCase)
            || current.StartsWith(item.ProjectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(item => item.ProjectRoot.Length)
        .ToList();
    if (candidates.Count == 1)
    {
        requestedProject = candidates[0].ProjectRoot;
        var context = ProjectLocator.CreateContext(requestedProject, applicationDirectory);
        applicationDirectory = context.ApplicationDirectory;
    }
    else if (candidates.Count > 1)
        throw new InvalidOperationException("当前目录匹配多个项目，请使用 --project 指定项目根目录。");
}
if (requestedProject is not null)
{
    var context = ProjectLocator.CreateContext(requestedProject, applicationDirectory);
    Directory.CreateDirectory(context.ApplicationDirectory);
    ProjectLocator.TryMigrateLegacy(applicationDirectory, context);
    var scopedConfig = Path.Combine(context.ApplicationDirectory, "config.json");
    if (!File.Exists(scopedConfig))
    {
        var legacyStore = new WorkspaceStore(applicationDirectory);
        var legacy = legacyStore.LoadConfiguration();
        if (!Path.GetFullPath(legacy.ProjectRoot).Equals(context.ProjectRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("指定项目尚未导入，且无法从旧配置自动迁移。请先在 GUI 中导入该 SVN 工作副本。");
        new WorkspaceStore(context.ApplicationDirectory).SaveConfiguration(legacy);
    }
    applicationDirectory = context.ApplicationDirectory;
}
var store = new WorkspaceStore(applicationDirectory);
var logger = new WorkspaceLogger(Path.Combine(applicationDirectory, "logs"));
var configuration = store.LoadConfiguration();
var registryRoot = requestedProject is null
    ? applicationDirectory
    : Directory.GetParent(Directory.GetParent(applicationDirectory)!.FullName)!.FullName;
var projectContext = ProjectLocator.FromConfiguration(configuration, registryRoot);
var projectRegistry = new ProjectRegistry(registryRoot);
projectRegistry.EnsureNoConflict(configuration);
projectRegistry.Register(projectContext);
var service = new WorkspaceService(store, configuration, new SvnClient(new ProcessRunner(logger), configuration.SvnExecutable), new JunctionManager());
var command = commandArgs.Length == 0 ? "status" : commandArgs[0].ToLowerInvariant();
var json = commandArgs.Any(argument => argument.Equals("--json", StringComparison.OrdinalIgnoreCase));
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Converters = { new WorkspaceModeJsonConverter() }
};

try
{
    using var operationLock = IsReadOnly(commandArgs) ? null : ProjectOperationLock.Acquire(ProjectIdentity.FromRoot(configuration.ProjectRoot), TimeSpan.FromSeconds(5));
    switch (command)
    {
        case "codex" when commandArgs.Length > 1 && commandArgs[1].Equals("context", StringComparison.OrdinalIgnoreCase):
            var context = commandArgs.Length > 2 && commandArgs[2].Equals("--normal", StringComparison.OrdinalIgnoreCase)
                ? service.GetCodexNormalContext()
                : commandArgs.Length > 2 ? service.GetCodexTaskContext(commandArgs[2])
                : throw new ArgumentException("codex context requires a task name or --normal");
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(context, jsonOptions));
            else
                PrintCodexContext(context);
            break;
        case "codex" when commandArgs.Length > 1 && commandArgs[1].Equals("workspace-info", StringComparison.OrdinalIgnoreCase):
            var workspaceInfo = service.GetCodexWorkspaceInfo();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(workspaceInfo, jsonOptions));
            else
                PrintCodexWorkspaceInfo(workspaceInfo);
            break;
        case "status":
            var snapshot = await service.GetStatusAsync();
            Console.WriteLine($"模式：{ModeName(snapshot.State.Mode)}");
            Console.WriteLine($"项目目录：{configuration.ProjectRoot}");
            Console.WriteLine($"任务目录：{configuration.TaskRoot}");
            Console.WriteLine($"任务映射目录：{string.Join(", ", configuration.IsolatedDirectories)}");
            Console.WriteLine($"当前任务：{snapshot.State.ActiveTask ?? "无"}");
            Console.WriteLine($"恢复状态：{snapshot.RestoreAssessment.Summary}");
            foreach (var directory in snapshot.RestoreAssessment.Directories)
                Console.WriteLine($"{directory.RelativePath}：任务 {(directory.TaskRevision is int taskRevision ? $"r{taskRevision}" : "未知")} / 恢复目标 {(directory.TargetRevision is int targetRevision ? $"r{targetRevision}" : "未知")}；{directory.Reason}");
            if (snapshot.State.RestoreOperation is { } operation)
                Console.WriteLine($"未完成操作：{operation.Kind} / {operation.Phase} / {operation.CurrentDirectory ?? "无"}");
            Console.WriteLine($"公共工作区状态：{(snapshot.ProjectStatus.Succeeded ? snapshot.ProjectStatus.StandardOutput.Trim() : snapshot.ProjectStatus.StandardError.Trim())}");
            foreach (var item in snapshot.TaskStatuses) Console.WriteLine($"{item.Key}：{item.Value.StandardOutput.Trim()}");
            break;
        case "task" when commandArgs.Length > 1 && commandArgs[1].Equals("list", StringComparison.OrdinalIgnoreCase):
            foreach (var task in service.ListTasks()) Console.WriteLine($"{task.Name}\t{task.RootPath}\t{task.SvnUrl}");
            break;
        case "task" when commandArgs.Length > 2 && commandArgs[1].Equals("create", StringComparison.OrdinalIgnoreCase):
            Console.WriteLine($"已创建任务：{(await service.CreateTaskAsync(commandArgs[2])).Name}");
            break;
        case "task" when commandArgs.Length > 2 && commandArgs[1].Equals("remove", StringComparison.OrdinalIgnoreCase):
            await service.RemoveTaskRecordAsync(commandArgs[2]);
            Console.WriteLine($"已移除任务记录：{commandArgs[2]}（磁盘文件保留）");
            break;
        case "task" when commandArgs.Length > 2 && commandArgs[1].Equals("delete", StringComparison.OrdinalIgnoreCase):
            await service.DeleteTaskToRecycleBinAsync(commandArgs[2]);
            Console.WriteLine($"已删除任务：{commandArgs[2]}（任务目录已移入 Windows 回收站）");
            break;
        case "mapping" when commandArgs.Length > 2 && commandArgs[1].Equals("enable", StringComparison.OrdinalIgnoreCase):
            await service.EnableTaskMappingAsync(commandArgs[2]);
            Console.WriteLine($"已进入任务映射模式：{commandArgs[2]}");
            break;
        case "mapping" when commandArgs.Length > 1 && commandArgs[1].Equals("disable", StringComparison.OrdinalIgnoreCase):
            await service.ExitTaskMappingAsync();
            Console.WriteLine("已退出任务映射模式，任务工作区已保留，未复制任务内容到原目录。");
            break;
        case "mapping" when commandArgs.Length > 1 && commandArgs[1].Equals("restore", StringComparison.OrdinalIgnoreCase):
            await service.RestoreCurrentTaskToProjectAsync();
            Console.WriteLine("已恢复当前任务到原目录并退出任务映射模式。");
            break;
        case "mapping" when commandArgs.Length > 1 && commandArgs[1].Equals("repair", StringComparison.OrdinalIgnoreCase):
            await service.ResumeInterruptedOperationAsync();
            Console.WriteLine("已继续未完成的任务映射操作。");
            break;
        case "mapping" when commandArgs.Length > 1 && commandArgs[1].Equals("repair-parent", StringComparison.OrdinalIgnoreCase):
            await service.RepairIsolatedDirectoriesAsync();
            Console.WriteLine("已修复普通模式下中断的父级任务映射目录。");
            break;
        case "mapping" when commandArgs.Length > 2 && commandArgs[1].Equals("migrate", StringComparison.OrdinalIgnoreCase):
            await service.CreateTaskFromCurrentChangesAsync(commandArgs[2]);
            Console.WriteLine($"已从当前修改创建任务并进入任务映射模式：{commandArgs[2]}");
            break;
        case "switch" when commandArgs.Length > 1:
            await service.SwitchTaskAsync(commandArgs[1]);
            Console.WriteLine($"已切换任务：{commandArgs[1]}");
            break;
        case "update":
            await service.UpdateAsync();
            Console.WriteLine("更新完成。");
            break;
        default:
            Console.Error.WriteLine("用法：status | task list | task create <name> | task delete <name> | task remove <name> | mapping enable <name> | mapping migrate <name> | switch <name> | update | mapping disable | mapping restore | mapping repair | mapping repair-parent | codex context <name> [--json] | codex context --normal [--json] | codex workspace-info [--json]");
            return 2;
    }
}
catch (Exception exception)
{
    logger.Error(exception.ToString());
    Console.Error.WriteLine(exception.Message);
    return 1;
}

logger.Info($"CLI 命令完成：{command}");
return 0;

static string ModeName(WorkspaceMode mode) => mode == WorkspaceMode.TaskMapping ? "任务映射模式" : "普通模式";

static bool IsReadOnly(string[] commandArgs)
{
    if (commandArgs.Length == 0) return true;
    if (commandArgs[0].Equals("status", StringComparison.OrdinalIgnoreCase)) return true;
    return commandArgs[0].Equals("task", StringComparison.OrdinalIgnoreCase)
        && commandArgs.Length > 1
        && commandArgs[1].Equals("list", StringComparison.OrdinalIgnoreCase)
        || commandArgs[0].Equals("codex", StringComparison.OrdinalIgnoreCase);
}


static void PrintCodexContext(CodexTaskContext context)
{
    Console.WriteLine($"模式：{context.Mode}");
    Console.WriteLine($"任务：{context.TaskName ?? "无"}");
    Console.WriteLine($"工作区根目录：{context.WorkspaceRoot}");
    Console.WriteLine($"任务工作区：{context.TaskRoot ?? "无"}");
    Console.WriteLine($"可编辑：{(context.CanEdit ? "是" : "否")}");
    foreach (var mapping in context.PathMappings)
        Console.WriteLine($"{mapping.RelativePath}：{mapping.TargetPath}（{(mapping.Exists ? "存在" : "缺失")}）");
}

static void PrintCodexWorkspaceInfo(CodexWorkspaceInfo info)
{
    Console.WriteLine($"项目根目录：{info.ProjectRoot}");
    Console.WriteLine($"任务根目录：{info.TaskRoot}");
    Console.WriteLine($"模式：{ModeName(info.Mode)}");
    Console.WriteLine($"当前映射任务：{info.ActiveTask ?? "无"}");
    Console.WriteLine($"未完成操作：{(info.HasIncompleteOperation ? info.Operation!.Kind + " / " + info.Operation.Phase : "无")}");
    Console.WriteLine($"任务：{(info.Tasks.Count == 0 ? "无" : string.Join("、", info.Tasks.Select(task => task.Name)))}");
    foreach (var mapping in info.Mappings)
        Console.WriteLine($"{mapping.RelativePath}：{(mapping.IsJunction ? mapping.TargetPath ?? "链接目标未知" : "普通目录")}（{(mapping.IsValid ? "有效" : "未映射或异常")}）");
}
