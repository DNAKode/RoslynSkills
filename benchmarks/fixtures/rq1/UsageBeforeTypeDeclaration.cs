namespace Rq1.Fixtures;

public static class Startup
{
    public static object Build()
    {
        return new Cache();
    }
}

public sealed class Cache
{
}
