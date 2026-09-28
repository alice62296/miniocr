using System.Text;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Groups OCR pages into LLM NER requests.
/// Blank pages (null/whitespace text) are skipped and never form a request.
/// Each request holds up to <c>pagesPerRequest</c> non-empty pages in document
/// order — not a fixed page-number window — so original page numbers stay on
/// the <c>--- page N ---</c> markers. A group is split early when the next
/// page would exceed <c>maxChars</c>.
/// <para>
/// <c>overlap</c> repeats the last non-empty pages of a full group at the start
/// of the next group so a name cut by the group boundary is still visible.
/// Overlap is clamped to <c>pagesPerRequest - 1</c>. The final partial group
/// is not emitted again when it is only that carried suffix.
/// </para>
/// </summary>
public static class LlmPageGrouper
{
    public readonly record struct PageBatch(string Text, int[] PageNumbers);

    public static bool IsBlank(OcrPageResult page) =>
        string.IsNullOrWhiteSpace(page.Text);

    public static string FormatPage(OcrPageResult page)
    {
        string text = page.Text ?? "";
        return $"--- page {page.Page} ---\n{text}\n";
    }

    public static List<PageBatch> BuildGroups(
        IReadOnlyList<OcrPageResult> pages,
        int pagesPerRequest,
        int maxChars,
        int overlap = 0)
    {
        pagesPerRequest = Math.Max(1, pagesPerRequest);
        maxChars = Math.Max(1, maxChars);
        overlap = ClampOverlap(overlap, pagesPerRequest);
        List<PageBatch> batches = [];
        List<OcrPageResult> pending = [];
        int pendingChars = 0;
        foreach (OcrPageResult page in pages)
            Accept(page, pagesPerRequest, maxChars, overlap, pending, ref pendingChars, batches, nonEmpty: null);
        FlushRemainder(pending, batches);
        return batches;
    }

    /// <summary>
    /// Accepts pages as OCR finishes (any order) and emits a group as soon as
    /// the next <c>pagesPerRequest</c> non-empty pages in document order are known.
    /// </summary>
    public sealed class OrderedBuffer
    {
        private readonly OcrPageResult?[] _slots;
        private readonly int _pagesPerRequest;
        private readonly int _maxChars;
        private readonly int _overlap;
        private readonly List<OcrPageResult> _pending = [];
        private readonly List<OcrPageResult> _nonEmpty = [];
        private int _pendingChars;
        private int _next;

        public OrderedBuffer(int pageCount, int pagesPerRequest, int maxChars, int overlap = 0)
        {
            if (pageCount < 0)
                throw new ArgumentOutOfRangeException(nameof(pageCount));
            _slots = new OcrPageResult?[pageCount];
            _pagesPerRequest = Math.Max(1, pagesPerRequest);
            _maxChars = Math.Max(1, maxChars);
            _overlap = ClampOverlap(overlap, _pagesPerRequest);
        }

        public IReadOnlyList<OcrPageResult> NonEmptyPages => _nonEmpty;

        public List<PageBatch> Add(OcrPageResult page)
        {
            int index = page.Page - 1;
            if ((uint)index >= (uint)_slots.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(page),
                    $"Page {page.Page} is outside 1..{_slots.Length}.");
            }

            if (_slots[index] is not null)
                throw new InvalidOperationException($"Page {page.Page} was added twice.");

            _slots[index] = page;
            List<PageBatch> ready = [];
            Drain(ready);
            return ready;
        }

        /// <summary>
        /// Emits any trailing partial group. Pages that were never added are
        /// treated as blank so a hole cannot drop every later page.
        /// </summary>
        public List<PageBatch> FlushRemainder()
        {
            List<PageBatch> ready = [];
            while (_next < _slots.Length)
            {
                _slots[_next] ??= new OcrPageResult { Page = _next + 1, Text = "" };
                Drain(ready);
            }

            LlmPageGrouper.FlushRemainder(_pending, ready);
            _pendingChars = 0;
            return ready;
        }

        private void Drain(List<PageBatch> ready)
        {
            while (_next < _slots.Length && _slots[_next] is not null)
            {
                OcrPageResult page = _slots[_next]!;
                _slots[_next] = null;
                _next++;
                Accept(page, _pagesPerRequest, _maxChars, _overlap, _pending, ref _pendingChars, ready, _nonEmpty);
            }
        }
    }

    private static int ClampOverlap(int overlap, int pagesPerRequest) =>
        Math.Clamp(overlap, 0, Math.Max(0, pagesPerRequest - 1));

    private static void Accept(
        OcrPageResult page,
        int pagesPerRequest,
        int maxChars,
        int overlap,
        List<OcrPageResult> pending,
        ref int pendingChars,
        List<PageBatch> emitted,
        List<OcrPageResult>? nonEmpty)
    {
        if (IsBlank(page))
            return;

        nonEmpty?.Add(page);
        string chunk = FormatPage(page);
        if (pending.Count > 0 && pendingChars + chunk.Length > maxChars)
        {
            // A carried overlap page must not block the next page forever.
            FlushPending(pending, emitted, overlap: 0);
            pendingChars = 0;
        }

        if (pending.Count == 0 && chunk.Length > maxChars)
        {
            emitted.Add(new PageBatch(chunk[..maxChars], [page.Page]));
            return;
        }

        pending.Add(page);
        pendingChars += chunk.Length;
        if (pending.Count >= pagesPerRequest)
            pendingChars = FlushPending(pending, emitted, overlap);
    }

    /// <summary>
    /// Emits <paramref name="pending"/> and, when <paramref name="overlap"/> is
    /// positive, leaves that many trailing pages in <paramref name="pending"/>
    /// for the next group. Returns the character count of what remains.
    /// </summary>
    private static int FlushPending(List<OcrPageResult> pending, List<PageBatch> emitted, int overlap)
    {
        if (pending.Count == 0)
            return 0;

        StringBuilder sb = new();
        int[] nums = new int[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            nums[i] = pending[i].Page;
            sb.Append(FormatPage(pending[i]));
        }

        emitted.Add(new PageBatch(sb.ToString(), nums));

        if (overlap <= 0 || pending.Count <= overlap)
        {
            pending.Clear();
            return 0;
        }

        pending.RemoveRange(0, pending.Count - overlap);
        int chars = 0;
        for (int i = 0; i < pending.Count; i++)
            chars += FormatPage(pending[i]).Length;
        return chars;
    }

    private static void FlushRemainder(List<OcrPageResult> pending, List<PageBatch> emitted)
    {
        if (pending.Count == 0)
            return;
        if (AlreadyEmitted(emitted, pending))
        {
            pending.Clear();
            return;
        }

        FlushPending(pending, emitted, overlap: 0);
    }

    private static bool AlreadyEmitted(List<PageBatch> emitted, List<OcrPageResult> pending)
    {
        if (emitted.Count == 0 || pending.Count == 0)
            return false;
        int[] last = emitted[^1].PageNumbers;
        if (pending.Count > last.Length)
            return false;
        int offset = last.Length - pending.Count;
        for (int i = 0; i < pending.Count; i++)
        {
            if (last[offset + i] != pending[i].Page)
                return false;
        }

        return true;
    }
}
