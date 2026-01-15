using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Extensions;

public static class TaskExtensions
{
    public static ValueTask AsValueTask(this Task task) => new(task);

    public static Task<T?> OrNullIfException<T>(this Task<T> task) where T : struct
    {
        return task.ContinueWith(t => t.IsCompletedSuccessfully ? (T?)t.Result : null);
    }

    public static Task<T?> OrNullIfExceptionClass<T>(this Task<T> task) where T : class
    {
        return task.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
    }
}
