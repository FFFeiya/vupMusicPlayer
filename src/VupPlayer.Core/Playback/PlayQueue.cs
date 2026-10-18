namespace VupPlayer.Core.Playback;

public enum PlayMode
{
    /// <summary>顺序播放，播完即停。</summary>
    Sequential,
    /// <summary>列表循环。</summary>
    RepeatAll,
    /// <summary>单曲循环（自动播完时重复；手动下一首仍会前进）。</summary>
    RepeatOne,
    /// <summary>洗牌：一轮内不重复，播完一轮重新洗。</summary>
    Shuffle,
    /// <summary>智能推荐：每次由推荐器挑下一首（范围可以是整个曲库，甚至 B 站发现池）。</summary>
    Random,
}

public static class PlayModeExtensions
{
    public static string DisplayName(this PlayMode m) => m switch
    {
        PlayMode.Sequential => "顺序播放",
        PlayMode.RepeatAll => "列表循环",
        PlayMode.RepeatOne => "单曲循环",
        PlayMode.Shuffle => "洗牌播放",
        PlayMode.Random => "智能推荐",
        _ => m.ToString(),
    };

    public static PlayMode NextMode(this PlayMode m) => (PlayMode)(((int)m + 1) % 5);
}

/// <summary>
/// 播放队列。只保存曲目 id，不关心播放本身。
/// Random 模式下的「下一首」由外部推荐器决定，然后调用 <see cref="Jump"/>。
/// </summary>
public sealed class PlayQueue
{
    readonly List<long> _items = [];
    List<int> _order = [];          // 播放顺序：_items 的下标
    int _pos = -1;                  // 在 _order 中的位置
    readonly List<long> _history = [];
    readonly Random _rng;
    PlayMode _mode = PlayMode.RepeatAll;

    public PlayQueue(Random? rng = null) => _rng = rng ?? Random.Shared;

    public IReadOnlyList<long> Items => _items;
    public IReadOnlyList<long> History => _history;
    public int Count => _items.Count;
    public long? Current => _pos >= 0 && _pos < _order.Count ? _items[_order[_pos]] : null;

    /// <summary>队列来源描述，比如「全部曲目」「歌单：xxx」，写进播放记录。</summary>
    public string? Context { get; set; }

    public event Action? Changed;

    public PlayMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            var current = Current;
            _mode = value;
            RebuildOrder(current);
            Changed?.Invoke();
        }
    }

    /// <summary>Random 模式需要外部推荐器给出下一首。</summary>
    public bool NeedsRecommender => _mode == PlayMode.Random;

    /// <summary>用新列表替换队列，并定位到 startId（为空时定位到第一首，洗牌模式下随机）。</summary>
    public void Load(IEnumerable<long> trackIds, long? startId = null, string? context = null)
    {
        _items.Clear();
        _items.AddRange(trackIds.Distinct());
        Context = context;
        if (_items.Count == 0) { _order = []; _pos = -1; Changed?.Invoke(); return; }
        var start = startId is long s && _items.Contains(s) ? s
            : _mode is PlayMode.Shuffle or PlayMode.Random ? _items[_rng.Next(_items.Count)] : _items[0];
        RebuildOrder(start);
        Push(start);
        Changed?.Invoke();
    }

    /// <summary>直接跳到某首（不在队列里时追加到末尾）。</summary>
    public void Jump(long trackId)
    {
        if (!_items.Contains(trackId))
        {
            _items.Add(trackId);
            if (_mode == PlayMode.Shuffle) _order.Insert(Math.Min(_pos + 1, _order.Count), _items.Count - 1);
            else _order.Add(_items.Count - 1);
        }
        var idx = _items.IndexOf(trackId);
        var p = _order.IndexOf(idx);
        if (_mode == PlayMode.Shuffle && p != _pos)
        {
            // 洗牌模式下点播：把它挪到当前位置之后，保证本轮剩余曲目的顺序不受影响
            _order.RemoveAt(p);
            if (p < _pos) _pos--;
            _order.Insert(_pos + 1, idx);
            p = _pos + 1;
        }
        _pos = p;
        Push(trackId);
        Changed?.Invoke();
    }

    /// <summary>
    /// 不改变状态，预测下一首（用于预取）。Random 模式返回 null。
    /// </summary>
    public long? PeekNext(bool auto = true)
    {
        if (_items.Count == 0 || NeedsRecommender) return null;
        if (auto && _mode == PlayMode.RepeatOne) return Current;
        var next = _pos + 1;
        if (next < _order.Count) return _items[_order[next]];
        return _mode switch
        {
            PlayMode.Sequential => null,
            PlayMode.Shuffle => null,   // 新一轮的顺序未定
            _ => _items[_order[0]],
        };
    }

    /// <summary>前进到下一首并返回它；到末尾且不循环时返回 null。Random 模式不要调用此方法。</summary>
    public long? Next(bool auto)
    {
        if (_items.Count == 0) return null;
        if (NeedsRecommender) throw new InvalidOperationException("Random 模式请使用推荐器后调用 Jump");
        if (auto && _mode == PlayMode.RepeatOne && Current is long cur)
        {
            Push(cur);
            return cur;
        }
        if (_pos + 1 < _order.Count)
        {
            _pos++;
        }
        else if (_mode == PlayMode.Sequential && auto)
        {
            return null;
        }
        else if (_mode == PlayMode.Shuffle)
        {
            var last = Current;
            _order = ShuffledOrder();
            // 避免新一轮第一首和上一轮最后一首相同
            if (_order.Count > 1 && last is long l && _items[_order[0]] == l)
                (_order[0], _order[1]) = (_order[1], _order[0]);
            _pos = 0;
        }
        else
        {
            _pos = 0;
        }
        var id = _items[_order[_pos]];
        Push(id);
        Changed?.Invoke();
        return id;
    }

    /// <summary>上一首：Random 模式按播放历史回退，其余按队列顺序回退。</summary>
    public long? Previous()
    {
        if (_items.Count == 0) return null;
        if (NeedsRecommender)
        {
            if (_history.Count < 2) return Current;
            _history.RemoveAt(_history.Count - 1);
            var prev = _history[^1];
            _pos = _order.IndexOf(_items.IndexOf(prev));
            Changed?.Invoke();
            return prev;
        }
        if (_pos > 0) _pos--;
        else if (_mode is PlayMode.RepeatAll or PlayMode.RepeatOne) _pos = _order.Count - 1;
        else return Current;
        var id = _items[_order[_pos]];
        Push(id);
        Changed?.Invoke();
        return id;
    }

    /// <summary>插到当前曲目之后。</summary>
    public void PlayNext(IEnumerable<long> trackIds)
    {
        var insertAt = _pos + 1;
        foreach (var id in trackIds)
        {
            var idx = _items.IndexOf(id);
            if (idx < 0) { _items.Add(id); idx = _items.Count - 1; }
            else
            {
                var p = _order.IndexOf(idx);
                if (p == _pos) continue;
                _order.RemoveAt(p);
                if (p < insertAt) { insertAt--; _pos--; }
            }
            _order.Insert(Math.Max(insertAt, 0), idx);
            insertAt++;
        }
        Changed?.Invoke();
    }

    /// <summary>追加到队尾。</summary>
    public void Enqueue(IEnumerable<long> trackIds)
    {
        foreach (var id in trackIds)
        {
            if (_items.Contains(id)) continue;
            _items.Add(id);
            _order.Add(_items.Count - 1);
        }
        Changed?.Invoke();
    }

    public void Remove(long trackId)
    {
        var idx = _items.IndexOf(trackId);
        if (idx < 0) return;
        var p = _order.IndexOf(idx);
        _order.RemoveAt(p);
        if (p < _pos) _pos--;
        else if (p == _pos) _pos--;   // 当前曲目被移除：下一次 Next 会播放它后面的那首
        _items.RemoveAt(idx);
        for (var i = 0; i < _order.Count; i++) if (_order[i] > idx) _order[i]--;
        Changed?.Invoke();
    }

    /// <summary>按播放顺序列出队列（从当前曲目开始往后）。</summary>
    public IReadOnlyList<long> Upcoming(int max = 50)
    {
        var list = new List<long>();
        for (var i = Math.Max(_pos, 0); i < _order.Count && list.Count < max; i++) list.Add(_items[_order[i]]);
        return list;
    }

    void Push(long id)
    {
        _history.Add(id);
        if (_history.Count > 500) _history.RemoveRange(0, _history.Count - 500);
    }

    void RebuildOrder(long? current)
    {
        if (_items.Count == 0) { _order = []; _pos = -1; return; }
        if (_mode == PlayMode.Shuffle)
        {
            _order = ShuffledOrder();
            if (current is long c)
            {
                var idx = _items.IndexOf(c);
                _order.Remove(idx);
                _order.Insert(0, idx);
                _pos = 0;
            }
            else _pos = -1;
        }
        else
        {
            _order = Enumerable.Range(0, _items.Count).ToList();
            _pos = current is long c ? _items.IndexOf(c) : -1;
        }
    }

    List<int> ShuffledOrder()
    {
        var order = Enumerable.Range(0, _items.Count).ToList();
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }
}
