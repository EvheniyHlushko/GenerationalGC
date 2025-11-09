namespace GenerationalGC.Runtime.Core;

/// <summary>
///     Per-segment card table. 1 byte per card; 0 = clean, 1 = dirty.
///     CardSizeBytes is typically small (e.g., 256).
/// </summary>
public sealed class CardTable
{
    private readonly byte[] _cards; // index by (segmentOffset / CardSizeBytes)

    public CardTable(int segmentSizeBytes, int cardSizeBytes)
    {
        CardSizeBytes = Math.Max(64, cardSizeBytes);
        var count = (segmentSizeBytes + CardSizeBytes - 1) / CardSizeBytes; // ceil
        _cards = new byte[count];
    }

    public int CardSizeBytes { get; }

    public void MarkDirtyByOffset(int segmentRelativeOffset)
    {
        var index = segmentRelativeOffset / CardSizeBytes;
        if ((uint)index < (uint)_cards.Length)
        {
            _cards[index] = 1;
            Console.WriteLine($"Card {index + 1} marked as dirty");
        }

    }

    public IEnumerable<(int start, int end)> DirtyRanges()
    {
        for (var i = 0; i < _cards.Length; i++)
        {
            if (_cards[i] == 0) continue;
            var start = i * CardSizeBytes;
            var end = Math.Min(start + CardSizeBytes, _cards.Length * CardSizeBytes);
            yield return (start, end);
        }
    }

    public int CountDirty()
    {
        return _cards.Count(t => t != 0);
    }

    public void ClearAll()
    {
        Array.Clear(_cards, 0, _cards.Length);
    }
}