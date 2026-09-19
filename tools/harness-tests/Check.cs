#nullable enable

public static class Check
{
    public static void True(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Expected condition to be true.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}>, got <{actual}>.");
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(T).Name}, got {ex.GetType().Name}.", ex);
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name} to be thrown.");
    }
}
