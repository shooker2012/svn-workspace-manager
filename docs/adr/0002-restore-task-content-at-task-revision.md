# 恢复到原目录以任务 revision 为目标

任何满足映射资格的任务都可以恢复到原目录，不要求任务 revision 与 trunk 或外层目录 revision 相等。工具读取任务隔离目录的统一 `svnversion` 作为目标 revision，移除 junction 后将父级隔离目录更新到该 revision，再同步任务内容（已跟踪新增、删除、修改、SVN 属性和未版本化文件）。

这样任务落后或领先 trunk 时都能安全放回原目录，也不会因为外层目录不是 HEAD 而把任务错误地更新到最新版本。代价是父级目录会被显式对齐到任务 revision，后续仍需要用户正常执行 `svn update`。
