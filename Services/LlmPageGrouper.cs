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
        int maxChars)
    {
        pagesPerRequest = Math.Max(1, pagesPerRequest);
        maxChars = Math.Max(1, maxChars);
        List<PageBatch> batches = [];
        List<OcrPageResult> pending = [];
        int pendingChars = 0;
        foreach (OcrPageResult page in pages)
            Accept(page, pagesPerRequest, maxChars, pending, ref pendingChars, batches, nonEmpty: null);
        FlushPending(pending, batches);
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
        private readonly List<OcrPageResult> _pending = [];
        private readonly List<OcrPageResult> _nonEmpty = [];
        private int _pendingChars;
        private int _next;

        public OrderedBuffer(int pageCount, int pagesPerRequest, int maxChars)
        {
            if (pageCount < 0)
                throw new ArgumentOutOfRangeException(nameof(pageCount));
            _slots = new OcrPageResult?[pageCount];
            _pagesPerRequest = Math.Max(1, pagesPerRequest);
            _maxChars = Math.Max(1, maxChars);
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

            FlushPending(_pending, ready);
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
                Accept(page, _pagesPerRequest, _maxChars, _pending, ref _pendingChars, ready, _nonEmpty);
            }
        }
    }

    private static void Accept(
        OcrPageResult page,
        int pagesPerRequest,
        int maxChars,
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
            FlushPending(pending, emitted);
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
        {
            FlushPending(pending, emitted);
            pendingChars = 0;
        }
    }

    private static void FlushPending(List<OcrPageResult> pending, List<PageBatch> emitted)
    {
        if (pending.Count == 0)
            return;

        StringBuilder sb = new();
        int[] nums = new int[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            nums[i] = pending[i].Page;
            sb.Append(FormatPage(pending[i]));
        }

        emitted.Add(new PageBatch(sb.ToString(), nums));
        pending.Clear();
    }
}
