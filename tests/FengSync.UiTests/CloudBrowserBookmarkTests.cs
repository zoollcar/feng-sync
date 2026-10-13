using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// 收藏 and 最近访问: the two lists the browser keeps of places the user cared about. They are two different
/// promises — a favourite is curated, so its order is the user's and nothing trims it, while a recent visit is a
/// queue the application wrote, so it is newest-first and bounded — and the rules at the top of this file pin that
/// asymmetry without WPF or a window. The file rules come next, because the lists outlive the window and the file
/// is input somebody can edit: a place that cannot address a real remote must not come back as a row that does
/// nothing, and a file this build cannot read has to be kept rather than overwritten. The window cases drive the
/// real browser over a stand-in rclone, because what a jump list must not do is start two listings and let the
/// later answer decide which directory the user is looking at.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserBookmarkTests
{
    private const string Nas = "nas";
    private const string Files = "files";
    private const string NasRoot = "team";
    private const string FilesRoot = "docs";
    private const string NasDocs = NasRoot + "/docs";
    private const string FilesInbox = FilesRoot + "/inbox";

    private static readonly CloudBookmark NasHome = new(Nas, NasRoot);
    private static readonly CloudBookmark NasDocsPlace = new(Nas, NasDocs);
    private static readonly CloudBookmark FilesHome = new(Files, FilesRoot);
    private static readonly CloudBookmark FilesInboxPlace = new(Files, FilesInbox);

    // ------------------------------------------------------------------ the rules

    [Fact]
    public void A_bookmark_names_a_remote_and_a_directory_and_refuses_anything_that_cannot_address_one()
    {
        Assert.Equal(NasDocsPlace, CloudBrowserBookmarks.Normalize(Nas, NasDocs));
        // The name is what rclone calls a section, so a trailing colon is the same endpoint written two ways.
        Assert.Equal(NasDocsPlace, CloudBrowserBookmarks.Normalize(Nas + ":", NasDocs));

        Assert.Null(CloudBrowserBookmarks.Normalize(null, "a"));
        Assert.Null(CloudBrowserBookmarks.Normalize("  ", "a"));
        // A name carrying a separator or a second colon would address a different section, or one that does not
        // exist, once it is written back into a remote string.
        Assert.Null(CloudBrowserBookmarks.Normalize("a/b", "x"));
        Assert.Null(CloudBrowserBookmarks.Normalize("a\\b", "x"));
        Assert.Null(CloudBrowserBookmarks.Normalize("a:b", "x"));
    }

    [Fact]
    public void A_remembered_path_is_the_same_address_the_browser_would_type()
    {
        // The path goes through the browser's own rule rather than a second one, so ".." cannot survive into a
        // place the user then jumps to and finds somewhere else.
        Assert.Equal(new CloudBookmark(Nas, "docs"), CloudBrowserBookmarks.Normalize(Nas, "docs/../docs"));
        Assert.Equal(new CloudBookmark(Nas, "docs"), CloudBrowserBookmarks.Normalize(Nas, "/docs/"));
        Assert.Equal(new CloudBookmark(Nas, "docs/backups"), CloudBrowserBookmarks.Normalize(Nas, "docs\\backups"));
        Assert.Equal(new CloudBookmark(Nas, ""), CloudBrowserBookmarks.Normalize(Nas, ""));
    }

    [Fact]
    public void A_favourite_keeps_the_order_it_was_given_and_adding_a_second_time_does_not_move_it()
    {
        var once = CloudBrowserBookmarks.AddFavorite([], NasHome);
        var twice = CloudBrowserBookmarks.AddFavorite(once, FilesInboxPlace);
        var reordered = CloudBrowserBookmarks.AddFavorite(twice, NasHome);

        // The curator's order is the promise: re-adding what is already there is not a request to move it.
        Assert.Equal([NasHome, FilesInboxPlace], reordered);
        Assert.True(CloudBrowserBookmarks.IsFavorite(reordered, NasHome));
    }

    [Fact]
    public void Removing_a_favourite_takes_the_matching_place_and_leaves_the_rest()
    {
        var kept = CloudBrowserBookmarks.RemoveFavorite([NasHome, NasDocsPlace, FilesHome], NasHome);
        Assert.Equal([NasDocsPlace, FilesHome], kept);
        // Removing a place that was never a favourite changes nothing rather than complaining.
        Assert.Equal(kept, CloudBrowserBookmarks.RemoveFavorite(kept, FilesInboxPlace));
    }

    [Fact]
    public void A_visit_is_put_at_the_front_the_list_drops_the_repeat_and_the_cap_holds()
    {
        var recent = CloudBrowserBookmarks.RecordVisit([], NasHome);
        recent = CloudBrowserBookmarks.RecordVisit(recent, NasDocsPlace);
        recent = CloudBrowserBookmarks.RecordVisit(recent, NasHome);

        Assert.Equal([NasHome, NasDocsPlace], recent);

        for (var index = 0; index < CloudBrowserBookmarks.RecentLimit + 5; index++)
            recent = CloudBrowserBookmarks.RecordVisit(recent, new CloudBookmark(Nas, "dir" + index));

        Assert.Equal(CloudBrowserBookmarks.RecentLimit, recent.Count);
        Assert.Equal(new CloudBookmark(Nas, "dir" + (CloudBrowserBookmarks.RecentLimit + 4)), recent[0]);
        // The oldest visits fell off the back rather than the front.
        Assert.DoesNotContain(new CloudBookmark(Nas, "dir0"), recent);
    }

    [Fact]
    public void Standing_in_the_directory_again_is_not_a_new_visit()
    {
        var recent = CloudBrowserBookmarks.RecordVisit([], NasHome);
        Assert.True(CloudBrowserBookmarks.IsNewestVisit(recent, NasHome));
        Assert.False(CloudBrowserBookmarks.IsNewestVisit(recent, NasDocsPlace));
        // Nothing has been visited yet, so the first listing is a new visit and there is no top entry to match.
        Assert.False(CloudBrowserBookmarks.IsNewestVisit([], NasHome));
    }

    [Fact]
    public void A_place_whose_endpoint_is_missing_is_hidden_and_counted_rather_than_deleted()
    {
        IReadOnlyList<CloudBookmark> favorites = [NasHome, FilesHome, NasDocsPlace];
        var visible = CloudBrowserBookmarks.VisibleFor(favorites, [Nas]);

        Assert.Equal([NasHome, NasDocsPlace], visible);
        Assert.Equal(1, CloudBrowserBookmarks.HiddenCount(favorites, [Nas]));
        // Hiding rather than dropping is the whole point: an endpoint can be missing because rclone.conf could not
        // be read this once, and a favourite the user chose should still be there when it comes back.
        Assert.Equal([NasHome, FilesHome, NasDocsPlace], CloudBrowserBookmarks.VisibleFor(favorites, [Nas, Files]));
    }

    [Fact]
    public void A_rename_moves_every_place_that_named_the_endpoint_and_collapses_a_collision()
    {
        // "NAS" is the same section as "nas" — rclone looks a section up without regard to case — so both entries
        // name the endpoint being renamed, and after the move they name the same place. One place is one entry.
        var renamed = CloudBrowserBookmarks.Renamed([NasHome, new CloudBookmark("NAS", NasRoot), FilesHome], Nas, "archive");
        Assert.Equal([new CloudBookmark("archive", NasRoot), FilesHome], renamed);

        var collided = CloudBrowserBookmarks.Renamed([NasDocsPlace, new CloudBookmark("archive", NasDocs)], Nas, "archive");
        var only = Assert.Single(collided);
        Assert.Equal(new CloudBookmark("archive", NasDocs), only);
        // A place that belonged to another endpoint is left exactly where it was.
        Assert.Equal([FilesHome], CloudBrowserBookmarks.Renamed([FilesHome], Nas, "archive"));
    }

    [Fact]
    public void A_jump_row_names_the_endpoint_first_and_the_directory_second()
    {
        Assert.Equal($"{Nas}  ·  /{NasDocs}", CloudBrowserBookmarks.LabelFor(NasDocsPlace, Nas));
        // A bookmark stores the address the browser uses, so the directory an endpoint is rooted in is written as
        // that path; 根目录 is what an endpoint with no prefix at all shows.
        Assert.Equal($"{Nas}  ·  /{NasRoot}", CloudBrowserBookmarks.LabelFor(NasHome, Nas));
        Assert.Equal($"{Nas}  ·  根目录", CloudBrowserBookmarks.LabelFor(new CloudBookmark(Nas, ""), Nas));

        // The name comes from the endpoint list rather than from the file, so a bookmark written before the case
        // changed reads the way the list reads.
        var rows = CloudBrowserBookmarks.JumpItems([new CloudBookmark("NAS", NasDocs)], [Nas]);
        var row = Assert.Single(rows);
        Assert.Equal($"{Nas}  ·  /{NasDocs}", row.Label);
        // Only the label is renamed by the list: the bookmark keeps the address as it was written, and the jump
        // finds the endpoint case-insensitively, which is how rclone looks a section up.
        Assert.Equal(new CloudBookmark("NAS", NasDocs), row.Bookmark);
        Assert.Empty(CloudBrowserBookmarks.JumpItems([FilesHome], [Nas]));
    }

    [Fact]
    public void One_place_is_the_same_directory_only_when_the_path_matches_exactly()
    {
        // Endpoint names are matched the way rclone looks a section up; paths are matched byte-for-byte, because a
        // backend may be case-sensitive and two directories differing only by case are two places.
        Assert.True(CloudBrowserBookmarks.SamePlace(new CloudBookmark("NAS", "team"), NasHome));
        Assert.False(CloudBrowserBookmarks.SamePlace(new CloudBookmark(Nas, "Team"), NasHome));
    }

    // ------------------------------------------------------------------ the file

    [Fact]
    public async Task Both_lists_round_trip_in_the_order_they_were_saved()
    {
        await WithFile(async path =>
        {
            var store = new CloudBrowserBookmarkStore(path);
            await store.SaveAsync([NasHome, FilesInboxPlace], [FilesHome, NasDocsPlace, NasHome]);

            var loaded = await new CloudBrowserBookmarkStore(path).LoadAsync();
            Assert.Equal([NasHome, FilesInboxPlace], loaded.Favorites);
            Assert.Equal([FilesHome, NasDocsPlace, NasHome], loaded.Recent);
            Assert.False(loaded.RecoveredFromCorruption);
            Assert.Null(loaded.BackupPath);
        });
    }

    [Fact]
    public async Task The_file_holds_place_names_and_has_nowhere_to_put_a_credential()
    {
        await WithFile(async path =>
        {
            await new CloudBrowserBookmarkStore(path).SaveAsync([NasHome], [NasDocsPlace]);
            var text = await File.ReadAllTextAsync(path);

            // The same rule an endpoint export follows: what is remembered is a name and a path. There is no field
            // for a value, so the file cannot become the place a token lives.
            var fields = typeof(CloudBookmarkRecord).GetProperties().Select(property => property.Name).ToArray();
            Assert.Equal(["Remote", "Path"], fields);
            Assert.DoesNotContain("pass", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task A_file_that_cannot_be_parsed_is_kept_aside_and_the_lists_start_empty()
    {
        await WithFile(async path =>
        {
            const string broken = "{ \"Version\": 1, \"Favorites\": [ ";
            await File.WriteAllTextAsync(path, broken);

            var loaded = await new CloudBrowserBookmarkStore(path).LoadAsync();

            Assert.True(loaded.RecoveredFromCorruption);
            Assert.Empty(loaded.Favorites);
            Assert.Empty(loaded.Recent);
            // The bytes the user had are still readable somewhere: this is the only copy of what they chose to keep.
            Assert.NotNull(loaded.BackupPath);
            Assert.True(File.Exists(loaded.BackupPath));
            Assert.Equal(broken, await File.ReadAllTextAsync(loaded.BackupPath!));
            Assert.False(File.Exists(path));

            // and the window can go on writing from here.
            await new CloudBrowserBookmarkStore(path).SaveAsync([NasHome], []);
            Assert.Equal([NasHome], (await new CloudBrowserBookmarkStore(path).LoadAsync()).Favorites);
        });
    }

    [Fact]
    public async Task A_file_written_by_a_later_version_is_not_half_understood()
    {
        await WithFile(async path =>
        {
            const string future = """{"Version":2,"Favorites":[{"Remote":"nas","Path":"team"}],"Recent":[]}""";
            await File.WriteAllTextAsync(path, future);

            var loaded = await new CloudBrowserBookmarkStore(path).LoadAsync();

            // A record this build does not know might carry a field it would silently drop, so the whole file is
            // treated as unreadable rather than read halfway.
            Assert.True(loaded.RecoveredFromCorruption);
            Assert.Empty(loaded.Favorites);
            Assert.Equal(future, await File.ReadAllTextAsync(loaded.BackupPath!));
        });
    }

    [Fact]
    public async Task An_entry_that_could_not_address_a_remote_is_dropped_on_the_way_in()
    {
        await WithFile(async path =>
        {
            await File.WriteAllTextAsync(path,
                """{"Version":1,"Favorites":[{"Remote":"evil/x","Path":"team"},{"Remote":"nas","Path":"team"},"""
                + """{"Remote":"","Path":"team"},{"Remote":"nas","Path":"team"}],"Recent":[{"Remote":"a:b","Path":""}]}""");

            var loaded = await new CloudBrowserBookmarkStore(path).LoadAsync();

            Assert.Equal([NasHome], loaded.Favorites);
            Assert.Empty(loaded.Recent);
            Assert.False(loaded.RecoveredFromCorruption);
        });
    }

    [Fact]
    public async Task A_rename_rewrites_both_lists_in_one_pass_and_writes_nothing_it_did_not_have_to()
    {
        await WithFile(async path =>
        {
            var store = new CloudBrowserBookmarkStore(path);
            await store.SaveAsync([NasHome, FilesHome], [NasDocsPlace]);

            await store.RenameRemoteAsync(Nas, "archive");
            var loaded = await store.LoadAsync();
            Assert.Equal([new CloudBookmark("archive", NasRoot), FilesHome], loaded.Favorites);
            Assert.Equal([new CloudBookmark("archive", NasDocs)], loaded.Recent);

            // A rename that touches neither list leaves the file exactly as it was: a bookmark file rewritten on
            // every rename would be one more thing that can go wrong for no reason.
            var before = await File.ReadAllTextAsync(path);
            await store.RenameRemoteAsync("nothing-here", "still-nothing");
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task A_rename_refuses_to_move_a_file_it_cannot_read()
    {
        await WithFile(async path =>
        {
            const string broken = "{ not json";
            await File.WriteAllTextAsync(path, broken);

            await new CloudBrowserBookmarkStore(path).RenameRemoteAsync(Nas, "archive");

            // Only an explicit read by the window may put an unreadable file aside, and a rename is not that:
            // backing it up here would leave the user's file under a new name and no explanation.
            Assert.Equal(broken, await File.ReadAllTextAsync(path));
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!),
                other => other.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task A_store_with_no_file_yet_reads_as_empty_rather_than_broken()
    {
        await WithFile(async path =>
        {
            var loaded = await new CloudBrowserBookmarkStore(path).LoadAsync();
            Assert.Empty(loaded.Favorites);
            Assert.Empty(loaded.Recent);
            Assert.False(loaded.RecoveredFromCorruption);
            Assert.Null(loaded.BackupPath);
        });
    }

    [Fact]
    public async Task A_write_whose_thread_stopped_pumping_still_frees_the_file_for_the_next_reader()
    {
        await WithFile(async path =>
        {
            // A listing records its visit and the test thread that asked for it is gone before the write lands,
            // which is exactly what a window harness does on every case. A lock that only released on the writer's
            // own dispatcher would then keep every later reader out of a file nobody is writing any more, so the
            // store resumes off the caller's context instead. Measured here on a thread that stops pumping the
            // moment the write has started: the file still arrives, and the next read still gets in.
            var started = new ManualResetEventSlim(false);
            var writer = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                dispatcher.InvokeAsync(() =>
                {
                    _ = new CloudBrowserBookmarkStore(path).SaveAsync([NasHome], [NasDocsPlace]);
                    started.Set();
                    frame.Continue = false;
                });
                Dispatcher.PushFrame(frame);
            })
            { IsBackground = true };
            writer.SetApartmentState(ApartmentState.STA);
            writer.Start();

            Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "那个写入没有开始");
            Assert.True(writer.Join(TimeSpan.FromSeconds(10)), "写入线程没有退出");

            var load = new CloudBrowserBookmarkStore(path).LoadAsync();
            Assert.True(await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10))) == load,
                "一个没人接应的写入把收藏文件锁住了，后来的读取再也进不去");

            var loaded = await load;
            Assert.Equal([NasHome], loaded.Favorites);
            Assert.Equal([NasDocsPlace], loaded.Recent);
        });
    }

    // ------------------------------------------------------------------ the window

    [Fact]
    public async Task A_directory_that_arrived_is_remembered_as_a_visit_and_written_to_disk()
    {
        await WithWindow(async (window, standIn, path) =>
        {
            await window.NavigateToAsync(NasDocs);

            Assert.Equal([NasDocsPlace], window.RecentPlaces);
            Assert.Equal([NasDocsPlace], (await new CloudBrowserBookmarkStore(path).LoadAsync()).Recent);
            Assert.Equal([$"{Nas}:{NasDocs}"], standIn.Listed);
        });
    }

    [Fact]
    public async Task A_directory_that_failed_to_answer_is_not_remembered_as_a_visit()
    {
        await WithWindow(async (window, standIn, path) =>
        {
            await window.NavigateToAsync(NasRoot + "/never-modelled");

            // The browser says what failed, and a directory the cloud refused to list is not somewhere the user can
            // jump back to.
            Assert.Contains("无法读取目录", window.StatusText.Text, StringComparison.Ordinal);
            Assert.Empty(window.RecentPlaces);
            Assert.Empty(window.FavoritePlaces);
            Assert.False(File.Exists(path));
            Assert.Contains($"{Nas}:{NasRoot}/never-modelled", standIn.Listed);
        });
    }

    [Fact]
    public async Task Refreshing_the_directory_already_on_screen_does_not_rewrite_the_file()
    {
        await WithWindow(async (window, standIn, path) =>
        {
            await window.NavigateToAsync(NasDocs);
            Assert.True(File.Exists(path));
            // The clearest way to show that a 刷新 wrote nothing: take the file away and list the same directory
            // again. A rewrite would put it back.
            File.Delete(path);

            await window.NavigateToAsync(NasDocs);
            Assert.False(File.Exists(path));
            Assert.Equal(2, standIn.Listed.Count(entry => entry == $"{Nas}:{NasDocs}"));

            // Walking somewhere new is a visit, and the file comes back with both lists in it.
            await window.NavigateToAsync(NasRoot);
            var loaded = await new CloudBrowserBookmarkStore(path).LoadAsync();
            Assert.Equal([NasHome, NasDocsPlace], loaded.Recent);
        });
    }

    [Fact]
    public async Task Going_back_to_a_place_and_then_forward_to_it_again_records_one_visit()
    {
        await WithWindow(async (window, _, path) =>
        {
            await window.NavigateToAsync(NasDocs);
            await window.NavigateToAsync(NasRoot);
            await window.NavigateToAsync(NasDocs);

            // Newest first, and the repeat moved to the front rather than being filed twice.
            Assert.Equal([NasDocsPlace, NasHome], window.RecentPlaces);
            Assert.Equal([NasDocsPlace, NasHome], (await new CloudBrowserBookmarkStore(path).LoadAsync()).Recent);
        });
    }

    [Fact]
    public async Task The_favourite_button_offers_the_current_directory_and_takes_it_back_again()
    {
        await WithWindow(async (window, _, path) =>
        {
            Assert.Equal("收藏此目录", window.FavoriteToggleLabel);

            await window.ToggleFavoriteAsync();
            Assert.Equal([NasHome], window.FavoritePlaces);
            Assert.Equal("取消收藏", window.FavoriteToggleLabel);
            Assert.Equal([NasHome], (await new CloudBrowserBookmarkStore(path).LoadAsync()).Favorites);

            await window.ToggleFavoriteAsync();
            Assert.Empty(window.FavoritePlaces);
            Assert.Equal("收藏此目录", window.FavoriteToggleLabel);
            Assert.Empty((await new CloudBrowserBookmarkStore(path).LoadAsync()).Favorites);
        });
    }

    [Fact]
    public async Task The_favourite_button_follows_the_directory_the_browser_moved_to()
    {
        await WithWindow(async (window, _, _) =>
        {
            await window.ToggleFavoriteAsync();
            Assert.Equal("取消收藏", window.FavoriteToggleLabel);

            // The button is a claim about the directory on screen, so it has to be re-decided every time that
            // directory changes: a user who favourites a folder and then walks away must not be offered the way
            // back by a button that still says 取消收藏 for the place they left.
            await window.NavigateToAsync(NasDocs);
            Assert.Equal("收藏此目录", window.FavoriteToggleLabel);
            await window.NavigateToAsync(NasRoot);
            Assert.Equal("取消收藏", window.FavoriteToggleLabel);
        });
    }

    [Fact]
    public async Task The_favourite_button_has_nothing_to_hold_while_no_endpoint_is_on_screen()
    {
        await WithWindow((window, _, _) =>
        {
            // The endpoint list's own search box: a filter that matches nothing empties the browser, and a button
            // that would favourite "nothing" must not be there to click.
            window.EndpointSearchBox.Text = "nothing-matches-this";

            Assert.Empty(window.VisibleEndpoints);
            Assert.Equal("", window.CurrentPath);
            Assert.False(window.FavoriteToggleButton.IsEnabled);
            Assert.Empty(window.FavoriteJumps);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_favourite_whose_endpoint_went_away_is_hidden_and_counted_rather_than_deleted()
    {
        await WithWindow(async (window, _, _) =>
        {
            await window.ToggleFavoriteAsync();                                // nas:team
            await window.JumpToAsync(new CloudJumpItem("kept", FilesInboxPlace));
            await window.ToggleFavoriteAsync();                                // files:docs/inbox
            Assert.Equal(2, window.FavoritePlaces.Count);

            window.LoadEndpointsForTest([Row(Nas, NasRoot)], selectName: Nas);

            Assert.Single(window.FavoriteJumps);
            Assert.Equal(NasHome, window.FavoriteJumps[0].Bookmark);
            // The count is the whole point of the sentence: six rows becoming one is otherwise
            // indistinguishable from a list that lost five of them for good.
            Assert.Equal(2, window.FavoritePlaces.Count);
            Assert.Contains("已隐藏 1 条收藏", window.FavoriteJumpBox.ToolTip as string ?? "", StringComparison.Ordinal);

            window.LoadEndpointsForTest([Row(Nas, NasRoot), Row(Files, FilesRoot)], selectName: Files);

            Assert.Equal(2, window.FavoriteJumps.Count);
            Assert.Null(window.FavoriteJumpBox.ToolTip);
        });
    }

    [Fact]
    public async Task Jumping_across_endpoints_lists_the_remembered_directory_and_nothing_else()
    {
        await WithWindow(async (window, standIn, _) =>
        {
            Assert.True(await window.JumpToAsync(new CloudJumpItem("kept", FilesInboxPlace)));

            // One listing for the destination. Adopting the endpoint must not also fetch its root, or two answers
            // race for the file pane and whichever arrives last is the directory the user ends up looking at.
            Assert.Equal([$"{Files}:{FilesInbox}"], standIn.Listed);
            Assert.Equal(FilesInbox, window.CurrentPath);
            Assert.Equal(Files, SelectedName(window));
            Assert.Equal([FilesInboxPlace], window.RecentPlaces);
            Assert.Equal($"{Files}  ·  /{FilesInbox}", window.RecentJumps[0].Label);
        });
    }

    [Fact]
    public async Task A_pick_from_a_jump_list_is_consumed_so_the_same_place_can_be_picked_again()
    {
        await WithWindow(async (window, standIn, _) =>
        {
            await window.ToggleFavoriteAsync();

            // Through the real control: the ComboBox's own selection event is what a pick goes through, and a box
            // that kept the row selected would read as a filter and offer nothing to pick twice.
            window.FavoriteJumpBox.SelectedItem = window.FavoriteJumps.Single();
            await WaitUntil(() => window.FavoriteJumpBox.SelectedItem is null
                && standIn.Listed.Count == 1, "the jump list kept the pick");

            await window.NavigateToAsync(NasDocs);
            window.FavoriteJumpBox.SelectedItem = window.FavoriteJumps.Single();
            // Waited for on the pick being consumed and the destination having answered, not on the current path:
            // the path changes the moment the jump starts, which would read a pick still in flight as finished.
            await WaitUntil(() => window.FavoriteJumpBox.SelectedItem is null
                && standIn.Listed.Count(entry => entry == $"{Nas}:{NasRoot}") == 2,
                "the same place could not be jumped to twice");

            Assert.Equal(2, standIn.Listed.Count(entry => entry == $"{Nas}:{NasRoot}"));
            Assert.Equal(NasRoot, window.CurrentPath);
            Assert.Null(window.FavoriteJumpBox.SelectedItem);
        });
    }

    [Fact]
    public async Task A_pick_from_the_address_bar_history_is_consumed_so_the_same_place_can_be_picked_again()
    {
        await WithWindow(async (window, standIn, _) =>
        {
            // Walking somewhere records the visit, and the address bar's dropdown is the only place 最近访问 is
            // offered now — so the list is filled by the listing itself rather than by anything a test set up.
            await window.NavigateToAsync(NasDocs);
            Assert.Equal($"{Nas}  ·  /{NasDocs}", window.RecentJumps.Single().Label);

            // Through the real control: the ListBox's own selection event is what a pick goes through, and a row
            // left selected would read as a filter and offer nothing to pick twice.
            window.AddressHistoryList.SelectedItem = window.RecentJumps.Single();
            await WaitUntil(() => window.AddressHistoryList.SelectedItem is null
                && standIn.Listed.Count(entry => entry == $"{Nas}:{NasDocs}") == 2, "the address history kept the pick");

            await window.NavigateToAsync(NasRoot);
            window.AddressHistoryList.SelectedItem = window.RecentJumps.Single(row => row.Bookmark == NasDocsPlace);
            await WaitUntil(() => window.AddressHistoryList.SelectedItem is null
                && window.CurrentPath == NasDocs, "the same place could not be jumped to twice");

            Assert.Equal(NasDocs, window.CurrentPath);
            Assert.Null(window.AddressHistoryList.SelectedItem);
        });
    }

    [Fact]
    public async Task A_jump_refuses_a_place_whose_endpoint_went_away_after_the_list_was_built()
    {
        await WithWindow(async (window, standIn, _) =>
        {
            await window.ToggleFavoriteAsync();
            window.LoadEndpointsForTest([Row(Files, FilesRoot)], selectName: Files);
            var path = window.CurrentPath;

            var jumped = await window.JumpToAsync(new CloudJumpItem("kept", NasHome));

            Assert.False(jumped);
            Assert.Contains("已不在列表里", window.StatusText.Text, StringComparison.Ordinal);
            // Refusing is also not a listing: the row that was clicked addresses an endpoint this window no longer
            // has, so there is nothing to ask the cloud about and nowhere new to move the file pane to.
            Assert.Empty(standIn.Listed);
            Assert.Equal(path, window.CurrentPath);
            Assert.Equal(Files, SelectedName(window));
        });
    }

    [Fact]
    public async Task The_remembered_places_come_back_through_a_second_window()
    {
        await WithWindow(async (window, _, path) =>
        {
            await window.ToggleFavoriteAsync();                     // nas:team, the directory on screen
            await window.NavigateToAsync(NasDocs);                  // a visit the user never chose to keep

            var reopened = await OpenWindow(Path.GetDirectoryName(path)!);
            try
            {
                Assert.Equal([NasHome], reopened.FavoritePlaces);
                Assert.Equal([NasDocsPlace], reopened.RecentPlaces);
                Assert.Equal([NasHome], reopened.FavoriteJumps.Select(row => row.Bookmark));
                // The reopened window is standing in the same directory, and its button knows it is a favourite:
                // what came back off the file is the state the UI shows, not only the data behind it.
                Assert.Equal("取消收藏", reopened.FavoriteToggleLabel);
                Assert.Null(reopened.BookmarkNotice);
            }
            finally { reopened.Close(); }
        });
    }

    [Fact]
    public async Task A_window_that_could_not_read_the_file_says_so_and_starts_from_empty_lists()
    {
        await WithWindow(async (window, _, path) =>
        {
            await window.ToggleFavoriteAsync();
            window.Close();

            // The bytes the user kept are put aside rather than overwritten, and the window that could not read
            // them has to say so: an empty 收藏 nobody chose looks exactly like an empty 收藏 nobody filled.
            var reopened = await OpenWindow(Path.GetDirectoryName(path)!, beforeLoad: _ =>
                File.WriteAllText(path, "{ this is not the bookmark file"));

            Assert.Empty(reopened.FavoritePlaces);
            Assert.Contains("收藏文件读不出来", reopened.BookmarkNotice ?? "", StringComparison.Ordinal);
            Assert.Contains(".bak", reopened.BookmarkNotice ?? "", StringComparison.Ordinal);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*.bak"));
            reopened.Close();
        });
    }

    // ------------------------------------------------------------------ harness

    private static Task WithWindow(Func<RemoteEndpointManagerWindow, FakeRclone, string, Task> test) =>
        RunSta(async () =>
        {
            var dataRoot = NewDirectory();
            var previous = AppServices.TryCurrent;
            // The browser writes its 收藏 / 最近访问 file under the application's data root, so both the window and
            // the store a test reads back have to be pointed at one temporary directory.
            var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
            RemoteEndpointManagerWindow? window = null;
            try
            {
                var (built, standIn) = await OpenWindowWithFake(dataRoot);
                window = built;
                await test(window, standIn, window.Bookmarks.FilePath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
                AppServices.Install(previous);
                // A test may close the window itself, and WPF rejects closing a closed window.
                try { window?.Close(); }
                catch (InvalidOperationException) { }
                TryDelete(dataRoot);
            }
        });

    /// <summary>
    /// Builds one browser window over a stand-in rclone and loads the remembered places from the data root the
    /// caller names. The window is never shown, so its <c>Loaded</c> handler — which would read the application's
    /// own settings — is not reached, and the case drives <see cref="RemoteEndpointManagerWindow.LoadBookmarksAsync"/>
    /// the way a shown window's handler does. The directory on screen starts as the selected endpoint's root, which
    /// is what a window that has just finished its first listing looks like; nothing is recorded as a visit by
    /// getting there, because <see cref="RemoteEndpointManagerWindow.ShowListing"/> is the pane's assembly, not a
    /// round trip.
    /// </summary>
    private static async Task<(RemoteEndpointManagerWindow Window, FakeRclone Rclone)> OpenWindowWithFake(
        string dataRoot, Action<RemoteEndpointManagerWindow>? beforeLoad = null)
    {
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
        var standIn = new FakeRclone
        {
            Directories = Tree(
                new Node(NasRoot + "/a.txt", false), new Node(NasDocs, true), new Node(NasDocs + "/b.txt", false),
                new Node(FilesRoot + "/c.txt", false), new Node(FilesInbox, true), new Node(FilesInbox + "/d.txt", false))
        };
        var http = new HttpClient(standIn) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, new Uri("http://rc.test/"), "user", "pass");
        var queue = new CloudTransferQueue(new CloudTransferJournalStore(Path.Combine(dataRoot, "jobs")),
            _ => Task.FromResult(client));
        // The window resolves the mount service from the installed shell, and the rclone behind that shell is
        // unreachable on purpose: a test that reaches it fails loudly instead of starting a daemon.
        AppServices.Install(new StubShell(queue));
        var window = new RemoteEndpointManagerWindow(new CloudFileManagerService(_ => Task.FromResult(client)), queue);
        window.LoadEndpointsForTest([Row(Nas, NasRoot), Row(Files, FilesRoot)], selectName: Nas);
        window.ShowListing(NasRoot, [FileEntry(NasRoot + "/a.txt"), FolderEntry(NasDocs)]);
        beforeLoad?.Invoke(window);
        await window.LoadBookmarksAsync();
        Assert.Equal(Path.Combine(dataRoot, "cloud-bookmarks.json"), window.Bookmarks.FilePath);
        return (window, standIn);
    }

    /// <summary>A second window over the same data root and the same modelled cloud, for the cases about what
    /// survives a close.</summary>
    private static async Task<RemoteEndpointManagerWindow> OpenWindow(string dataRoot,
        Action<RemoteEndpointManagerWindow>? beforeLoad = null) =>
        (await OpenWindowWithFake(dataRoot, beforeLoad)).Window;

    private static CloudEndpointRow Row(string name, string root) =>
        new(new CloudEndpointAccount(new RcloneAccount(name, "sftp"),
            new CloudEndpointMetadata(name, "sftp", "SFTP", root, "")));

    private static CloudFileEntry FileEntry(string path) =>
        new(path.Split('/').Last(), path, false, 4096, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

    private static CloudFileEntry FolderEntry(string path) =>
        new(path.Split('/').Last(), path, true, 0, null);

    private static string SelectedName(RemoteEndpointManagerWindow window) =>
        ((CloudEndpointRow)window.EndpointList.SelectedItem!).Account.Name;

    /// <summary>
    /// The tree a stand-in models, written as the paths of the nodes it holds and grouped by the directory each one
    /// sits in. A directory not present here does not exist, which is the state a jump to a deleted folder meets.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<Node>> Tree(params Node[] nodes)
    {
        var directories = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var directory = CloudFileManagerService.Parent(node.Path);
            if (!directories.TryGetValue(directory, out var held)) directories[directory] = held = [];
            held.Add(node);
        }

        return directories.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<Node>)entry.Value);
    }

    private static async Task WithFile(Func<string, Task> test)
    {
        var directory = NewDirectory();
        try
        {
            await test(Path.Combine(directory, "cloud-bookmarks.json"));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>Polls until the window has finished the jump, pumping the dispatcher while it waits.</summary>
    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"超时：{message}");
            await Task.Delay(25);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-bookmark-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Windows can hold a directory a few milliseconds after its last handle closes.</summary>
    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Runs one action against a window on its own STA thread, with the dispatcher pumped while the action is in
    /// flight. The dispatcher is then shut down explicitly, because the process allows only one WPF
    /// <c>Application</c>. A run that stops making progress is cut off by a watchdog.
    /// </summary>
    private static Task RunSta(Func<Task> action)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Exception? failure = null;
            Task? run = null;
            var ownsApplication = MergeApplicationThemes();
            try
            {
                run = action();
                _ = run.ContinueWith(completed =>
                {
                    failure = completed.IsFaulted ? completed.Exception!.GetBaseException() : null;
                    frame.Continue = false;
                }, CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception exception)
            {
                failure = exception;
                frame.Continue = false;
            }
            var watchdog = new System.Threading.Timer(
                _ => dispatcher.BeginInvoke(() => frame.Continue = false),
                null, TimeSpan.FromSeconds(90), System.Threading.Timeout.InfiniteTimeSpan);
            Dispatcher.PushFrame(frame);
            watchdog.Dispose();
            if (failure is null && run is { IsCompleted: false })
                failure = new TimeoutException("云端收藏测试没有在规定时间内完成（窗口线程被卡住了）。");
            if (ownsApplication)
                try { Application.Current?.Shutdown(); }
                catch (InvalidOperationException) { }
            dispatcher.InvokeShutdown();
            finished.TrySetResult(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return Task.Run(async () =>
        {
            var exception = await finished.Task;
            thread.Join(TimeSpan.FromSeconds(30));
            if (exception is not null) ExceptionDispatchInfo.Capture(exception).Throw();
        });
    }

    /// <summary>
    /// Gives this thread an Application to resolve a window's theme dictionaries through, and says whether the
    /// thread also owns it.
    /// </summary>
    private static bool MergeApplicationThemes()
    {
        var ownsApplication = Application.Current is null;
        if (ownsApplication) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var application = Application.Current!;
        if (application.Resources.MergedDictionaries.Count == 0)
            foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
                });
        return ownsApplication;
    }

    /// <summary>Just enough of a shell to build the window; nothing here owns a daemon or a journal.</summary>
    private sealed class StubShell : IAppServiceHost
    {
        private readonly RcloneLifecycleHost _rclone = new();

        public StubShell(CloudTransferQueue transfers)
        {
            CloudTransfers = transfers;
            MountService = new RcloneMountService(new UnreachableRclone());
        }

        public RcloneLifecycleHost RcloneHost => _rclone;
        public IRcloneLifecycleClient RcloneLifecycle => _rclone;
        public RcloneMountService MountService { get; }
        public CloudTransferQueue CloudTransfers { get; }

        private sealed class UnreachableRclone : IRcloneLifecycleClient
        {
            public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException($"这个测试不调用 rclone（{operation}）。");
        }
    }

    /// <summary>One name in the modelled tree, and whether the cloud reports it as a directory.</summary>
    private sealed record Node(string Path, bool IsDirectory);

    /// <summary>
    /// A stand-in for rclone's RC endpoint that answers <c>operations/list</c> from the modelled tree the way the
    /// bundled v1.75.0 was measured to — a directory the model does not hold is a 404 "directory not found", an
    /// existing one is the entries it holds — and records every directory it was asked for, tagged with the
    /// endpoint the call named. The record is the point: the claim under test is <em>which</em> directories a jump
    /// fetches, and a fake that forgot to say would let two listings pass as one.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _listed = [];

        public IReadOnlyDictionary<string, IReadOnlyList<Node>> Directories { get; init; } =
            new Dictionary<string, IReadOnlyList<Node>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each <c>operations/list</c> as <c>fs</c> plus the directory it named, in the order it arrived.</summary>
        public IReadOnlyList<string> Listed
        {
            get { lock (_gate) return _listed.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            using var document = body.Length == 0 ? null : JsonDocument.Parse(body);
            var root = document?.RootElement ?? default;
            string answer;
            HttpStatusCode status = HttpStatusCode.OK;
            if (operation == "operations/list")
            {
                var directory = Read(root, "remote").Trim('/');
                lock (_gate) _listed.Add(Read(root, "fs") + directory);
                if (Directories.TryGetValue(directory, out var nodes))
                    answer = "{\"list\":[" + string.Join(",", nodes.Select(Item)) + "]}";
                else
                {
                    answer = """{"error":"sftp: directory not found","input":{},"path":"","status":404}""";
                    status = HttpStatusCode.NotFound;
                }
            }
            else
            {
                answer = "{}";
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(answer, Encoding.UTF8,
                    status == HttpStatusCode.OK ? "application/json" : "text/plain")
            };
        }

        /// <summary>One node in the shape v1.75.0 answers <c>operations/list</c> with: a path relative to the
        /// remote root, the same shape the browser puts in <see cref="CloudFileEntry.Path"/>.</summary>
        private static string Item(Node node) =>
            "{\"Path\":\"" + node.Path + "\",\"Name\":\"" + CloudFileManagerService.NameOf(node.Path)
            + "\",\"IsDir\":" + (node.IsDirectory ? "true" : "false")
            + ",\"Size\":" + (node.IsDirectory ? 0 : 4096)
            + ",\"ModTime\":\"2026-03-01T08:00:00Z\"}";

        private static string Read(JsonElement root, string name) =>
            root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
                ? ""
                : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }
}
