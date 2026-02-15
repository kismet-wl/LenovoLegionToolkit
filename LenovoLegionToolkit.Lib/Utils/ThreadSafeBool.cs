using System.Threading;

namespace LenovoLegionToolkit.Lib.Utils;

public class ThreadSafeBool
{
    private int _threadSafeBoolBackValue;

    public bool Value
    {
        get => Interlocked.CompareExchange(ref _threadSafeBoolBackValue, 1, 1) == 1;
        set => Interlocked.Exchange(ref _threadSafeBoolBackValue, value ? 1 : 0);
    }
}
