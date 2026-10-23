using SvnWorkspaceManager.Core;
using SvnWorkspaceManager.Core.Configuration;
using SvnWorkspaceManager.Core.FileSystem;
using SvnWorkspaceManager.Core.Logging;
using SvnWorkspaceManager.Core.Models;
using SvnWorkspaceManager.Core.Process;
using SvnWorkspaceManager.Core.Svn;

var applicationDirectory = Environment.GetEnvironmentVariable("SVN_WORKSPACE_MANAGER_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Azure", "SvnWorkspaceManager");
var store = new WorkspaceStore(applicationDirectory);
var logger = new WorkspaceLogger(Path.Combine(applicationDirectory, "logs"));
var configuration = store.LoadConfiguration();
var service = new WorkspaceService(store, configuration, new SvnClient(new ProcessRunner(logger), configuration.SvnExecutable), new JunctionManager());
var command = args.Length == 0 ? "status" : args[0].ToLowerInvariant();

try
{
    switch (command)
    {
        case "status":
            var snapshot = await service.GetStatusAsync();
            Console.WriteLine($"模式：{ModeName(snapshot.State.Mode)}");
            Console.WriteLine($"项目目录：{configuration.ProjectRoot}");
            Console.WriteLine($"任务目录：{configuration.TaskRoot}");
            Console.WriteLine($"隔离目录：{string.Join(", ", configuration.IsolatedDirectories)}");
            Console.WriteLine($"当前任务：{snapshot.State.ActiveTask ?? "无"}");
            Console.WriteLine($"恢复状态：{snapshot.RestoreAssessment.Summary}");
            foreach (var directory in snapshot.RestoreAssessment.Directories)
                Console.WriteLine($"{directory.RelativePath}：任务 {(directory.TaskRevision is int taskRevision ? $"r{taskRevision}" : "未知")} / 恢复目标 {(directory.TargetRevision is int targetRevision ? $"r{targetRevision}" : "未知")}；{directory.Reason}");
            if (snapshot.State.RestoreOperation is { } operation)
                Console.WriteLine($"未完成操作：{operation.Kind} / {operation.Phase} / {operation.CurrentDirectory ?? "无"}");
            Console.WriteLine($"公共工作区状态：{(snapshot.ProjectStatus.Succeeded ? snapshot.ProjectStatus.StandardOutput.Trim() : snapshot.ProjectStatus.StandardError.Trim())}");
            foreach (var item in snapshot.TaskStatuses) Console.WriteLine($"{item.Key}：{item.Value.StandardOutput.Trim()}");
            break;
        case "task" when args.Length > 1 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase):
            foreach (var task in service.ListTasks()) Console.WriteLine($"{task.Name}\t{task.RootPath}\t{task.SvnUrl}");
            break;
        case "task" when args.Length > 2 && args[1].Equals("create", StringComparison.OrdinalIgnoreCase):
            Console.WriteLine($"已创建任务：{(await service.CreateTaskAsync(args[2])).Name}");
            break;
        case "task" when args.Length > 2 && args[1].Equals("remove", StringComparison.OrdinalIgnoreCase):
            await service.RemoveTaskRecordAsync(args[2]);
            Console.WriteLine($"已移除任务记录：{args[2]}（磁盘文件保留）");
            break;
        case "task" when args.Length > 2 && args[1].Equals("delete", StringComparison.OrdinalIgnoreCase):
            await service.DeleteTaskToRecycleBinAsync(args[2]);
            Console.WriteLine($"已删除任务：{args[2]}（任务目录已移入 Windows 回收站）");
            break;
        case "mapping" when args.Length > 2 && args[1].Equals("enable", StringComparison.OrdinalIgnoreCase):
            await service.EnableTaskMappingAsync(args[2]);
            Console.WriteLine($"已进入任务映射模式：{args[2]}");
            break;
        case "mapping" when args.Length > 1 && args[1].Equals("disable", StringComparison.OrdinalIgnoreCase):
            await service.ExitTaskMappingAsync();
            Console.WriteLine("已退出任务映射模式，任务工作区已保留，未复制任务内容到原目录。");
            break;
        case "mapping" when args.Length > 1 && args[1].Equals("restore", StringComparison.OrdinalIgnoreCase):
            await service.RestoreCurrentTaskToProjectAsync();
            Console.WriteLine("已恢复当前任务到原目录并退出任务映射模式。");
            break;
        case "mapping" when args.Length > 1 && args[1].Equals("repair", StringComparison.OrdinalIgnoreCase):
            await service.ResumeInterruptedOperationAsync();
            Console.WriteLine("已继续未完成的任务映射操作。");
            break;
        case "mapping" when args.Length > 1 && args[1].Equals("repair-parent", StringComparison.OrdinalIgnoreCase):
            await service.RepairIsolatedDirectoriesAsync();
            Console.WriteLine("已修复普通模式下中断的父级隔离目录。");
            break;
        case "mapping" when args.Length > 2 && args[1].Equals("migrate", StringComparison.OrdinalIgnoreCase):
            await service.CreateTaskFromCurrentChangesAsync(args[2]);
            Console.WriteLine($"已从当前修改创建任务并进入任务映射模式：{args[2]}");
            break;
        case "switch" when args.Length > 1:
            await service.SwitchTaskAsync(args[1]);
            Console.WriteLine($"已切换任务：{args[1]}");
            break;
        case "update":
            await service.UpdateAsync();
            Console.WriteLine("更新完成。");
            break;
        default:
            Console.Error.WriteLine("用法：status | task list | task create <name> | task delete <name> | task remove <name> | mapping enable <name> | mapping migrate <name> | switch <name> | update | mapping disable | mapping restore | mapping repair | mapping repair-parent");
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
