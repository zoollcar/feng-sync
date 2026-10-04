using System.IO;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Covers the cloud-drive features added after the gap analysis in
/// <c>docs/cloud-drive-feature-plan.md</c>: the browse/navigate rules, the file-operation safety rules,
/// the endpoint health classification, and the transfer queue's new pause/concurrency/retry state machine.
/// Every one of these is a decision that is easy to get subtly wrong and impossible to notice from the UI,
/// so they are pinned here rather than only exercised through the window.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudFeatureBehaviorTests : IDisposable
{
    // ---------------------------------------------------------------- browse: address normalisation

    [Fact]
    public void A_typed_address_cannot_escape_the_endpoint_root()
    {
        // ".." at the root is clamped rather than rejected: the endpoint root is a boundary the user
        // cannot cross anyway, and refusing the input would only be a worse way to say the same thing.
        Assert.Equal("team", CloudBrowse.Normalize("../..", "team"));
        // Walking above the root lands back on the root, not on whatever is above it.
        Assert.Equal("team", CloudBrowse.Normalize("team/docs/../../..", "team"));
        // With no configured root the floor is the remote root itself.
        Assert.Equal("", CloudBrowse.Normalize("team/../..", null));
        Assert.Equal("shared", CloudBrowse.Normalize("team/../../shared", null));
    }

    [Fact]
    public void A_typed_address_is_read_relative_to_the_endpoint_root_but_an_absolute_one_is_kept()
    {
        Assert.Equal("team/docs", CloudBrowse.Normalize("docs", "team"));
        Assert.Equal("team/docs", CloudBrowse.Normalize("team/docs", "team"));
        Assert.Equal("team", CloudBrowse.Normalize("", "team"));
    }

    [Fact]
    public void A_pasted_windows_path_and_a_dot_segment_both_normalise()
    {
        Assert.Equal("team/docs", CloudBrowse.Normalize(@"team\docs", null));
        Assert.Equal("team/docs", CloudBrowse.Normalize("./team/./docs/", null));
    }

    [Fact]
    public void The_breadcrumb_trail_always_starts_at_the_endpoint_root()
    {
        var trail = CloudBrowse.Breadcrumbs("team", "team/docs/reports", "归档");

        Assert.Equal(["归档", "docs", "reports"], trail.Select(crumb => crumb.Name));
        // The first crumb is the endpoint's own boundary, which is the one place the user cannot go above.
        Assert.Equal("team", trail[0].Path);
        Assert.Equal("team/docs", trail[1].Path);
        Assert.Equal("team/docs/reports", trail[2].Path);
    }

    [Fact]
    public void A_breadcrumb_trail_for_an_endpoint_with_no_root_is_just_the_endpoint()
    {
        var trail = CloudBrowse.Breadcrumbs(null, "", "我的网盘");

        Assert.Single(trail);
        Assert.Equal("我的网盘", trail[0].Name);
    }

    // ---------------------------------------------------------------- browse: ordering and filtering

    [Fact]
    public void Directories_stay_ahead_of_files_in_both_sort_directions()
    {
        var entries = new[] { FileEntry("a.txt", 10), FolderEntry("zeta"), FileEntry("b.txt", 20), FolderEntry("alpha") };

        var ascending = CloudBrowse.Sort(entries, CloudSortColumn.Name, descending: false);
        var descending = CloudBrowse.Sort(entries, CloudSortColumn.Name, descending: true);

        // Sorting a folder by size — always zero — would bury every folder under every file, so the
        // directory/file split is applied before the direction and the reversal only reorders within each.
        Assert.Equal(["alpha", "zeta", "a.txt", "b.txt"], ascending.Select(entry => entry.Name));
        Assert.Equal(["zeta", "alpha", "b.txt", "a.txt"], descending.Select(entry => entry.Name));
    }

    [Fact]
    public void Sorting_by_size_falls_back_to_the_name_for_equal_sizes()
    {
        var entries = new[] { FileEntry("b.txt", 10), FileEntry("a.txt", 10), FileEntry("c.txt", 30) };

        var ordered = CloudBrowse.Sort(entries, CloudSortColumn.Size, descending: false);

        Assert.Equal(["a.txt", "b.txt", "c.txt"], ordered.Select(entry => entry.Name));
    }

    [Fact]
    public void A_missing_timestamp_sorts_last_instead_of_pretending_to_be_1970()
    {
        var entries = new[]
        {
            FileEntry("unknown.txt", 1, modified: null),
            FileEntry("newer.txt", 1, modified: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            FileEntry("older.txt", 1, modified: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
        };

        var ordered = CloudBrowse.Sort(entries, CloudSortColumn.Modified, descending: false);

        Assert.Equal(["older.txt", "newer.txt", "unknown.txt"], ordered.Select(entry => entry.Name));
    }

    [Fact]
    public void Clearing_the_filter_box_restores_the_listing_instead_of_emptying_it()
    {
        var entries = new[] { FileEntry("report.pdf", 1), FileEntry("notes.txt", 1) };

        Assert.Equal(2, CloudBrowse.Filter(entries, "   ").Count);
        Assert.Single(CloudBrowse.Filter(entries, "REPORT"));
        Assert.Empty(CloudBrowse.Filter(entries, "missing"));
    }

    [Fact]
    public void A_search_matches_the_folder_part_of_a_path_and_not_only_the_file_name()
    {
        var nested = FileEntry("summary.txt", 1, path: "archive/2026/reports/summary.txt");

        // A user looking for "reports" under a large tree means the folder too.
        Assert.True(CloudBrowse.MatchesQuery(nested, "reports"));
        Assert.True(CloudBrowse.MatchesQuery(nested, "summary"));
        Assert.False(CloudBrowse.MatchesQuery(nested, "budget"));
    }

    [Fact]
    public void A_search_result_shows_its_full_path_while_a_plain_listing_shows_the_name()
    {
        var entry = FileEntry("summary.txt", 1, path: "archive/summary.txt");

        Assert.Equal("summary.txt", entry.NameDisplay);
        Assert.Equal("archive/summary.txt", (entry with { ShowFullPath = true }).NameDisplay);
    }

    // ---------------------------------------------------------------- file names

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a\tb")]
    public void A_name_that_rclone_would_read_as_a_path_is_rejected(string name)
    {
        // rclone treats a separator inside a name as a path, so an unvalidated name would silently create
        // the file somewhere else than the user pointed at.
        Assert.False(CloudFileNames.IsValid(name));
        Assert.NotNull(CloudFileNames.Validate(name));
    }

    [Fact]
    public void A_name_longer_than_the_common_backend_limit_is_rejected()
    {
        Assert.True(CloudFileNames.IsValid(new string('a', CloudFileNames.MaxLength)));
        Assert.False(CloudFileNames.IsValid(new string('a', CloudFileNames.MaxLength + 1)));
    }

    [Fact]
    public void An_accepted_name_reports_no_reason()
    {
        Assert.Null(CloudFileNames.Validate("季度报告 2026.pdf"));
        Assert.True(CloudFileNames.IsValid("季度报告 2026.pdf"));
    }

    [Fact]
    public void A_rename_stays_in_the_directory_the_entry_came_from()
    {
        // A rename is not a move, so the destination must be the original directory under the new name.
        Assert.Equal("archive/report.txt", CloudFileNames.RenameTarget("archive/old.txt", "report.txt"));
        Assert.Equal("report.txt", CloudFileNames.RenameTarget("old.txt", "report.txt"));
    }

    [Fact]
    public void Keeping_both_names_the_incoming_file_beside_the_existing_one()
    {
        Assert.Equal("report (1).pdf", CloudFileNames.KeepBothName("report.pdf", []));
        Assert.Equal("report (2).pdf", CloudFileNames.KeepBothName("report.pdf", ["report.pdf", "report (1).pdf"]));
        // The suffix goes between the stem and the extension: a file whose extension moved would no longer be
        // the kind of file the user uploaded.
        Assert.Equal("archive.tar (1).gz", CloudFileNames.KeepBothName("archive.tar.gz", []));
    }

    [Fact]
    public void A_name_taken_in_any_case_is_not_offered_back_as_a_free_one()
    {
        // Most backends are case-insensitive, so an existing "A (1).TXT" means "a (1).txt" is not free.
        Assert.Equal("a (2).txt", CloudFileNames.KeepBothName("a.txt", ["A (1).TXT"]));
    }

    [Fact]
    public void A_rename_never_produces_a_name_longer_than_the_backend_accepts()
    {
        var longStem = new string('a', CloudFileNames.MaxLength);

        var renamed = CloudFileNames.KeepBothName($"{longStem}.txt", [$"{longStem}.txt"]);

        // A rename that produced a name the backend rejects would fail the very upload the user chose
        // 保留两者 to complete, so the suffix costs stem, not the limit.
        Assert.Equal(CloudFileNames.MaxLength, renamed.Length);
        Assert.EndsWith(" (1).txt", renamed, StringComparison.Ordinal);
    }

    [Fact]
    public void Counting_up_to_a_free_name_stops_rather_than_running_forever()
    {
        // Every candidate from (1) to (9999) is already taken, so no further counting frees a name; the last
        // one is returned and the batch's own answer decides, instead of looping on a name that never comes.
        var taken = Enumerable.Range(1, 9999).Select(index => $"x ({index}).txt").ToArray();

        Assert.Equal("x (9999).txt", CloudFileNames.KeepBothName("x.txt", taken));
    }

    // ---------------------------------------------------------------- delete safety

    [Fact]
    public void Only_a_backend_with_a_trash_is_presented_as_recoverable()
    {
        Assert.True(CloudDeleteSafety.SupportsTrash(EndpointType.GoogleDrive));
        Assert.False(CloudDeleteSafety.SupportsTrash(EndpointType.Sftp));
        Assert.False(CloudDeleteSafety.SupportsTrash(EndpointType.S3));
    }

    [Fact]
    public void An_irreversible_delete_says_so_and_a_recoverable_one_does_not()
    {
        var entries = new[] { FileEntry("a.txt", 1) };

        var irreversible = CloudDeleteSafety.Describe(entries, supportsTrash: false);
        var recoverable = CloudDeleteSafety.Describe(entries, supportsTrash: true);

        Assert.True(irreversible.IsIrreversible);
        Assert.Contains("无法恢复", irreversible.Message, StringComparison.Ordinal);
        Assert.False(recoverable.IsIrreversible);
        Assert.Contains("回收站", recoverable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_delete_warns_that_its_contents_go_with_it()
    {
        var confirmation = CloudDeleteSafety.Describe([FolderEntry("docs")], supportsTrash: false);

        // The count is of the things the user pointed at, not of everything that will disappear, so saying
        // so is the difference between an informed confirmation and a misleading one.
        Assert.Contains("连同其中的全部内容", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("文件夹 1 个", confirmation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_batch_is_flagged_for_a_second_acknowledgement()
    {
        var atThreshold = Enumerable.Range(0, CloudDeleteSafety.BulkThreshold).Select(index => FileEntry($"f{index}.txt", 1)).ToArray();
        var aboveThreshold = Enumerable.Range(0, CloudDeleteSafety.BulkThreshold + 1).Select(index => FileEntry($"f{index}.txt", 1)).ToArray();

        Assert.False(CloudDeleteSafety.Describe(atThreshold, supportsTrash: false).IsBulk);
        Assert.True(CloudDeleteSafety.Describe(aboveThreshold, supportsTrash: false).IsBulk);
        // A long list is previewed and the remainder summarised rather than printed in full.
        Assert.Contains("以及另外 3 项", CloudDeleteSafety.Describe(aboveThreshold, supportsTrash: false).Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- conflict answers

    [Theory]
    [InlineData(CloudUploadConflictPolicy.Overwrite, CloudConflictChoice.Overwrite)]
    [InlineData(CloudUploadConflictPolicy.Skip, CloudConflictChoice.Skip)]
    [InlineData(CloudUploadConflictPolicy.KeepBoth, CloudConflictChoice.KeepBoth)]
    public void A_saved_policy_answers_the_conflict_for_the_whole_batch(CloudUploadConflictPolicy policy, CloudConflictChoice choice)
    {
        Assert.Equal(choice, CloudConflictResolver.ChoiceFor(policy));
    }

    [Fact]
    public void The_ask_policy_hands_the_question_to_the_user_rather_than_answering_it_with_overwrite()
    {
        // A setting that reads 每次询问 must not be implemented as a default answer. Null is the only thing
        // that tells the caller to ask; anything else would replace a file nobody agreed to replace.
        Assert.Null(CloudConflictResolver.ChoiceFor(CloudUploadConflictPolicy.Ask));
    }

    [Fact]
    public void Asking_about_a_batch_stops_once_an_answer_covers_the_rest()
    {
        var asked = new List<string>();
        var choices = CloudConflictResolver.AskFor(["dir/a.txt", "dir/b.txt", "dir/c.txt"], (path, _) =>
        {
            asked.Add(path);
            return path == "dir/a.txt"
                ? new CloudConflictAnswer(CloudConflictChoice.KeepBoth, ApplyToAll: true)
                : new CloudConflictAnswer(CloudConflictChoice.Overwrite, ApplyToAll: false);
        });

        // One question answered 应用到全部 decides the whole batch, which is the promise that keeps a dropped
        // tree from asking once per file.
        Assert.Equal(["dir/a.txt"], asked);
        Assert.Equal(CloudConflictChoice.KeepBoth, choices!["dir/b.txt"]);
        Assert.Equal(CloudConflictChoice.KeepBoth, choices["dir/c.txt"]);
    }

    [Fact]
    public void An_answer_that_is_not_applied_leaves_the_other_names_to_be_asked_and_counts_them()
    {
        var remaining = new List<int>();
        var choices = CloudConflictResolver.AskFor(["dir/a.txt", "dir/b.txt"], (path, left) =>
        {
            remaining.Add(left);
            return new CloudConflictAnswer(path == "dir/a.txt" ? CloudConflictChoice.Skip : CloudConflictChoice.Overwrite,
                ApplyToAll: false);
        });

        // The count is what makes the offer honest: it names the files still undecided, including this one.
        Assert.Equal([2, 1], remaining);
        Assert.Equal(CloudConflictChoice.Skip, choices!["dir/a.txt"]);
        Assert.Equal(CloudConflictChoice.Overwrite, choices["dir/b.txt"]);
    }

    [Fact]
    public void Cancelling_the_question_is_not_the_same_as_answering_skip_for_everything()
    {
        var choices = CloudConflictResolver.AskFor(["dir/a.txt", "dir/b.txt"],
            (path, _) => path == "dir/a.txt"
                ? null
                : new CloudConflictAnswer(CloudConflictChoice.Overwrite, ApplyToAll: false));

        // A null result is the only signal a caller has that nothing may be queued. An empty dictionary would
        // read as "every name was decided" and the batch would start with the rows nobody answered for.
        Assert.Null(choices);
    }

    [Fact]
    public void A_skipped_conflict_keeps_its_row_in_the_answer_so_the_batch_stays_aligned()
    {
        var placements = CloudConflictResolver.Apply(["dir/a.txt", "dir/b.txt"],
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            path => path == "dir/a.txt" ? CloudConflictChoice.Skip : CloudConflictChoice.Overwrite);

        // One placement per destination, in the order given: a caller pairs its own rows with the answers by
        // position, so an omitted skip would shift every later file onto the wrong answer.
        Assert.Equal(2, placements.Count);
        Assert.Equal(("dir/a.txt", "dir/a.txt", CloudConflictChoice.Skip),
            (placements[0].Original, placements[0].Destination, placements[0].Choice));
        Assert.Equal(CloudConflictChoice.Overwrite, placements[1].Choice);
    }

    [Fact]
    public void A_keep_both_rename_replaces_only_the_name_and_never_the_directory()
    {
        var placements = CloudConflictResolver.Apply(["shared/incoming/a.txt"],
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt" },
            _ => CloudConflictChoice.KeepBoth);

        var placement = Assert.Single(placements);
        Assert.Equal("shared/incoming/a (1).txt", placement.Destination);
    }

    [Fact]
    public void Two_files_dropped_onto_one_remote_name_are_renamed_apart_rather_than_onto_each_other()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt" };

        var placements = CloudConflictResolver.Apply(["dir/a.txt", "dir/a.txt"], _ => taken,
            _ => CloudConflictChoice.KeepBoth);

        // Two dropped folders can each hold a file of the same name, and that pair addresses one remote path.
        // Looking the answer up by name would hand both rows the same rename, and the second copy would
        // replace the first; pairing by position, with a name this batch already used counted as taken, is
        // what keeps both files.
        Assert.Equal(["dir/a (1).txt", "dir/a (2).txt"], placements.Select(placement => placement.Destination));
    }

    [Fact]
    public void A_rename_is_only_free_relative_to_the_directory_it_lands_in()
    {
        var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["docs"] = ["a.txt", "a (1).txt"],
            ["img"] = ["a.txt"]
        };

        var placements = CloudConflictResolver.Apply(["docs/a.txt", "img/a.txt"], directory => names[directory],
            _ => CloudConflictChoice.KeepBoth);

        Assert.Equal(["docs/a (2).txt", "img/a (1).txt"], placements.Select(placement => placement.Destination));
    }

    // ---------------------------------------------------------------- details (属性)

    [Fact]
    public void A_folder_is_never_reported_as_zero_bytes()
    {
        // A listing carries no size for a directory, and the object really might hold something: 0 字节 is a
        // number the user reads as "this folder is empty".
        var rows = CloudItemDetails.For(FolderEntry("docs"), Account("files", "shared"),
            BackendStat(size: 0, directory: true, mime: "inode/directory"));

        Assert.Equal("未统计", DetailValue(rows, "大小"));
        Assert.DoesNotContain("0 字节", DetailValue(rows, "大小"));
    }

    [Fact]
    public void A_file_shows_the_exact_byte_count_beside_the_rounded_size()
    {
        var rows = CloudItemDetails.For(FileEntry("a.txt", 2_097_152), Account("files", "shared"),
            BackendStat(size: 2_097_152));

        Assert.Equal("2.0 MB（2,097,152 字节）", DetailValue(rows, "大小"));
    }

    [Fact]
    public void The_backend_s_answer_wins_over_the_row_that_happens_to_be_on_screen()
    {
        // The listing is a snapshot; 属性 is asked about the object now. A panel that reprinted the row would
        // tell the user the file it just read is the size it had when the directory was opened.
        var rows = CloudItemDetails.For(FileEntry("a.txt", 1024), Account("files", "shared"),
            BackendStat(size: 4096));

        Assert.Equal("4.0 KB（4,096 字节）", DetailValue(rows, "大小"));
        // A MIME type is not something a listing reports at all, so this row exists only because the stat did.
        Assert.Equal("text/plain", DetailValue(rows, "MIME 类型"));
    }

    [Fact]
    public void An_object_the_backend_no_longer_has_is_said_to_be_gone()
    {
        var rows = CloudItemDetails.For(FileEntry("a.txt", 1024), Account("files", "shared"), CloudItemStat.Missing);

        Assert.Contains("云端已没有", DetailValue(rows, "状态"));
        Assert.DoesNotContain(rows, row => row.Label == "大小");
        // Where it was is still worth knowing: the path is what the user would check with rclone directly.
        Assert.Equal("files:a.txt", DetailValue(rows, "路径"));
    }

    [Fact]
    public void A_checksum_is_only_offered_for_a_file_the_backend_still_has()
    {
        var file = FileEntry("a.txt", 1024);

        Assert.True(CloudItemDetails.OffersHash(file, BackendStat()));
        Assert.False(CloudItemDetails.OffersHash(file, BackendStat(hashes: new Dictionary<string, string> { ["md5"] = "ab" })));
        Assert.False(CloudItemDetails.OffersHash(FolderEntry("docs"), BackendStat(directory: true)));
        Assert.False(CloudItemDetails.OffersHash(file, CloudItemStat.Missing));
        // Nothing was read yet, so nothing can be promised about what the backend would answer.
        Assert.False(CloudItemDetails.OffersHash(file, stat: null));
    }

    [Theory]
    [InlineData(false, "未计算")]
    [InlineData(true, "此后端未提供校验值")]
    public void An_absent_checksum_says_who_left_it_out(bool asked, string expected)
    {
        // "Not computed" is about this dialog and "this backend has none" is about the endpoint; the user has
        // to be able to tell whether asking again could help.
        var rows = CloudItemDetails.For(FileEntry("a.txt", 1024), Account("files", "shared"),
            BackendStat(), hashRequested: asked);

        Assert.Equal(expected, DetailValue(rows, "校验值"));
    }

    [Fact]
    public void A_folder_does_not_get_asked_for_a_checksum_it_cannot_have()
    {
        var rows = CloudItemDetails.For(FolderEntry("docs"), Account("files", "shared"),
            BackendStat(directory: true, mime: "inode/directory"), hashRequested: true);

        Assert.Equal("文件夹没有校验值", DetailValue(rows, "校验值"));
    }

    [Fact]
    public void Every_checksum_the_backend_gives_is_named_by_the_type_it_is()
    {
        // An unlabelled hex string invites the user to compare it against a file's SHA-256 and conclude the
        // copy is broken. The order is the application's preference, shared with the sync engine's fingerprint.
        var rows = CloudItemDetails.For(FileEntry("a.txt", 1024), Account("files", "shared"),
            BackendStat(hashes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sha1"] = "aa",
                ["md5"] = "bb",
            }));

        var checksums = rows.Where(row => row.Label.StartsWith("校验值", StringComparison.Ordinal)).ToList();

        Assert.Equal(["校验值（MD5）", "校验值（SHA-1）"], checksums.Select(row => row.Label));
        Assert.Equal(["bb", "aa"], checksums.Select(row => row.Value));
    }

    [Fact]
    public void The_checksums_offered_for_a_read_are_the_ones_the_engine_trusts()
    {
        // Rclone names these itself, and each extra one costs another pass over an object that has no stored
        // value, so the list stays the engine's preferred chain rather than whatever backend hash exists.
        Assert.Equal(["md5", "sha1", "sha256"], CloudItemDetails.HashPreference);
    }

    [Fact]
    public void A_selection_adds_up_the_files_and_leaves_the_folders_unmeasured()
    {
        var rows = CloudItemDetails.ForSelection([
            FileEntry("a.txt", 1024),
            FileEntry("b.txt", 3072),
            FolderEntry("docs"),
        ], Account("files", "shared"));

        Assert.Equal("3 项", DetailValue(rows, "选择"));
        Assert.Equal("2 个", DetailValue(rows, "文件"));
        Assert.Equal("4.0 KB（4,096 字节）", DetailValue(rows, "大小"));
        Assert.Equal("1 个（大小未统计）", DetailValue(rows, "文件夹"));
    }

    [Fact]
    public void A_selection_of_nothing_but_folders_claims_no_total()
    {
        var rows = CloudItemDetails.ForSelection([FolderEntry("a"), FolderEntry("b")], account: null);

        Assert.Equal("没有文件", DetailValue(rows, "大小"));
        Assert.Equal("未知", DetailValue(rows, "最近修改"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(512_000, "500.0 KB")]
    [InlineData(2_097_152, "2.0 MB")]
    [InlineData(3_221_225_472, "3.0 GB")]
    public void The_size_units_are_the_same_words_the_list_column_uses(long bytes, string expected)
    {
        // The column used to print "0.0 MB" for anything under a megabyte; both it and the details panel now
        // read through this one formatter, so a file cannot be 500.0 KB in one place and 0.0 MB in the other.
        Assert.Equal(expected, CloudItemDetails.HumanSize(bytes));
        Assert.Equal(expected, new CloudFileEntry("a.bin", "a.bin", false, bytes, null).SizeDisplay);
    }

    [Fact]
    public void The_location_rows_name_the_address_rclone_would_take()
    {
        var rows = CloudItemDetails.For(FileEntry("a.txt", 1024, path: "shared/docs/a.txt"), Account("files", "shared"),
            BackendStat());

        Assert.Equal("files:shared/docs/a.txt", DetailValue(rows, "路径"));
        Assert.Equal("files · SFTP · sftp", DetailValue(rows, "所在端点"));
        Assert.Equal("/shared", DetailValue(rows, "端点根目录"));
    }

    // ---------------------------------------------------------------- share link (分享链接)

    [Fact]
    public void Only_a_file_earns_the_share_command()
    {
        // Measured against the bundled v1.75.0 with a real S3 server: a prefix answers 404 "object not found",
        // so a folder has no link to be given — and a link over a folder would grant a subtree nobody selected.
        Assert.True(CloudShareLinks.OffersLink(FileEntry("a.txt", 1024)));
        Assert.False(CloudShareLinks.OffersLink(FolderEntry("docs")));
    }

    [Fact]
    public void The_question_before_the_link_names_what_the_link_grants()
    {
        var text = CloudShareLinks.ConfirmationFor(FileEntry("report.pdf", 1024));

        // rclone's own default is the least-restricted link it can make, so the user has to be told this is an
        // account-free read grant before anything exists, not after the URL is in their clipboard.
        Assert.Contains("report.pdf", text, StringComparison.Ordinal);
        Assert.Contains("无需登录", text, StringComparison.Ordinal);
        Assert.Contains("由云端决定", text, StringComparison.Ordinal);
        Assert.EndsWith("确定生成？", text, StringComparison.Ordinal);
        Assert.Equal("生成分享链接", CloudShareLinks.ConfirmationTitle);
    }

    [Fact]
    public void A_link_that_came_back_is_the_one_that_gets_stored()
    {
        var result = CloudShareLinks.FromAnswer("https://example.test/a.txt?sign=1");

        Assert.Equal(CloudShareLinkOutcome.Copied, result.Outcome);
        Assert.Equal("https://example.test/a.txt?sign=1", result.Url);
    }

    [Fact]
    public void An_answer_without_a_link_is_a_failure_rather_than_an_empty_success()
    {
        // A 200 with no usable url would otherwise be reported as "已复制", and the user would paste nothing.
        foreach (var answer in new string?[] { null, "", "   " })
            Assert.Equal(CloudShareLinkOutcome.Failed, CloudShareLinks.FromAnswer(answer).Outcome);

        Assert.Contains("云端没有返回链接内容", CloudShareLinks.Explain(CloudShareLinks.FromAnswer(null)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_backend_without_links_is_called_unsupported_rather_than_temporarily_broken()
    {
        // The exact wording this test uses is what v1.75.0 answers over a local backend, and the shared
        // classifier reads that HTTP 500 as a temporary connection problem — a retryable-sounding sentence for
        // something that will never succeed here. The share rules have to look at the words first.
        var failure = RcloneFailureClassifier.FromRc("operations/publiclink", 500,
            "{\"error\":\"Local file system at //?/C:/files doesn't support public links\",\"input\":{},\"path\":\"operations/publiclink\",\"status\":500}");
        Assert.Equal(RcloneFailureCategory.Temporary, failure.Category);

        var result = CloudShareLinks.FromFailure(new RcloneException(failure));

        Assert.Equal(CloudShareLinkOutcome.Unsupported, result.Outcome);
        Assert.Contains("此端点不支持分享链接", CloudShareLinks.Explain(result), StringComparison.Ordinal);
        Assert.Contains("没有生成任何东西", CloudShareLinks.Explain(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_gone_object_is_reported_as_missing_rather_than_as_a_network_failure()
    {
        var failure = RcloneFailureClassifier.FromRc("operations/publiclink", 404,
            "{\"error\":\"object not found\",\"input\":{},\"path\":\"operations/publiclink\",\"status\":404}");

        var result = CloudShareLinks.FromFailure(new RcloneException(failure));

        Assert.Equal(CloudShareLinkOutcome.Missing, result.Outcome);
        Assert.Contains("请刷新目录", CloudShareLinks.Explain(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_that_is_not_about_links_keeps_its_diagnosis()
    {
        var result = CloudShareLinks.FromFailure(Rclone(RcloneFailureCategory.Permission));

        Assert.Equal(CloudShareLinkOutcome.Failed, result.Outcome);
        // The generic sentence alone would leave the user with nothing to quote; the diagnosis id has to travel.
        Assert.Contains("没有取到分享链接", CloudShareLinks.Explain(result), StringComparison.Ordinal);
        Assert.Contains("rclone failed: Permission", CloudShareLinks.Explain(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_the_clipboard_refused_is_still_reported_as_a_link()
    {
        var text = CloudShareLinks.Explain(new CloudShareLinkResult(CloudShareLinkOutcome.CopiedNotStored,
            "https://example.test/a.txt", "clipboard busy"));

        // Saying "没有取到分享链接" here would send the user to ask the cloud again for something it already gave.
        Assert.Contains("云端已经生成链接", text, StringComparison.Ordinal);
        Assert.DoesNotContain("没有取到分享链接", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Local file system at C:/files doesn't support public links", true)]
    [InlineData("Sftp remote doesn't support public links", true)]
    [InlineData("Google drive does not support public links", true)]
    [InlineData("object not found", false)]
    public void The_refusal_rclone_words_is_recognised_in_either_spelling(string message, bool expected) =>
        Assert.Equal(expected, CloudShareLinks.IsUnsupported(message));

    [Fact]
    public void Every_outcome_gets_a_sentence_of_its_own()
    {
        var sentences = new[]
        {
            CloudShareLinkOutcome.Copied, CloudShareLinkOutcome.CopiedNotStored, CloudShareLinkOutcome.Declined,
            CloudShareLinkOutcome.Unsupported, CloudShareLinkOutcome.Missing, CloudShareLinkOutcome.NotAFile,
            CloudShareLinkOutcome.Failed,
        }.Select(outcome => CloudShareLinks.Explain(new CloudShareLinkResult(outcome, "url"))).ToList();

        Assert.Equal(sentences.Count, sentences.Distinct().Count());
        Assert.All(sentences, sentence => Assert.False(string.IsNullOrWhiteSpace(sentence)));
        Assert.Contains("已复制到剪贴板", sentences[0], StringComparison.Ordinal);
        Assert.Contains("已取消", sentences[2], StringComparison.Ordinal);
        Assert.Contains("文件夹", sentences[5], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- recycle bin (回收站)

    [Fact]
    public void The_trash_is_one_named_folder_at_the_endpoint_root()
    {
        // The mirror lives at the *endpoint's* root, not the rclone remote's: an S3 endpoint whose prefix is
        // enforced by an alias would otherwise be told to write outside the area rclone lets it touch.
        Assert.Equal(".fengsync-trash", CloudRecycleBin.RootOf(""));
        Assert.Equal("Documents/.fengsync-trash", CloudRecycleBin.RootOf("Documents"));
        Assert.True(CloudRecycleBin.IsRoot("Documents", "Documents/.fengsync-trash"));
        Assert.True(CloudRecycleBin.IsRoot("Documents", "/Documents/.fengsync-trash/"));
        Assert.False(CloudRecycleBin.IsRoot("Documents", "Documents/.fengsync-trash/a.txt"));
        Assert.True(CloudRecycleBin.IsInside("Documents", "Documents/.fengsync-trash/a/b.txt"));
        Assert.False(CloudRecycleBin.IsInside("Documents", "Documents/.fengsync-trash"));
        Assert.False(CloudRecycleBin.IsInside("Documents", "Documents/a/b.txt"));
        // Standing in the folder or anywhere under it is what shows 还原 and 清空回收站.
        Assert.True(CloudRecycleBin.IsTrashView("Documents", "Documents/.fengsync-trash"));
        Assert.True(CloudRecycleBin.IsTrashView("Documents", "Documents/.fengsync-trash/a"));
        Assert.False(CloudRecycleBin.IsTrashView("Documents", "Documents"));
    }

    [Fact]
    public void A_trashed_object_keeps_the_directory_it_came_from_as_a_mirror()
    {
        // The object carries its own origin in its path, so 还原 needs no lookup table and survives the app data
        // being cleared — which is also why the mirror has to be exactly reversible.
        Assert.Equal("Documents/.fengsync-trash/reports", CloudRecycleBin.LandingDirectory("Documents", "Documents/reports/a.pdf"));
        Assert.Equal("Documents/.fengsync-trash", CloudRecycleBin.LandingDirectory("Documents", "Documents/top.txt"));
        Assert.Equal("Documents/reports/a.pdf", CloudRecycleBin.OriginalPath("Documents", "Documents/.fengsync-trash/reports/a.pdf"));
        Assert.Equal("Documents/top.txt", CloudRecycleBin.OriginalPath("Documents", "Documents/.fengsync-trash/top.txt"));
        Assert.Equal("Documents/reports", CloudRecycleBin.OriginalDirectory("Documents", "Documents/.fengsync-trash/reports/sub"));
        // A row that came from the endpoint's top level goes back to that top level, named in full: the browser
        // addresses rows from the remote root, so the root is a path rather than the empty string.
        Assert.Equal("Documents", CloudRecycleBin.OriginalDirectory("Documents", "Documents/.fengsync-trash/top.txt"));
        // Same rule with no endpoint root, which is what an alias-backed S3 endpoint has.
        Assert.Equal(".fengsync-trash/a", CloudRecycleBin.LandingDirectory("", "a/x.txt"));
        Assert.Equal("a/x.txt", CloudRecycleBin.OriginalPath("", ".fengsync-trash/a/x.txt"));
    }

    [Fact]
    public void Something_that_was_never_recycled_has_nowhere_to_be_restored_to()
    {
        Assert.Null(CloudRecycleBin.OriginalPath("Documents", "Documents/notes.txt"));
        Assert.Null(CloudRecycleBin.OriginalPath("Documents", "Documents/.fengsync-trash"));

        var rows = new[] { FileEntry("a.txt", 10, "Documents/.fengsync-trash/a.txt"), FileEntry("b.txt", 10, "Documents/b.txt") };
        var restorable = CloudRecycleBin.SelectForRestore("Documents", rows);

        // A restore that moved b.txt would put it somewhere the user never asked for, so it is filtered out here
        // rather than trusted to the caller.
        Assert.Equal(["Documents/.fengsync-trash/a.txt"], restorable.Select(entry => entry.Path));
    }

    [Fact]
    public void The_trash_folder_is_never_moved_into_itself()
    {
        var root = new[] { FolderAt("Documents/.fengsync-trash") };
        Assert.Equal(CloudRecycleOutcome.IsTrashFolder, CloudRecycleBin.SelectForMove("Documents", root).Refusal);

        var trashed = new[] { FileEntry("a.txt", 10, "Documents/.fengsync-trash/a.txt") };
        Assert.Equal(CloudRecycleOutcome.InsideTrash, CloudRecycleBin.SelectForMove("Documents", trashed).Refusal);

        // A selection of the folder and its neighbours is the user pointing at both at once: the neighbours still
        // move, because refusing the whole gesture is less useful than saying what was left out.
        var mixed = new[]
        {
            FileEntry("a.txt", 10, "Documents/a.txt"),
            FileEntry("gone.txt", 10, "Documents/.fengsync-trash/gone.txt"),
        };
        var (movable, refusal) = CloudRecycleBin.SelectForMove("Documents", mixed);
        Assert.Null(refusal);
        Assert.Equal(["Documents/a.txt"], movable.Select(entry => entry.Path));
    }

    [Fact]
    public void A_taken_landing_name_becomes_name_1_instead_of_replacing_what_is_there()
    {
        // Measured on v1.75.0: operations/movefile overwrites a file that is already at the destination, and a
        // directory move merges and lets the incoming bytes win. Both would destroy the copy that got there
        // first — the object the whole gesture exists to keep — so the name is chosen free before anything moves.
        var entries = new[] { FileEntry("x.txt", 10, "Documents/x.txt") };
        var placements = CloudRecycleBin.PlacementsFor("Documents", entries,
            _ => ["x.txt", "KEEPME"], restore: false);

        Assert.Single(placements);
        Assert.Equal("Documents/.fengsync-trash/x (1).txt", placements[0].Destination);
        Assert.True(placements[0].Renamed);
    }

    [Fact]
    public void One_batch_cannot_point_two_rows_at_the_same_path()
    {
        // The taken list is only what the directory held when it was listed; the rows in this same batch have to
        // be claimed too, or two of them would land on a name the first one already took.
        var entries = new[]
        {
            FileEntry("x.txt", 10, "Documents/a/x.txt"),
            FileEntry("x.txt", 12, "Documents/a/x.txt"),
        };
        var placements = CloudRecycleBin.PlacementsFor("Documents", entries, _ => [], restore: false);

        Assert.Equal(
            ["Documents/.fengsync-trash/a/x.txt", "Documents/.fengsync-trash/a/x (1).txt"],
            placements.Select(placement => placement.Destination));
        Assert.Equal(new[] { false, true }, placements.Select(placement => placement.Renamed).ToList());
    }

    [Fact]
    public void Two_same_named_rows_from_two_directories_both_keep_their_name()
    {
        // Freeness is a property of the directory the row lands in, so it is tracked per directory: two rows the
        // listing says nothing about, from two different folders, both keep their own name even though the names
        // are identical to each other.
        var entries = new[]
        {
            FileEntry("x.txt", 10, "Documents/a/x.txt"),
            FileEntry("x.txt", 12, "Documents/b/x.txt"),
        };
        var placements = CloudRecycleBin.PlacementsFor("Documents", entries, _ => [], restore: false);

        Assert.Equal(
            ["Documents/.fengsync-trash/a/x.txt", "Documents/.fengsync-trash/b/x.txt"],
            placements.Select(placement => placement.Destination));
        Assert.All(placements, placement => Assert.False(placement.Renamed));
    }

    [Fact]
    public void A_restore_lands_back_where_it_came_from_and_yields_to_what_is_there_now()
    {
        var entries = new[] { FileEntry("a.pdf", 10, "Documents/.fengsync-trash/reports/a.pdf") };
        // Nothing else took the name in the meantime, so the row goes back under the name the user remembers —
        // and the missing directory is not a problem, because both move legs create the parents they need.
        var moved = CloudRecycleBin.PlacementsFor("Documents", entries, _ => [], restore: true);
        Assert.Equal("Documents/reports/a.pdf", moved[0].Destination);
        Assert.False(moved[0].Renamed);

        // Something already holds that name: both move legs overwrite rather than refuse, so the restore yields
        // to it and says the new name out loud instead of replacing that other object.
        var blocked = CloudRecycleBin.PlacementsFor("Documents", entries, _ => ["a.pdf"], restore: true);
        Assert.Equal("Documents/reports/a (1).pdf", blocked[0].Destination);
        Assert.True(blocked[0].Renamed);
    }

    [Fact]
    public void The_recycle_question_says_space_is_not_freed_and_where_to_look()
    {
        var text = CloudRecycleBin.ConfirmFor([FileEntry("a.txt", 10, "Documents/a.txt")]);

        Assert.Contains("不会释放空间", text, StringComparison.Ordinal);
        Assert.Contains(CloudRecycleBin.FolderName, text, StringComparison.Ordinal);
        Assert.Contains("还原", text, StringComparison.Ordinal);
        Assert.Contains("名称 (1)", text, StringComparison.Ordinal);
        Assert.Contains("清空回收站", text, StringComparison.Ordinal);
        Assert.EndsWith("确定移动？", text, StringComparison.Ordinal);
        // A folder goes whole, and the count of rows the user pointed at says nothing about that on its own.
        Assert.Contains("文件夹 1 个会连同里面的全部内容一起移动",
            CloudRecycleBin.ConfirmFor([FolderAt("Documents/docs")]), StringComparison.Ordinal);
    }

    [Fact]
    public void Emptying_the_trash_is_the_delete_question_with_one_more_line_not_a_second_rule()
    {
        var entries = Enumerable.Range(0, 11).Select(index => FileEntry($"a{index}.txt", 10, $".fengsync-trash/a{index}.txt")).ToList();

        var plain = CloudDeleteSafety.Describe(entries, supportsTrash: false);
        var fromTrash = CloudDeleteSafety.Describe(entries, supportsTrash: false, insideRecycleBin: true);

        // The threshold, the counting and the irreversibility flag are the ones 删除 already uses; the two
        // messages differ by exactly the one line about the last safety net.
        Assert.Equal(1, fromTrash.Message.Split('\n').Length - plain.Message.Split('\n').Length);
        Assert.Contains("本应用的「还原」也帮不上了", fromTrash.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("本应用的「还原」", plain.Message, StringComparison.Ordinal);
        Assert.True(fromTrash.IsBulk);
        Assert.Equal(plain.IsBulk, fromTrash.IsBulk);
        Assert.Equal(plain.IsIrreversible, fromTrash.IsIrreversible);
        // The threshold itself stays where it was: ten is one click, eleven is a deliberate one.
        Assert.False(CloudDeleteSafety.Describe(entries.Take(CloudDeleteSafety.BulkThreshold).ToList(), false).IsBulk);
    }

    [Fact]
    public void Every_recycle_outcome_gets_a_sentence_of_its_own()
    {
        var outcomes = new[]
        {
            CloudRecycleOutcome.Moved, CloudRecycleOutcome.Restored, CloudRecycleOutcome.Declined,
            CloudRecycleOutcome.InsideTrash, CloudRecycleOutcome.IsTrashFolder, CloudRecycleOutcome.NotInTrash,
            CloudRecycleOutcome.Failed,
        };
        var sentences = outcomes.Select(outcome => CloudRecycleBin.Explain(new CloudRecycleResult(outcome, [
            new CloudRecyclePlacement("Documents/a.txt", "Documents/.fengsync-trash/a.txt", false),
            new CloudRecyclePlacement("Documents/b.txt", "Documents/.fengsync-trash/b.txt", false),
        ]))).ToList();

        Assert.Equal(outcomes.Length, sentences.Distinct().Count());
        Assert.All(sentences, sentence => Assert.False(string.IsNullOrWhiteSpace(sentence)));
        Assert.Contains("空间未释放", sentences[0], StringComparison.Ordinal);
        Assert.Contains("已还原 2 项", sentences[1], StringComparison.Ordinal);
        Assert.Contains("云端没有任何东西被移动", sentences[2], StringComparison.Ordinal);
        Assert.Contains("已经在回收站里", sentences[3], StringComparison.Ordinal);
        Assert.Contains("回收站文件夹本身", sentences[4], StringComparison.Ordinal);
        Assert.Contains("没有可还原的来源", sentences[5], StringComparison.Ordinal);
        Assert.Contains("没有完成", sentences[6], StringComparison.Ordinal);
    }

    [Fact]
    public void A_rename_inside_the_batch_and_a_half_finished_batch_are_both_said_out_loud()
    {
        var intoTrash = new[]
        {
            new CloudRecyclePlacement("Documents/x.txt", "Documents/.fengsync-trash/x (1).txt", true),
            new CloudRecyclePlacement("Documents/y.txt", "Documents/.fengsync-trash/y.txt", false),
        };

        var moved = CloudRecycleBin.Explain(new CloudRecycleResult(CloudRecycleOutcome.Moved, intoTrash, 2, "云端拒绝了操作"));
        Assert.Contains("其中 1 项在回收站里已有同名项目，改存为 x (1).txt", moved, StringComparison.Ordinal);
        // The count of what did NOT move is what tells the user to refresh, so it belongs in the same sentence.
        Assert.Contains("2 项没有成功：云端拒绝了操作", moved, StringComparison.Ordinal);

        var back = new[]
        {
            new CloudRecyclePlacement("Documents/.fengsync-trash/reports/a.pdf", "Documents/reports/a (1).pdf", true),
            new CloudRecyclePlacement("Documents/.fengsync-trash/reports/b.pdf", "Documents/reports/b.pdf", false),
        };
        Assert.Contains("其中 1 项原名仍被占用，还原为 a (1).pdf",
            CloudRecycleBin.Explain(new CloudRecycleResult(CloudRecycleOutcome.Restored, back)), StringComparison.Ordinal);

        // With one row that kept its name the destination is the whole answer, and a count alone would not be.
        var single = new[] { new CloudRecyclePlacement("Documents/.fengsync-trash/top.txt", "Documents/top.txt", false) };
        Assert.Contains("已回到 Documents/top.txt",
            CloudRecycleBin.Explain(new CloudRecycleResult(CloudRecycleOutcome.Restored, single)), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- endpoint health

    [Fact]
    public void A_rejected_credential_is_reported_as_needing_a_new_login()
    {
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.Classify(Rclone(RcloneFailureCategory.Authentication)));
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.Classify(Rclone(RcloneFailureCategory.Permission)));
    }

    [Fact]
    public void A_plain_network_outage_is_not_reported_as_a_login_problem()
    {
        // Calling an outage "needs re-login" would send the user through a browser authorization that
        // cannot fix anything.
        Assert.Equal(CloudEndpointHealth.Unreachable, CloudEndpointStatusService.Classify(new IOException("connection reset by peer")));
        Assert.Equal(CloudEndpointHealth.Unreachable, CloudEndpointStatusService.Classify(Rclone(RcloneFailureCategory.Dns)));
    }

    [Fact]
    public void An_unclassified_error_that_mentions_an_expired_token_is_still_a_login_problem()
    {
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.ClassifyMessage("oauth2: cannot fetch token: invalid_grant"));
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.ClassifyMessage("401 Unauthorized"));
        Assert.Equal(CloudEndpointHealth.Unreachable, CloudEndpointStatusService.ClassifyMessage("dial tcp: i/o timeout"));
    }

    [Fact]
    public void A_backend_that_reports_no_quota_shows_nothing_extra()
    {
        // Most backends answer nothing at all, and an absent quota is "unknown", never "0 of 0".
        Assert.Equal("", new CloudEndpointStatus(CloudEndpointHealth.Connected, "").QuotaText);
        Assert.Equal("", new CloudEndpointStatus(CloudEndpointHealth.Connected, "", TotalBytes: 0).QuotaText);
        Assert.Equal("", CloudEndpointStatus.Unknown.QuotaText);
    }

    [Fact]
    public void A_reported_quota_is_summarised_in_readable_units()
    {
        var status = new CloudEndpointStatus(CloudEndpointHealth.Connected, "", 100L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024);

        Assert.Contains("已用", status.QuotaText, StringComparison.Ordinal);
        Assert.Contains("100", status.QuotaText, StringComparison.Ordinal);
        Assert.Contains("GB", status.QuotaText, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_health_state_reads_as_the_answer_the_user_asked_for()
    {
        Assert.Equal("需要重新登录", new CloudEndpointStatus(CloudEndpointHealth.NeedsLogin, "").HealthText);
        Assert.Equal("已连接", new CloudEndpointStatus(CloudEndpointHealth.Connected, "").HealthText);
        Assert.Equal("无法连接", new CloudEndpointStatus(CloudEndpointHealth.Unreachable, "").HealthText);
        Assert.Equal("未检查", CloudEndpointStatus.Unknown.HealthText);
    }

    // ---------------------------------------------------------------- transfer queue

    [Fact]
    public void The_concurrency_limit_cannot_be_driven_outside_its_supported_range()
    {
        var queue = NewQueue();

        queue.Concurrency = 0;
        Assert.Equal(CloudTransferQueue.MinimumConcurrency, queue.Concurrency);
        queue.Concurrency = 999;
        Assert.Equal(CloudTransferQueue.MaximumConcurrency, queue.Concurrency);
        queue.Concurrency = 4;
        Assert.Equal(4, queue.Concurrency);
    }

    [Fact]
    public void The_automatic_retry_count_cannot_be_driven_outside_its_supported_range()
    {
        var queue = NewQueue();

        queue.MaxRetries = -3;
        Assert.Equal(0, queue.MaxRetries);
        queue.MaxRetries = 99;
        Assert.Equal(10, queue.MaxRetries);
    }

    [Fact]
    public void A_failure_that_waiting_cannot_fix_is_not_retried()
    {
        // Retrying these would only delay the error the user needs to see — and for an expired login it
        // would delay it three times over.
        Assert.False(CloudTransferQueue.IsRetryable(new FileNotFoundException("gone", "a.txt")));
        Assert.False(CloudTransferQueue.IsRetryable(new DirectoryNotFoundException("gone")));
        Assert.False(CloudTransferQueue.IsRetryable(new UnauthorizedAccessException("denied")));
        Assert.False(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.Authentication)));
        Assert.False(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.NotFound)));

        Assert.True(CloudTransferQueue.IsRetryable(new IOException("connection reset")));
        Assert.True(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.Temporary)));
        Assert.True(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.RateLimit)));
    }

    [Fact]
    public void A_paused_transfer_is_distinct_from_a_cancelled_one_and_is_resumable()
    {
        var queue = NewQueue();
        var task = Add(queue, "docs/a.txt");

        queue.Pause(task);
        Assert.Equal(CloudTransferState.Paused, task.State);
        Assert.True(task.IsResumable);
        Assert.True(queue.HasPausedTasks);
        // A parked transfer is the newest way to be stopped, so it is offered by the continue action.
        Assert.True(queue.HasResumableTasks);
        Assert.Equal("继续", task.RestartText);

        queue.Cancel(task);
        Assert.Equal(CloudTransferState.Cancelled, task.State);
        // Cancelling is still something the user can undo, but it is not a pause.
        Assert.True(task.IsResumable);
        Assert.False(queue.HasPausedTasks);
        Assert.Equal("重试", task.RestartText);
    }

    [Fact]
    public void Pausing_everything_counts_only_the_transfers_that_can_be_paused()
    {
        var queue = NewQueue();
        var pending = Add(queue, "docs/a.txt");
        var completed = Add(queue, "docs/b.txt", CloudTransferState.Completed);

        Assert.Equal(1, queue.PauseAll());
        Assert.Equal(CloudTransferState.Paused, pending.State);
        Assert.Equal(CloudTransferState.Completed, completed.State);
    }

    [Fact]
    public void Clearing_finished_transfers_keeps_everything_the_user_can_still_continue()
    {
        var queue = NewQueue();
        var completed = Add(queue, "docs/done.txt", CloudTransferState.Completed);
        var cancelled = Add(queue, "docs/stopped.txt", CloudTransferState.Cancelled);
        var failed = Add(queue, "docs/failed.txt", CloudTransferState.Failed);

        // "清除已完成" must not silently drop the queue of things the user still means to finish.
        Assert.True(queue.HasCompletedTasks);
        queue.ClearFinished();

        Assert.Equal(["docs/stopped.txt", "docs/failed.txt"], queue.Tasks.Select(task => task.RemotePath));
        Assert.False(queue.HasCompletedTasks);
        Assert.NotNull(completed);
    }

    [Fact]
    public void A_queue_of_only_stopped_transfers_does_not_offer_to_clear_anything()
    {
        var queue = NewQueue();
        queue.Cancel(Add(queue, "docs/a.txt"));

        Assert.False(queue.HasCompletedTasks);
    }

    [Fact]
    public async Task A_restart_brings_back_an_interrupted_transfer_as_cancelled_and_a_parked_one_as_paused()
    {
        var root = NewDirectory();
        _roots.Add(root);
        // The journal root is removed when this test class is disposed.
        {
            var journal = new CloudTransferJournalStore(root);
            var interrupted = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/big.bin", @"C:\tmp\big.bin", 4096, CloudTransferState.Running, "connection reset");
            var parked = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Upload,
                "s3://archive", "docs/parked.bin", @"C:\tmp\parked.bin", 4096, CloudTransferState.Paused, null);
            var done = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/done.bin", @"C:\tmp\done.bin", 4096, CloudTransferState.Completed, null);
            await journal.SaveAsync(interrupted);
            await journal.SaveAsync(parked);
            await journal.SaveAsync(done);

            var queue = new CloudTransferQueue(journal);
            var restored = await queue.LoadResumableAsync();

            // A finished transfer is not worth restoring; a running one is factually a cancelled one, while
            // a pause was a deliberate act that must survive the restart.
            Assert.Equal(2, restored);
            Assert.Equal(CloudTransferState.Cancelled, queue.Tasks.Single(task => task.Id == interrupted.Id).State);
            Assert.Equal(CloudTransferState.Paused, queue.Tasks.Single(task => task.Id == parked.Id).State);
            Assert.DoesNotContain(queue.Tasks, task => task.Id == done.Id);
            // Restoring lists the work; it deliberately does not start any network transfer on its own.
            Assert.False(queue.HasActiveTransfers);
        }
    }

    [Fact]
    public async Task A_restored_failure_keeps_its_full_diagnostic_not_just_the_one_line_message()
    {
        var root = NewDirectory();
        _roots.Add(root);
        var detail = string.Join(Environment.NewLine, "上下文：cloud-transfer", "操作：operations/copyfile", "诊断 ID：abc123");
        {
            var journal = new CloudTransferJournalStore(root);
            await journal.SaveAsync(new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/big.bin", @"C:\tmp\big.bin", 4096, CloudTransferState.Failed, "网络中断", detail));

            var queue = new CloudTransferQueue(journal);
            Assert.Equal(1, await queue.LoadResumableAsync());

            // The short message is what the row shows; the detail is what a bug report needs, and a failure
            // noticed after a restart must still be diagnosable.
            var restored = Assert.Single(queue.Tasks);
            Assert.Equal("网络中断", restored.Error);
            Assert.Equal(detail, restored.ErrorDetail);
            Assert.True(restored.HasErrorDetail);
        }
    }

    [Fact]
    public async Task A_journal_written_before_the_detail_field_existed_still_loads()
    {
        var root = NewDirectory();
        _roots.Add(root);
        {
            var journal = new CloudTransferJournalStore(root);
            // Shape of a journal file produced by the previous schema: no errorDetail property at all.
            var id = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(root, id + ".json"),
                $$"""{"Id":"{{id}}","Direction":1,"RemoteUri":"s3://archive","RemotePath":"docs/a.bin","LocalPath":"C:\\tmp\\a.bin","TotalBytes":10,"State":5,"Error":"failed"}""");

            var queue = new CloudTransferQueue(journal);
            Assert.Equal(1, await queue.LoadResumableAsync());

            var restored = Assert.Single(queue.Tasks);
            Assert.Equal("failed", restored.Error);
            Assert.Null(restored.ErrorDetail);
            Assert.False(restored.HasErrorDetail);
        }
    }

    [Fact]
    public void The_copyable_diagnostic_carries_what_a_log_search_needs()
    {
        var failure = new RcloneFailure(RcloneFailureCategory.Authentication, false, "operations/list", 401, null,
            "授权已过期", "请重新登录", "invalid_grant", """{"remote":"root"}""", "diag-77");
        var text = RcloneUiError.DescribeDetail(new RcloneException(failure), "cloud-transfer");

        // Without the correlation id and the operation the log line cannot be found again.
        Assert.Contains("diag-77", text);
        Assert.Contains("operations/list", text);
        Assert.Contains("Authentication", text);
        Assert.Contains("cloud-transfer", text);
        Assert.Contains("401", text);
    }

    [Fact]
    public void The_copyable_diagnostic_never_echoes_a_credential()
    {
        var text = RcloneUiError.DescribeDetail(new InvalidOperationException("connect failed for password=hunter2"));

        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public async Task Restoring_the_same_journal_twice_does_not_duplicate_a_row()
    {
        var root = NewDirectory();
        _roots.Add(root);
        // The journal root is removed when this test class is disposed.
        {
            var journal = new CloudTransferJournalStore(root);
            await journal.SaveAsync(new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/big.bin", @"C:\tmp\big.bin", 4096, CloudTransferState.Running, null));

            var queue = new CloudTransferQueue(journal);
            Assert.Equal(1, await queue.LoadResumableAsync());
            Assert.Equal(0, await queue.LoadResumableAsync());
            Assert.Single(queue.Tasks);
        }
    }

    [Fact]
    public void A_queue_with_nothing_left_to_do_reports_a_full_progress_bar()
    {
        Assert.Equal(0, NewQueue().OverallPercentage);

        // A parked transfer's bytes are not done, so a queue that is merely paused must not read as 100%.
        var paused = NewQueue();
        var parked = Add(paused, "docs/a.txt");
        paused.Pause(parked);
        Assert.Equal(0, paused.OverallPercentage);

        // Only a batch that actually drained reports the bar as full.
        var drained = NewQueue();
        Add(drained, "docs/b.txt", CloudTransferState.Completed);
        Assert.Equal(100, drained.OverallPercentage);
    }

    // ---------------------------------------------------------------- completion reporting

    [Fact]
    public void A_batch_the_user_stopped_themselves_reports_nothing()
    {
        // A notification that fires on the user's own action is noise.
        Assert.Equal("", CloudTransferSummary.Describe(new CloudTransferBatch(3, 0, 0, 3)));
        Assert.Equal("", CloudTransferSummary.Describe(new CloudTransferBatch(0, 0, 0, 0)));
    }

    [Fact]
    public void A_batch_that_finished_reports_how_many()
    {
        Assert.Equal("云盘传输完成：2 项。", CloudTransferSummary.Describe(new CloudTransferBatch(2, 2, 0, 0)));
    }

    [Fact]
    public void A_mixed_batch_reports_success_failure_and_stops_separately()
    {
        var message = CloudTransferSummary.Describe(new CloudTransferBatch(4, 1, 2, 1));

        Assert.Contains("1 项成功", message, StringComparison.Ordinal);
        Assert.Contains("2 项失败", message, StringComparison.Ordinal);
        Assert.Contains("1 项已停止", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_completion_notification_honours_the_setting()
    {
        var queue = NewQueue();
        var notifier = new CloudCompletionNotifier(queue) { Enabled = false };
        string? reported = null;
        notifier.Notified += (_, message) => reported = message;

        notifier.Report(new CloudTransferBatch(1, 1, 0, 0));
        Assert.Null(reported);
        Assert.Null(notifier.LastMessage);

        notifier.Enabled = true;
        notifier.Report(new CloudTransferBatch(1, 1, 0, 0));
        Assert.Equal("云盘传输完成：1 项。", reported);
        Assert.Equal(reported, notifier.LastMessage);
        notifier.Dispose();
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void The_cloud_defaults_are_the_values_an_unconfigured_installation_expects()
    {
        var settings = new ApplicationSettings();

        // Two matches the concurrency the queue had before it became adjustable, so an existing
        // installation behaves exactly as it did.
        Assert.Equal(CloudTransferQueue.DefaultConcurrency, settings.CloudMaxConcurrentTransfers);
        Assert.Equal(2, settings.CloudRetryCount);
        Assert.Null(settings.CloudDownloadDirectory);
        Assert.Equal(CloudUploadConflictPolicy.Ask, settings.CloudUploadConflict);
        Assert.True(settings.CloudNotifyOnCompletion);
        Assert.True(settings.CloudResumeOnStartup);
    }

    [Theory]
    [InlineData(0, 2, false)]
    [InlineData(17, 2, false)]
    [InlineData(4, -1, false)]
    [InlineData(4, 11, false)]
    [InlineData(4, 2, true)]
    public void The_cloud_settings_are_validated_where_they_are_loaded(int concurrency, int retries, bool expectedValid)
    {
        var settings = new ApplicationSettings { CloudMaxConcurrentTransfers = concurrency, CloudRetryCount = retries };

        var errors = ConfigurationValidator.Validate(settings);

        Assert.Equal(expectedValid, errors.Count == 0);
    }

    [Theory]
    [InlineData(CloudUploadConflictPolicy.Ask, 0)]
    [InlineData(CloudUploadConflictPolicy.Overwrite, 1)]
    [InlineData(CloudUploadConflictPolicy.Skip, 2)]
    [InlineData(CloudUploadConflictPolicy.KeepBoth, 3)]
    public void The_conflict_picker_round_trips_its_selection(CloudUploadConflictPolicy policy, int index)
    {
        Assert.Equal(index, SettingsWindow.ConflictIndex(policy));
        Assert.Equal(policy, SettingsWindow.ConflictPolicy(index));
    }

    [Fact]
    public void An_unrecognised_conflict_selection_falls_back_to_asking()
    {
        // The picker is the only way to set this, so an index outside its range means the list changed
        // under a saved value; asking is the safe answer.
        Assert.Equal(CloudUploadConflictPolicy.Ask, SettingsWindow.ConflictPolicy(-1));
        Assert.Equal(CloudUploadConflictPolicy.Ask, SettingsWindow.ConflictPolicy(7));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A queue whose journal lives in its own temp directory. Tasks are added through <see cref="Add"/>
    /// rather than <c>Enqueue</c>, because enqueueing starts a real rclone transfer — these tests pin the
    /// state machine, not the byte movement.
    /// </summary>
    private CloudTransferQueue NewQueue()
    {
        var root = NewDirectory();
        _roots.Add(root);
        return new CloudTransferQueue(new CloudTransferJournalStore(root));
    }

    /// <summary>Queues one transfer in the state the test needs, without starting it.</summary>
    private static CloudTransferTask Add(CloudTransferQueue queue, string remotePath, CloudTransferState state = CloudTransferState.Pending)
    {
        var task = new CloudTransferTask(CloudTransferDirection.Download, "s3://archive", remotePath,
            Path.Combine(@"C:\tmp", CloudTransferTask.NameOf(remotePath)), 2048) { State = state };
        queue.Tasks.Add(task);
        return task;
    }

    private static CloudFileEntry FileEntry(string name, long size, string? path = null, DateTimeOffset? modified = null) =>
        new(name, path ?? name, IsDirectory: false, size, modified);

    private static CloudFileEntry FolderEntry(string name) => new(name, name, IsDirectory: true, 0, null);

    /// <summary>A folder row at an explicit path, for rules that read the path rather than the name.</summary>
    private static CloudFileEntry FolderAt(string path) =>
        new(path.Trim('/').Split('/')[^1], path, IsDirectory: true, 0, null);

    /// <summary>An endpoint account with the given name and configured root folder.</summary>
    private static CloudEndpointAccount Account(string name, string root) =>
        new(new RcloneAccount(name, "sftp"), new CloudEndpointMetadata(name, "sftp", "SFTP", root, ""));

    /// <summary>
    /// What the backend answered about one object just now: its current size and MIME type, and whichever
    /// checksums it happens to hold. Nothing here is computed — the point of the fake is to be able to say
    /// "this endpoint has no MD5" without finding one.
    /// </summary>
    private static CloudItemStat BackendStat(long size = 4096, bool directory = false, string? mime = "text/plain",
        IReadOnlyDictionary<string, string>? hashes = null) =>
        new(true, directory, size, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero), mime,
            hashes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static string DetailValue(IReadOnlyList<CloudDetailRow> rows, string label) =>
        rows.Single(row => row.Label == label).Value;

    // ---------------------------------------------------------------- paste: what a clipboard row means

    [Fact]
    public void A_paste_recognises_the_same_file_on_the_same_endpoint_however_it_is_spelled()
    {
        // Whether a path's case is significant is the backend's answer, not Feng Sync's, so the browser's
        // identity rule has always been one remote plus one path, compared the way the rest of the app
        // compares paths. A different remote is a different file even when the paths match exactly.
        var nas = Account("nas", "team");
        var ontoItself = CloudPastePlanner.Build(
            [new CloudClipboardItem(nas, FileEntry("A.TXT", 10, path: "TEAM/a.txt"))], nas, "team", []);
        Assert.Empty(ontoItself.Targets);
        Assert.Contains("已经在目标目录里", string.Join(" ", ontoItself.Skipped), StringComparison.Ordinal);

        var otherEndpoint = CloudPastePlanner.Build(
            [new CloudClipboardItem(nas, FileEntry("a.txt", 10, path: "team/a.txt"))],
            Account("files", "team"), "team", []);
        var target = Assert.Single(otherEndpoint.Targets);
        Assert.Equal("team/a.txt", target.DestinationPath);
        Assert.False(target.SameEndpoint);
    }

    [Fact]
    public void A_paste_reports_the_rows_it_cannot_paste_rather_than_dropping_them_silently()
    {
        // A folder is not a paste target, because the transfer unit is one object, and the reason belongs in
        // the plan: a plan that quietly returned one target would read like a paste that lost the user's files.
        // One line for the folders, not one per row, because the answer is the same for all of them.
        var nas = Account("nas", "team");
        var plan = CloudPastePlanner.Build(
            [
                new CloudClipboardItem(nas, FolderEntry("docs")),
                new CloudClipboardItem(nas, FolderEntry("photos")),
                new CloudClipboardItem(nas, FileEntry("c.txt", 10, path: "team/c.txt")),
            ],
            nas, "team/incoming", [FileEntry("c.txt", 10, path: "team/incoming/c.txt")]);

        var target = Assert.Single(plan.Targets);
        Assert.True(target.OverwritesExisting);
        Assert.Equal(1, plan.Conflicts);
        var skipped = Assert.Single(plan.Skipped);
        Assert.StartsWith("2 个文件夹没有被复制", skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paste_strips_each_endpoints_own_root_and_only_once()
    {
        // The queue addresses an endpoint URI, so both sides of a paste have to be relative to their own
        // endpoint's root — and an endpoint whose prefix lives in an rclone alias has no root to strip,
        // because the alias already applies it. Composing either wrong copies to team/team/a.txt.
        var nas = Account("nas", "team");
        var betweenRoots = CloudPastePlanner.Build(
            [new CloudClipboardItem(nas, FileEntry("a.txt", 10, path: "team/a.txt"))],
            Account("files", "shared"), "shared/incoming", []);
        var across = Assert.Single(betweenRoots.Targets);
        Assert.Equal("a.txt", across.SourceRelativePath);
        Assert.Equal("incoming/a.txt", across.DestinationRelativePath);

        var aliasBacked = new CloudEndpointAccount(new RcloneAccount("docs", "alias"),
            new CloudEndpointMetadata("docs", "alias", "Backblaze", "bucket", "team/files", AliasOf: "docs_s3"));
        var insideAlias = CloudPastePlanner.Build(
            [new CloudClipboardItem(aliasBacked, FileEntry("a.txt", 10, path: "team/files/a.txt"))],
            aliasBacked, "team/files/backup", []);
        var kept = Assert.Single(insideAlias.Targets);
        Assert.Equal("team/files/a.txt", kept.SourceRelativePath);
        Assert.Equal("team/files/backup/a.txt", kept.DestinationRelativePath);
        Assert.Equal("team/files/backup/a.txt", kept.DestinationPath);
        Assert.True(kept.SameEndpoint);
    }

    private static RcloneException Rclone(RcloneFailureCategory category) =>
        new(new RcloneFailure(category, false, "test", null, null, $"rclone failed: {category}", "", "", "", ""));

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-cloud-feature-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
