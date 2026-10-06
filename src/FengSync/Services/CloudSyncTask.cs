using FengSync.Core;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// E1 / E2 的全部规则：从一个云盘端点开出一个同步任务（Profile）。纯函数，不碰 WPF、不碰 rclone，
/// 所以「云端那侧到底填了什么 URI」「默认叫什么名字」「什么时候该拒绝」都能不开窗就断言。
/// </summary>
/// <remarks>
/// <para>三条决定是这个类的全部内容，改动前先读它们：</para>
/// <para>1. <b>云端那侧是「浏览器当前所在目录」，不是端点根</b>。用户在 <c>team/docs</c> 里点「创建同步任务…」，
/// 要的是这个目录；把端点根填进去等于替他同步了整棵树。浏览的是别的端点时（选中的行与正在浏览的行不同）
/// 退化为端点根，因为那时候目录不属于这个端点，替它猜一个路径就是把 <c>team/docs</c> 挂到别的端点上去。</para>
/// <para>2. <b>强制前缀的 S3 端点只填浏览到的那一段</b>。<see cref="CloudEndpointAccount.RootPath"/> 对 alias 端点
/// 本来就是空的（alias 根**就是**前缀，重复一遍会寻址两次），所以这里不做任何特判 —— 特判一旦存在，
/// 就会有人以为 alias 端点需要把 bucket/prefix 再拼一次。</para>
/// <para>3. <b>名字必须唯一</b>。<see cref="ProfileStore.UpdateAsync"/> 遇到重名会抛，而 <see cref="ProfileStore.SaveAsync"/>
/// 是整份覆盖，所以「加一个」必须是读—加—写三步；重名的唯一处理方式是加后缀，而不是让写盘那一层去拒绝。</para>
/// </remarks>
public static class CloudSyncTask
{
    /// <summary>
    /// 一个待编辑的同步任务草稿。<see cref="LeftPath"/> 是本地目录、<see cref="RightPath"/> 是云端 URI：
    /// 云端那侧放在右边，因为右边是主窗口里「云端按钮」所在的一侧，而本地目录是空着等用户填的那一格。
    /// </summary>
    public sealed record Plan(string Name, string LeftPath, string RightPath, string Description);

    /// <summary>这次点击给出的答案：要么一份草稿，要么一句拒绝的理由。</summary>
    public sealed record Offer(Plan? Plan, string? Refusal)
    {
        public bool CanProceed => Plan is not null;
    }

    /// <summary>
    /// 端点 + 浏览器当前位置 + 已有 Profile 名字 → 草稿或拒绝。
    /// </summary>
    /// <param name="account">选中的端点。<see langword="null"/> 就是拒绝的全部理由。</param>
    /// <param name="browsedEndpoint">
    /// 浏览器正在看哪个端点的名字。为空或与 <paramref name="account"/> 不同名时，云端那侧退化为端点根。
    /// </param>
    /// <param name="currentPath">浏览器当前路径，相对该端点的 rclone 根；空串即端点根。</param>
    /// <param name="existingNames">已经存在的 Profile 名字，用来给默认名去重。</param>
    public static Offer OfferFor(CloudEndpointAccount? account, string? browsedEndpoint, string? currentPath,
        IReadOnlyCollection<string> existingNames)
    {
        if (account is null) return new Offer(null, Tr.F("Task.SelectEndpointFirst"));
        var path = BrowsedPathOf(account, browsedEndpoint, currentPath);
        var uri = CloudEndpointService.BuildUri(
            CloudEndpointService.KindFromRcloneType(account.Type), account.Name, path);
        return new Offer(Build(account, uri, leftPath: "", name: SuggestName(account, path, existingNames),
            description: DescribeSource(account, path)), null);
    }

    /// <summary>
    /// 这个端点、这个浏览位置，真正能作为云端那一侧的相对根。规则见类注释第 1 条：浏览器在别的端点时
    /// 不猜路径，落在端点根上。
    /// </summary>
    public static string BrowsedPathOf(CloudEndpointAccount account, string? browsedEndpoint, string? currentPath)
    {
        var browsing = string.Equals(browsedEndpoint, account.Name, StringComparison.OrdinalIgnoreCase);
        var path = browsing ? (currentPath ?? "").Trim() : "";
        return Join(account.RootPath, path);
    }

    /// <summary>端点根与浏览路径拼成一段相对根，两段都去掉首尾斜杠；空段不占位。</summary>
    public static string Join(string? root, string? path) => string.Join('/',
        new[] { root, path }.Select(part => (part ?? "").Trim().Trim('/')).Where(part => part.Length > 0));

    /// <summary>默认名。重名的加后缀而不是拒绝，见类注释第 3 条。</summary>
    public static string SuggestName(CloudEndpointAccount account, string? browsedPath,
        IReadOnlyCollection<string> existingNames)
    {
        var leaf = (browsedPath ?? "").Trim().Trim('/');
        var where = leaf.Length == 0 ? account.Name : $"{account.Name} · {leaf}";
        return UniqueName(Tr.Format("Task.SuggestName", where), existingNames);
    }

    /// <summary>把 <paramref name="desired"/> 改成一句不与任何已有名字重复的（大小写不敏感）。</summary>
    public static string UniqueName(string desired, IReadOnlyCollection<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(desired)) return desired;
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{desired} ({suffix})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{desired} ({Guid.NewGuid().ToString("N")[..6]})";
    }

    /// <summary>
    /// 组装草稿。名字留空时用 <paramref name="account"/> 与路径生成默认名；本地目录留空时那一格是空的，
    /// 因为替用户猜一个本地路径比让他自己点一次「浏览…」更糟。
    /// </summary>
    public static Plan Build(CloudEndpointAccount account, string cloudUri, string? leftPath, string? name = null,
        string? description = null)
    {
        var browsed = BrowsedPathOf(account, browsedEndpoint: account.Name, currentPath: PathOf(cloudUri, account));
        return new Plan(
            (name ?? "").Trim() is { Length: > 0 } explicitName ? explicitName : SuggestName(account, browsed, []),
            (leftPath ?? "").Trim(),
            cloudUri,
            description ?? DescribeSource(account, browsed));
    }

    /// <summary>状态行上那句「云端那侧到底指向哪里」——它说的是人话，不是 URI。</summary>
    public static string DescribeSource(CloudEndpointAccount account, string? browsedPath)
    {
        var leaf = (browsedPath ?? "").Trim().Trim('/');
        return leaf.Length == 0
            ? Tr.Format("Task.SourceRoot", account.Name)
            : Tr.Format("Task.SourceInside", account.Name, leaf);
    }

    /// <summary>从 <paramref name="uri"/> 里剥掉这个端点自己的根，拿到相对路径；剥不掉就是端点根。</summary>
    public static string PathOf(string? uri, CloudEndpointAccount account)
    {
        var text = (uri ?? "").Trim();
        var prefix = $"://{account.Name}";
        var at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        var rest = text[(at + prefix.Length)..].Trim().Trim('/');
        var root = (account.RootPath ?? "").Trim().Trim('/');
        // 端点根是这段相对路径的前缀时才剥掉它：剩下的是浏览到的那一段，而不是把同名的另一段也算进去。
        return root.Length > 0 && rest.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? rest[(root.Length + 1)..]
            : rest;
    }

    // ---- E2：向导那一侧的两格 ----

    /// <summary>
    /// E2 向导的两格 → 草稿或拒绝。规则只有四条，且每一条都在**建之前**说清：
    /// 少一格、云端那侧不是端点、两格相同、两格都是云端。第四条的理由是同步引擎的本职 ——
    /// 它要么把一端当本地目录读写，要么把两端当远端做 <c>operations/copyfile</c>；让用户建出一个
    /// 两端都是 URI 的「双向同步」任务，只会在第一次运行时才告诉他这件事。
    /// </summary>
    public static Offer WizardOfferFor(string? localPath, string? cloudUri, IReadOnlyCollection<string> existingNames)
    {
        var local = (localPath ?? "").Trim();
        var cloud = (cloudUri ?? "").Trim();
        if (local.Length == 0) return new Offer(null, Tr.F("Task.WizardNeedLocal"));
        if (cloud.Length == 0) return new Offer(null, Tr.F("Task.WizardNeedCloud"));
        if (!EndpointSchemes.IsRemote(cloud)) return new Offer(null, Tr.F("Task.WizardCloudMustBeEndpoint"));
        if (EndpointSchemes.IsRemote(local)) return new Offer(null, Tr.F("Task.WizardLocalMustBeFolder"));
        if (string.Equals(local, cloud, StringComparison.OrdinalIgnoreCase)) return new Offer(null, Tr.F("Task.WizardBothSidesRemote"));
        return new Offer(new Plan(SuggestWizardName(local, cloud, existingNames), local, cloud,
            $"{cloud} ↔ {local}"), null);
    }

    /// <summary>向导的默认名：本地文件夹的末级名 ↔ 端点名。同样走去重。</summary>
    public static string SuggestWizardName(string? localPath, string? cloudUri, IReadOnlyCollection<string> existingNames)
    {
        var local = (localPath ?? "").Trim().TrimEnd('\\', '/');
        var leaf = local.Length == 0 ? "" : local[(local.LastIndexOfAny(['\\', '/']) + 1)..];
        var remote = EndpointSchemes.RemoteNameOf(cloudUri) ?? (cloudUri ?? "").Trim();
        return UniqueName($"{leaf} ↔ {remote}", existingNames);
    }
}
