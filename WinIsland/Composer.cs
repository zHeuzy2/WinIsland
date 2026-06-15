using System.Text;

namespace WinIsland;

/// <summary>
/// State for the inline task composer drawn inside the Tasks view. The island
/// can't take keyboard focus (WS_EX_NOACTIVATE), so while <see cref="Active"/> a
/// low-level keyboard hook feeds keystrokes here. All access happens on the UI
/// thread (the hook callback and the render loop share that thread), so no
/// locking is required.
/// </summary>
public sealed class Composer
{
    public bool Active;

    /// <summary>Index of the task being edited, or -1 when composing a new one.</summary>
    public int EditIndex = -1;

    private readonly StringBuilder _text = new();
    public int Caret;

    // Due date/time. When HasDate is false the task has no deadline.
    public bool HasDate;
    public DateTime Date = DateTime.Today;
    public bool HasTime;
    public int Hour = 9;
    public int Minute = 0;

    public string Text => _text.ToString();

    public void BeginNew()
    {
        Active = true;
        EditIndex = -1;
        _text.Clear();
        Caret = 0;
        HasDate = false;
        Date = DateTime.Today;
        HasTime = false;
        Hour = 9;
        Minute = 0;
    }

    public void BeginEdit(int index, TaskItem item)
    {
        Active = true;
        EditIndex = index;
        _text.Clear();
        _text.Append(item.Text);
        Caret = _text.Length;
        HasDate = item.DueAt.HasValue;
        Date = (item.DueAt ?? DateTime.Today).Date;
        HasTime = item.HasTime;
        Hour = item.DueAt?.Hour ?? 9;
        Minute = item.DueAt?.Minute ?? 0;
    }

    public void Close()
    {
        Active = false;
        EditIndex = -1;
        _text.Clear();
        Caret = 0;
    }

    /// <summary>Builds the DueAt value from the current date/time selection.</summary>
    public DateTime? BuildDueAt()
    {
        if (!HasDate) return null;
        return HasTime ? Date.Date.AddHours(Hour).AddMinutes(Minute) : Date.Date;
    }

    // ---- text editing ----
    public void Insert(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        // Keep it single-line and bounded.
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        if (_text.Length + s.Length > 200) s = s[..Math.Max(0, 200 - _text.Length)];
        if (s.Length == 0) return;
        _text.Insert(Caret, s);
        Caret += s.Length;
    }

    public void Backspace()
    {
        if (Caret <= 0 || _text.Length == 0) return;
        _text.Remove(Caret - 1, 1);
        Caret--;
    }

    public void DeleteForward()
    {
        if (Caret >= _text.Length) return;
        _text.Remove(Caret, 1);
    }

    public void MoveCaret(int delta) => Caret = Math.Clamp(Caret + delta, 0, _text.Length);
    public void CaretHome() => Caret = 0;
    public void CaretEnd() => Caret = _text.Length;

    // ---- date/time adjustments ----
    public void EnableDate(DateTime d) { HasDate = true; Date = d.Date; }
    public void ClearDate() { HasDate = false; HasTime = false; }

    public void AdjustDate(int days)
    {
        if (!HasDate) { HasDate = true; Date = DateTime.Today; }
        Date = Date.AddDays(days).Date;
    }

    public void ToggleTime()
    {
        if (!HasDate) { HasDate = true; Date = DateTime.Today; }
        HasTime = !HasTime;
    }

    public void AdjustHour(int delta)
    {
        EnsureTime();
        Hour = ((Hour + delta) % 24 + 24) % 24;
    }

    public void AdjustMinute(int delta)
    {
        EnsureTime();
        int total = Hour * 60 + Minute + delta;
        total = (total % 1440 + 1440) % 1440;
        Hour = total / 60;
        Minute = total % 60;
    }

    private void EnsureTime()
    {
        if (!HasDate) { HasDate = true; Date = DateTime.Today; }
        HasTime = true;
    }
}
