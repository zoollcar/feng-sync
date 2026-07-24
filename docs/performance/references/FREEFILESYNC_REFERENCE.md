# FreeFileSync 14.10 参考代码摘录

## 用途与版本

本文保存 FengSync 性能重构真正需要参考的 FreeFileSync 实现片段。后续可以删除工作区
`FreeFileSync` 目录；开发不得依赖其文件存在。

来源版本：

```text
FreeFileSync 14.10
commit: 80ee767396ec0e5e9a943e182deb265bc24d7fe0
release date: 2026-06-28
```

摘录只用于说明算法边界。实际 FengSync 实现应使用 C#、现有端点抽象和本方案定义的数据
模型，不要机械翻译 FreeFileSync 的 GUI、线程库或整个 AFS 类层次。

## 1. 默认比较：时间与大小优先

来源：
`FreeFileSync/Source/base/comparison.cpp`，
`ComparisonBuffer::compareByTimeSize`。

```cpp
for (FilePair* file : uncategorizedFiles)
{
    switch (compareFileTime(file->getLastWriteTime<SelectSide::left>(),
                            file->getLastWriteTime<SelectSide::right>(),
                            fileTimeTolerance_,
                            fpConfig.ignoreTimeShiftMinutes))
    {
        case TimeResult::equal:
            if (file->getFileSize<SelectSide::left>() ==
                file->getFileSize<SelectSide::right>())
                file->setContentCategory(FileContentCategory::equal);
            else
                file->setCategoryInvalidTime(
                    getConflictSameDateDiffSize(*file));
            break;

        case TimeResult::leftNewer:
            file->setContentCategory(FileContentCategory::leftNewer);
            break;

        case TimeResult::rightNewer:
            file->setContentCategory(FileContentCategory::rightNewer);
            break;

        case TimeResult::leftInvalid:
            file->setCategoryInvalidTime(
                getConflictInvalidDate<SelectSide::left>(*file));
            break;

        case TimeResult::rightInvalid:
            file->setCategoryInvalidTime(
                getConflictInvalidDate<SelectSide::right>(*file));
            break;
    }
}
```

FengSync 应采用的要点：

- 默认扫描不读取内容；
- 时间相等还必须比较大小；
- “同时间不同大小”不是 equal；
- 内容比较是显式模式或疑难项的惰性操作。

## 2. 单次扫描缓冲贯穿比较

来源：
`FreeFileSync/Source/base/comparison.cpp`，
`ComparisonBuffer::execute`。

```cpp
folderBuffer_ = parallelFolderScan(
    foldersToRead,
    [&](const PhaseCallback::ErrorInfo& errorInfo)
    {
        return cb_.reportError(errorInfo);
    },
    onStatusUpdate,
    UI_UPDATE_INTERVAL / 2);

for (const auto& [folderPair, fpCfg] : workLoad)
    switch (fpCfg.compareVar)
    {
        case CompareVariant::timeSize:
            output.push_back(compareByTimeSize(folderPair, fpCfg));
            break;
        case CompareVariant::size:
            output.push_back(compareBySize(folderPair, fpCfg));
            break;
        case CompareVariant::content:
            output.push_back(*itOByC++);
            break;
    }
```

FengSync 应采用的要点：

- 枚举结果先形成稳定 snapshot；
- time-size、size、content 三种策略消费同一份 snapshot；
- planner、安全校验和 baseline 更新不自行扫描。

## 3. 扫描按设备分组

来源：
`FreeFileSync/Source/base/parallel_scan.cpp`，
`parallelFolderScan`。

```cpp
// aggregate folder paths that are on the same root device:
// => one worker thread *per device*: avoid excessive parallelism
std::map<AfsDevice, std::set<DirectoryKey>> perDeviceFolders;

for (const DirectoryKey& key : foldersToRead)
    perDeviceFolders[key.folderPath.afsDevice].insert(key);

for (const auto& [afsDevice, dirKeys] : perDeviceFolders)
{
    const size_t parallelOps = 1;
    std::map<DirectoryKey, DirectoryValue*> workload;

    for (const DirectoryKey& key : dirKeys)
        workload.emplace(key, &output[key]);

    worker.emplace_back(
        [afsDevice, workload, threadIdx, &acb, parallelOps] mutable
        {
            AFS::TraverserWorkload travWorkload;
            for (auto& [folderKey, folderVal] : workload)
                travWorkload.emplace_back(
                    folderKey.folderPath.afsPath,
                    std::make_shared<BaseDirCallback>(
                        folderKey, *folderVal, acb,
                        threadIdx, lastReportTime));

            AFS::traverseFolderRecursive(
                afsDevice, travWorkload, parallelOps);
        });
}
```

FengSync 应采用的要点：

- 并发预算属于设备/端点，而不是整个运行只有一个 semaphore；
- 同一机械设备保持低并发；
- 不同设备可以并行；
- remote 的 host/account 也视为资源键。

## 4. 跨端点流复制会重新读取源流属性

来源：
`FreeFileSync/Source/afs/abstract.cpp`，
`AbstractFileSystem::copyFileAsStream`。

```cpp
auto streamIn = getInputStream(sourcePath);

StreamAttributes sourceAttrNew = {};
if (std::optional<StreamAttributes> attr =
        streamIn->tryGetAttributesFast())
    sourceAttrNew = *attr;
else
    sourceAttrNew = sourceAttr;

auto streamOut = getOutputStream(
    targetPath,
    sourceAttrNew.fileSize,
    sourceAttrNew.modTime);

const uint64_t streamSize = unbufferedStreamCopy(
    [&](void* buffer, size_t bytesToRead)
    {
        return streamIn->tryRead(
            buffer, bytesToRead, notifyIoDiv);
    },
    streamIn->getBlockSize(),
    [&](const void* buffer, size_t bytesToWrite)
    {
        return streamOut->tryWrite(
            buffer, bytesToWrite, notifyIoDiv);
    },
    streamOut->getBlockSize());

if (streamSize != sourceAttrNew.fileSize)
    throw FileError(...);

const FinalizeResult finResult =
    streamOut->finalize(notifyIoDiv);
```

FengSync 应采用的要点：

- 新鲜度检查是单文件属性检查，不是重新扫描目录；
- 复制过程统计实际字节；
- 完成时验证实际流大小；
- 输出 writer 的 finalize 返回最终目标属性；
- 支持强验证时在同一 copy loop 计算源 hash。

## 5. 事务复制的抽象边界

来源：
`FreeFileSync/Source/afs/abstract.h`。

```cpp
static FileCopyResult copyFileTransactional(
    const AbstractPath& sourcePath,
    const StreamAttributes& sourceAttr,
    const AbstractPath& targetPath,
    bool copyFilePermissions,
    bool transactionalCopy,
    const std::function<void()>& onDeleteTargetFile,
    const zen::IoCallback& notifyUnbufferedIO);
```

FengSync 应采用的要点：

- 事务复制是文件系统能力，不应散落在 executor 的类型判断中；
- 复制输入包含比较时源属性；
- 复制输出包含最终属性；
- 目标覆盖/删除通过明确回调或策略协调；
- 不支持原生事务复制时使用 temporary + publish fallback。

## 6. 基线从比较树增量更新

来源：
`FreeFileSync/Source/base/db_file.cpp`，
`LastSynchronousStateUpdater`。

关键逻辑的等价摘要：

```cpp
if (item-is-in-sync)
{
    // write/update the current synchronized state
}
else
{
    // preserve the last synchronous state
}

// Items that disappeared on both sides and pass the active filter
// may be removed from the database.
// Filtered items are preserved because filtering is not deletion.
```

目录处理的原始关键片段：

```cpp
if (folder.getDirCategory() == DIR_EQUAL)
{
    const Zstring& folderName =
        folder.getItemName<SelectSide::left>();
    dbFolders.try_emplace(folderName);
    toPreserve.emplace(folderName, &folder);
}
else
{
    toPreserve.emplace(
        folder.getItemName<SelectSide::left>(), &folder);
    toPreserve.emplace(
        folder.getItemName<SelectSide::right>(), &folder);
}
```

FengSync 应采用的要点：

- 只有已确认同步的项更新为当前状态；
- 失败、冲突和未选择项保留旧状态；
- 过滤项保留旧状态；
- baseline 更新消费 comparison tree/operation result，不重新扫描。

## 7. 两侧数据库先生成临时文件再发布

来源：
`FreeFileSync/Source/base/db_file.cpp`，
`saveLastSynchronousState`。

```cpp
// 1. create *both* temporary files first
// 2. if successful, rename both files almost transactionally

massParallelExecute(
    parallelWorkloadSave,
    Zstr("Save sync.ffs_db"),
    callback);

if (saveSuccessL && saveSuccessR)
    massParallelExecute(
        parallelWorkloadMove,
        Zstr("Move sync.ffs_db"),
        callback);
```

FengSync 应采用的要点：

- 两侧候选都成功后才进入发布；
- 候选发布前必须完整性校验；
- 任一侧失败都不把新 session 作为删除权威；
- journal 记录 baseline 发布边界。

## 8. 不复制的内容

以下 FreeFileSync 内容不属于 FengSync 改进依赖：

- wxWidgets GUI；
- RealTimeSync；
- 整套 `zen` 工具库；
- FreeFileSync 配置和安装更新逻辑；
- libssh2/libcurl/Google Drive AFS 实现；
- 应用入口和品牌资源；
- C++ 线程类的具体实现。

FengSync 保持 .NET/WPF/rclone。这里只复用经过验证的设计原则和小范围算法。

