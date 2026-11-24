using System;
using System.Collections.Generic;

namespace PixelStacking.Data
{
    /// <summary>一筆可復原的操作：<see cref="Apply"/> 把狀態還原；之後 UI 依 Frame / View / Slice 切到被影響的位置。</summary>
    public sealed class UndoEntry
    {
        public Action Apply;

        /// <summary>還原後要切過去的那一幀（沒有就 null）。</summary>
        public VoxelModel Frame;

        /// <summary>還原後要切過去的視角與切片（筆畫才有；null = 不切）。</summary>
        public ViewDirection? View;
        public int Slice = -1;
    }

    /// <summary>復原堆疊：筆畫（還原一層的快照）與幀的新增/刪除/搬移共用，依發生順序反向還原。</summary>
    public sealed class UndoStack
    {
        private readonly List<UndoEntry> _entries = new List<UndoEntry>();

        public int MaxCount { get; set; } = 200;
        public int Count => _entries.Count;

        public void Push(UndoEntry entry)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxCount) { _entries.RemoveAt(0); }
        }

        public bool TryPop(out UndoEntry entry)
        {
            if (_entries.Count == 0) { entry = null; return false; }

            entry = _entries[_entries.Count - 1];
            _entries.RemoveAt(_entries.Count - 1);
            return true;
        }

        public void Clear()
        {
            _entries.Clear();
        }
    }
}
