using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VupPlayer.Bilibili;
using VupPlayer.Core;

namespace VupPlayer.App.ViewModels;

/// <summary>直播：订阅列表、观看、开播提醒开关。</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<LiveRoom> LiveRooms { get; } = [];

    [ObservableProperty] public partial string? LiveStatusText { get; set; }
    [ObservableProperty] public partial bool IsCheckingLive { get; set; }

    /// <summary>开始看直播，由 App 提供（要用播放器）。</summary>
    public Func<long, Task>? LiveWatcher { get; set; }
    /// <summary>立即查一轮开播状态，由 App 提供。</summary>
    public Func<Task>? LivePoller { get; set; }
    /// <summary>直播订阅与取流，由 App 提供。</summary>
    public LiveService? LiveApi { get; set; }

    /// <summary>当前选中的是直播页。</summary>
    public bool IsLiveList => SelectedNav?.Kind == NavKind.Live;

    public int LiveCount => LiveRooms.Count(r => r.IsLive);

    /// <summary>重新读取直播订阅列表并刷新导航上的在播人数。</summary>
    public void RefreshLive()
    {
        LiveRooms.Clear();
        foreach (var r in _repo.ListLiveRooms()) LiveRooms.Add(r);
        OnPropertyChanged(nameof(LiveCount));
        foreach (var n in NavItems.Where(n => n.Kind == NavKind.Live)) n.Count = LiveCount;
        if (IsLiveList) UpdateLiveSubtitle();
    }

    void UpdateLiveSubtitle()
    {
        var live = LiveCount;
        ListTitle = "直播";
        ListSubtitle = LiveRooms.Count == 0
            ? "还没有订阅的直播间。粘贴直播链接（live.bilibili.com/…）可以添加，或者点右上角从曲库导入主播"
            : live > 0
                ? $"{live} 位主播正在直播 · 双击观看 · 共订阅 {LiveRooms.Count} 个直播间"
                : $"现在没人在播 · 共订阅 {LiveRooms.Count} 个直播间";
        ListActionText = "从曲库导入主播";
    }

    /// <summary>双击 / 「播放全部」时：看在播的第一个。</summary>
    public async Task WatchLiveAsync(LiveRoom? room)
    {
        var target = room ?? LiveRooms.FirstOrDefault(r => r.IsLive);
        if (target is null)
        {
            LiveStatusText = "现在没有人在直播";
            return;
        }
        if (!target.IsLive)
        {
            LiveStatusText = target.LastStatus == LiveStatus.Replay
                ? $"{target.DisplayName} 在轮播（放录像），没有直播流"
                : $"{target.DisplayName} 没有在直播";
            return;
        }
        if (target.RoomId <= 0)
        {
            LiveStatusText = "还不知道这位主播的房间号，先点一下「立即刷新」";
            return;
        }
        LiveStatusText = null;
        if (LiveWatcher is not null) await LiveWatcher(target.RoomId);
    }

    [RelayCommand]
    async Task CheckLive()
    {
        if (IsCheckingLive || LivePoller is null) return;
        IsCheckingLive = true;
        LiveStatusText = "正在查开播状态…";
        try
        {
            await LivePoller();
            RefreshLive();
            LiveStatusText = LiveCount > 0 ? $"{LiveCount} 位主播正在直播" : "现在没人在播";
        }
        catch (Exception ex)
        {
            LiveStatusText = "查开播状态失败：" + ex.Message;
        }
        finally
        {
            IsCheckingLive = false;
        }
    }

    /// <summary>把曲库里有 UID 的主播补进直播订阅。</summary>
    public async Task ImportStreamersToLiveAsync()
    {
        if (LiveApi is null) return;
        var n = LiveApi.SyncFromStreamers();
        RefreshLive();
        LiveStatusText = n > 0 ? $"从曲库导入 {n} 位主播，正在查开播状态…" : "曲库里有 UID 的主播都已经订阅了";
        if (n > 0 && LivePoller is not null)
        {
            await LivePoller();
            RefreshLive();
        }
    }

    /// <summary>粘贴直播链接时添加订阅。返回是否处理了这条输入。</summary>
    public async Task<bool> TryAddLiveAsync(ParsedLink link)
    {
        if (link.Kind != LinkKind.Live || link.RoomId is not long roomId || LiveApi is null) return false;
        ImportStatus = $"正在查直播间 {roomId}…";
        try
        {
            var room = await Task.Run(() => LiveApi.AddByRoomIdAsync(roomId));
            if (room is null)
            {
                ImportStatus = $"找不到直播间 {roomId}";
                return true;
            }
            RefreshLive();
            RebuildNav();
            ImportStatus = $"已添加直播间：{room.DisplayName}"
                           + (room.IsLive ? "（正在直播，双击可以观看）" : "，开播时会通知你");
        }
        catch (Exception ex)
        {
            ImportStatus = "添加直播间失败：" + ex.Message;
        }
        return true;
    }

    public void ToggleLiveNotify(LiveRoom room)
    {
        _repo.SetLiveNotify(room.Mid, !room.Notify);
        RefreshLive();
        LiveStatusText = room.Notify ? $"已关掉 {room.DisplayName} 的开播提醒" : $"已打开 {room.DisplayName} 的开播提醒";
    }

    public void RemoveLiveRoom(LiveRoom room)
    {
        _repo.DeleteLiveRoom(room.Mid);
        RefreshLive();
        RebuildNav();
        LiveStatusText = $"已移除 {room.DisplayName}";
    }
}
